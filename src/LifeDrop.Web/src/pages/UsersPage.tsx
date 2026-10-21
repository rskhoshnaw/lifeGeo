import { Fragment, useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, Permissions, type Me, type Role, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { RolesPanel } from '../components/RolesPanel.tsx'
import { SortTh, useSort } from '../components/sort.tsx'

export function UsersPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [showInactive, setShowInactive] = useState(false)
  const users = useLoad(useCallback(() => admin.users(showInactive), [admin, showInactive]))
  const canKill = can(me, ...Permissions.killSessions)
  const canAssign = can(me, ...Permissions.assignRoles)
  const roles = useLoad(useCallback(() => (canAssign ? admin.roles() : Promise.resolve<Role[]>([])), [admin, canAssign]))
  const action = useAction(users.reload)
  const sorted = useSort(users.data, { email: (u) => u.email ?? u.id, roles: (u) => u.roles.join(', '), team: (u) => u.teamName, city: (u) => u.city, phone: (u) => u.phoneType, sessions: (u) => u.activeSessions, status: (u) => u.isActive })

  const [editing, setEditing] = useState<string | null>(null)
  const [form, setForm] = useState({ email: '', password: '', roleId: '' })
  const [created, setCreated] = useState(false)

  async function add(event: FormEvent) {
    event.preventDefault()
    setCreated(false)
    const done = await action.run(async () => (await admin.createUser({ email: form.email.trim(), password: form.password, roleId: form.roleId }), true))
    if (done) {
      setForm({ email: '', password: '', roleId: '' })
      setCreated(true)
    }
  }

  function kill(user: UserSummary) {
    if (!window.confirm(t('web.users.confirmEnd', { user: user.email ?? user.id }))) return
    void action.run(() => admin.killSessions(user.id))
  }

  function changeEmail(user: UserSummary) {
    const email = window.prompt(t('web.users.newEmail', { user: user.email ?? user.id }), user.email ?? '')?.trim()
    if (email) void action.run(() => admin.changeUserEmail(user.id, email))
  }

  function setPassword(user: UserSummary) {
    const password = window.prompt(t('web.users.passwordPrompt', { user: user.email ?? user.id }))
    if (password) void action.run(() => admin.setUserPassword(user.id, password))
  }

  function deactivate(user: UserSummary) {
    if (!window.confirm(t('web.users.confirmDeactivate', { user: user.email ?? user.id }))) return
    void action.run(() => admin.deactivateUser(user.id))
  }

  return (
    <section className="p-6">
      <h2 className="mb-4 text-xl font-semibold">{t('web.users.title')}</h2>
      {(users.error ?? action.error) && <p role="alert" className="mb-3 text-sm text-red-700">{action.error ?? users.error}</p>}
      {canAssign && roles.data && roles.data.length > 0 && (
        <form className="mb-6 grid max-w-3xl grid-cols-2 gap-3 rounded border p-4" onSubmit={(e) => void add(e)}>
          <h3 className="col-span-2 font-medium">{t('web.users.new')}</h3>
          <label className="text-sm">
            {t('web.users.email')}
            <input type="email" autoComplete="off" className="mt-1 block w-full rounded border px-2 py-1" value={form.email} required maxLength={200} onChange={(e) => setForm({ ...form, email: e.target.value })} />
          </label>
          <label className="text-sm">
            {t('web.users.newPassword')}
            <input type="password" autoComplete="new-password" className="mt-1 block w-full rounded border px-2 py-1" value={form.password} required minLength={12} maxLength={200} onChange={(e) => setForm({ ...form, password: e.target.value })} />
          </label>
          <label className="text-sm">
            {t('web.users.newRole')}
            <select className="mt-1 block w-full rounded border px-2 py-1" value={form.roleId} required onChange={(e) => setForm({ ...form, roleId: e.target.value })}>
              <option value="">{t('web.users.chooseRole')}</option>
              {roles.data.map((role) => (
                <option key={role.id} value={role.id}>
                  {role.name}
                </option>
              ))}
            </select>
          </label>
          <div className="self-end text-end">
            <button className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy}>
              {t('web.users.create')}
            </button>
          </div>
          {created && <p role="status" className="col-span-2 text-sm text-green-700">{t('web.users.created')}</p>}
        </form>
      )}
      {canAssign && (
        <label className="mb-3 flex items-center gap-2 text-sm">
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          {t('web.users.showInactive')}
        </label>
      )}
      {users.data?.length === 0 && <p className="text-slate-600">{t('web.users.none')}</p>}
      {users.data && users.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <SortTh id="email" className="py-2" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.email')}</SortTh>
              <SortTh id="roles" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.roles')}</SortTh>
              <SortTh id="team" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.team')}</SortTh>
              <SortTh id="city" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.city')}</SortTh>
              <SortTh id="phone" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.phone')}</SortTh>
              <SortTh id="sessions" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.sessions')}</SortTh>
              <SortTh id="status" sort={sorted.sort} onSort={sorted.toggle}>{t('web.users.status')}</SortTh>
              <th />
            </tr>
          </thead>
          <tbody>
            {(sorted.rows ?? users.data).map((user) => (
              <Fragment key={user.id}>
              <tr className="border-b align-top">
                <td className="py-2">
                  {canAssign ? (
                    <button className="text-brand-700 underline" aria-label={t('web.users.edit', { user: user.email ?? user.id })} onClick={() => setEditing(editing === user.id ? null : user.id)}>
                      {user.email ?? user.id}
                    </button>
                  ) : (
                    (user.email ?? user.id)
                  )}
                </td>
                <td>{user.roles.join(', ') || '—'}</td>
                <td>{user.teamName ?? '—'}</td>
                <td>{user.city ?? '—'}</td>
                <td>{user.phoneType ? (user.phoneType === 'ios' ? 'iPhone' : user.phoneType === 'android' ? 'Android' : user.phoneType) : '—'}</td>
                <td>{user.activeSessions}</td>
                <td>{user.isActive ? t('web.users.active') : t('web.users.inactive')}</td>
                <td className="space-x-2 py-2 text-end">
                  {canAssign && user.isActive && (
                    <>
                      <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => changeEmail(user)}>
                        {t('web.users.changeEmail')}
                      </button>
                      <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => setPassword(user)}>
                        {t('web.users.setPassword')}
                      </button>
                    </>
                  )}
                  {canAssign && user.id !== me.id && (
                    <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => (user.isActive ? deactivate(user) : void action.run(() => admin.activateUser(user.id)))}>
                      {user.isActive ? t('web.users.deactivate') : t('web.users.activate')}
                    </button>
                  )}
                  {canAssign && user.isActive && roles.data && (
                    <AssignRole user={user} roles={roles.data} busy={action.busy} onAssign={(roleId) => void action.run(() => admin.assignRole(user.id, roleId))} />
                  )}
                  {canKill && user.activeSessions > 0 && user.id !== me.id && (
                    <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => kill(user)}>
                      {t('web.users.endSessions')}
                    </button>
                  )}
                </td>
              </tr>
              {canAssign && editing === user.id && roles.data && (
                <tr className="border-b bg-slate-50">
                  <td colSpan={8} className="p-2">
                    <UserEditor user={user} roles={roles.data} self={user.id === me.id} admin={admin} onClose={() => setEditing(null)} onChanged={users.reload} />
                  </td>
                </tr>
              )}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
      {canAssign && <RolesPanel admin={admin} />}
    </section>
  )
}

