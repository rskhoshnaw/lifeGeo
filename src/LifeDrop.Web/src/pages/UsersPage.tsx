import { useCallback, useState } from 'react'
import { AdminApi, can, Permissions, type Me, type Role, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

export function UsersPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const users = useLoad(useCallback(() => admin.users(), [admin]))
  const canKill = can(me, ...Permissions.killSessions)
  const canAssign = can(me, ...Permissions.assignRoles)
  const roles = useLoad(useCallback(() => (canAssign ? admin.roles() : Promise.resolve<Role[]>([])), [admin, canAssign]))
  const action = useAction(users.reload)

  function kill(user: UserSummary) {
    if (!window.confirm(t('web.users.confirmEnd', { user: user.email ?? user.id }))) return
    void action.run(() => admin.killSessions(user.id))
  }

  return (
    <section className="p-6">
      <h2 className="mb-4 text-xl font-semibold">{t('web.users.title')}</h2>
      {(users.error ?? action.error) && <p role="alert" className="mb-3 text-sm text-red-700">{action.error ?? users.error}</p>}
      {users.data && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.users.email')}</th>
              <th>{t('web.users.roles')}</th>
              <th>{t('web.users.team')}</th>
              <th>{t('web.users.sessions')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {users.data.map((user) => (
              <tr key={user.id} className="border-b align-top">
                <td className="py-2">{user.email ?? user.id}</td>
                <td>{user.roles.join(', ') || '—'}</td>
                <td>{user.teamName ?? '—'}</td>
                <td>{user.activeSessions}</td>
                <td className="space-x-2 py-2 text-end">
                  {canAssign && roles.data && (
                    <AssignRole user={user} roles={roles.data} busy={action.busy} onAssign={(roleId) => void action.run(() => admin.assignRole(user.id, roleId))} />
                  )}
                  {canKill && user.activeSessions > 0 && user.id !== me.id && (
                    <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => kill(user)}>
                      {t('web.users.endSessions')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
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
