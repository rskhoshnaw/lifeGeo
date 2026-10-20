import { useCallback, useState } from 'react'
import { AdminApi, type PermissionInfo, type Role, type RoleInput } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

const scopes = ['tenant', 'region', 'own'] as const

// Roles (plan step 4.38): the list, and a form to make a role, change its reach and permissions, or delete it. Default roles keep their name and cannot be deleted.
export function RolesPanel({ admin }: { admin: AdminApi }) {
  const roles = useLoad(useCallback(() => admin.roles(), [admin]))
  const permissions = useLoad(useCallback(() => admin.permissions(), [admin]))
  const [editing, setEditing] = useState<Role | 'new' | null>(null)
  const action = useAction(() => {
    roles.reload()
    setEditing(null)
  })

  function remove(role: Role) {
    if (window.confirm(t('web.roles.confirmDelete', { role: role.name }))) void action.run(() => admin.deleteRole(role.id))
  }

  return (
    <section className="mt-8 space-y-3">
      <div className="flex items-center gap-3">
        <h3 className="text-lg font-semibold">{t('web.roles.title')}</h3>
        <button className="rounded border px-3 py-1 text-sm" onClick={() => setEditing('new')}>
          {t('web.roles.new')}
        </button>
      </div>
      {(roles.error ?? action.error) && <p role="alert" className="text-sm text-red-700">{action.error ?? roles.error}</p>}
      {roles.data && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.roles.name')}</th>
              <th>{t('web.roles.scope')}</th>
              <th>{t('web.roles.users')}</th>
              <th>{t('web.roles.permissions')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {roles.data.map((role) => (
              <tr key={role.id} className="border-b">
                <td className="py-2">
                  {role.name} {role.isDefault && <span className="text-slate-500">({t('web.roles.default')})</span>}
                </td>
                <td>{t(`web.roles.scope.${role.dataScope}` as never)}</td>
                <td>{role.userCount ?? 0}</td>
                <td>{role.permissions?.length ?? 0}</td>
                <td className="space-x-2 text-end">
                  <button className="rounded border px-2 py-1" aria-label={t('web.roles.edit', { role: role.name })} onClick={() => setEditing(role)}>
                    {t('web.places.edit')}
                  </button>
                  {!role.isDefault && (
                    <button className="rounded border px-2 py-1" aria-label={t('web.roles.delete', { role: role.name })} disabled={action.busy || (role.userCount ?? 0) > 0} onClick={() => remove(role)}>
                      {t('web.roles.deleteAction')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && permissions.data && (
        <RoleForm
          key={editing === 'new' ? 'new' : editing.id}
          role={editing === 'new' ? null : editing}
          permissions={permissions.data}
          busy={action.busy}
          onSave={(input) => void action.run(() => (editing === 'new' ? admin.createRole(input) : admin.updateRole(editing.id, input)))}
          onClose={() => setEditing(null)}
        />
      )}
    </section>
  )
}

export function RoleForm({ role, permissions, busy, onSave, onClose }: { role: Role | null; permissions: PermissionInfo[]; busy: boolean; onSave: (input: RoleInput) => void; onClose: () => void }) {
  const [name, setName] = useState(role?.name ?? '')
  const [scope, setScope] = useState<RoleInput['dataScope']>((role?.dataScope as RoleInput['dataScope']) ?? 'own')
  const [binding, setBinding] = useState(role?.requiresDeviceBinding ?? false)
  const [chosen, setChosen] = useState<Set<string>>(new Set(role?.permissions ?? []))
  const groups = [...new Set(permissions.map((p) => p.group))]

  function toggle(key: string) {
    const next = new Set(chosen)
    if (!next.delete(key)) next.add(key)
    setChosen(next)
  }

  return (
    <form
      className="space-y-3 rounded border p-4 text-sm"
      onSubmit={(e) => {
        e.preventDefault()
        onSave({ name: name.trim(), dataScope: scope, requiresDeviceBinding: binding, permissions: [...chosen] })
      }}
    >
      <h4 className="font-semibold">{role ? t('web.roles.edit', { role: role.name }) : t('web.roles.new')}</h4>
      <div className="flex flex-wrap items-center gap-3">
        <input aria-label={t('web.roles.name')} placeholder={t('web.roles.name')} className="rounded border px-2 py-1" maxLength={60} disabled={!!role?.isDefault} value={name} onChange={(e) => setName(e.target.value)} />
        <select aria-label={t('web.roles.scope')} className="rounded border px-2 py-1" value={scope} onChange={(e) => setScope(e.target.value as RoleInput['dataScope'])}>
          {scopes.map((s) => (
            <option key={s} value={s}>
              {t(`web.roles.scope.${s}` as never)}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={binding} onChange={(e) => setBinding(e.target.checked)} />
          {t('web.roles.binding')}
        </label>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {groups.map((group) => (
          <fieldset key={group} className="rounded border p-2">
            <legend className="px-1 font-medium">{group}</legend>
            {permissions.filter((p) => p.group === group).map((p) => (
              <label key={p.key} className="flex items-center gap-1">
                <input type="checkbox" checked={chosen.has(p.key)} onChange={() => toggle(p.key)} />
                {p.key}
              </label>
            ))}
          </fieldset>
        ))}
      </div>
      <div className="space-x-2">
        <button type="submit" className="rounded border px-3 py-1" disabled={busy || !name.trim()}>
          {t('web.places.save')}
        </button>
        <button type="button" className="rounded border px-3 py-1" onClick={onClose}>
          {t('web.customers.closeHours')}
        </button>
      </div>
    </form>
  )
}