// One user in detail (plan step 4.37): city, and the roles with a button to take each away.
export function UserEditor({ user, roles, self, admin, onClose, onChanged }: { user: UserSummary; roles: Role[]; self: boolean; admin: AdminApi; onClose: () => void; onChanged: () => void }) {
  const [city, setCity] = useState(user.city ?? '')
  const [email, setEmail] = useState(user.email ?? '')
  const [password, setPassword] = useState('')
  const action = useAction(onChanged)
  const own = roles.filter((role) => user.roles.includes(role.name))
  return (
    <div className="mt-4 max-w-3xl space-y-2 rounded border p-4 text-sm">
      <h3 className="font-semibold">{user.email ?? user.id}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {user.isActive && (
        <>
          <div className="flex flex-wrap items-center gap-2">
            <input type="email" aria-label={t('web.users.email')} placeholder={t('web.users.email')} className="rounded border px-2 py-1" maxLength={200} value={email} onChange={(e) => setEmail(e.target.value)} />
            <button className="rounded border px-2 py-1" disabled={action.busy || !email.trim() || email.trim() === user.email} onClick={() => void action.run(() => admin.changeUserEmail(user.id, email.trim()))}>
              {t('web.users.changeEmail')}
            </button>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <input type="password" autoComplete="new-password" aria-label={t('web.users.newPassword')} placeholder={t('web.users.newPassword')} className="rounded border px-2 py-1" minLength={12} maxLength={200} value={password} onChange={(e) => setPassword(e.target.value)} />
            <button
              className="rounded border px-2 py-1"
              disabled={action.busy || password.length < 12}
              onClick={() => void action.run(async () => { await admin.setUserPassword(user.id, password); setPassword('') })}
            >
              {t('web.users.setPassword')}
            </button>
          </div>
        </>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <input aria-label={t('web.users.city')} placeholder={t('web.users.city')} className="rounded border px-2 py-1" maxLength={80} value={city} onChange={(e) => setCity(e.target.value)} />
        <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.setUserCity(user.id, city.trim() || null))}>
          {t('web.places.save')}
        </button>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{t('web.users.roles')}:</span>
        {own.length === 0 && <span>—</span>}
        {own.map((role) => (
          <span key={role.id} className="inline-flex items-center gap-1 rounded border px-2 py-0.5">
            {role.name}
            {!self && (
              <button className="text-red-700" aria-label={t('web.users.removeRole', { role: role.name })} disabled={action.busy} onClick={() => void action.run(() => admin.removeUserRole(user.id, role.id))}>
                ✕
              </button>
            )}
          </span>
        ))}
      </div>
      {user.isActive && (
        <div className="flex flex-wrap items-center gap-2">
          <AssignRole user={user} roles={roles} busy={action.busy} onAssign={(roleId) => void action.run(() => admin.assignRole(user.id, roleId))} />
        </div>
      )}
      <button className="rounded border px-2 py-1" onClick={onClose}>
        {t('web.customers.closeHours')}
      </button>
    </div>
  )
}

function AssignRole({ user, roles, busy, onAssign }: { user: UserSummary; roles: Role[]; busy: boolean; onAssign: (roleId: string) => void }) {
  const available = roles.filter((role) => !user.roles.includes(role.name))
  const [roleId, setRoleId] = useState('')
  if (available.length === 0) return null
  return (
    <>
      <select aria-label={t('web.users.roleFor', { user: user.email ?? user.id })} className="rounded border px-1 py-1" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
        <option value="">{t('web.users.addRole')}</option>
        {available.map((role) => (
          <option key={role.id} value={role.id}>
            {role.name}
          </option>
        ))}
      </select>
      <button className="rounded border px-2 py-1" disabled={busy || !roleId} onClick={() => onAssign(roleId)}>
        {t('web.action.assign')}
      </button>
    </>
  )
}
