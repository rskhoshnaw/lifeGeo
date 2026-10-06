import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, type Tenant, type TenantAuditEntry } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const emptyTenant = { code: '', name: '', managerEmail: '', managerPassword: '' }
const when = (iso: string) => new Date(iso).toISOString().replace('T', ' ').slice(0, 16)

// Companies (plan step 3.1): only the Super Admin of the platform company. Creating, renaming and switching a company off or on need a written reason where the
// server asks for one; reading another company's audit log needs a reason that is recorded in the platform audit, shown below.
export function PlatformPage({ admin }: { admin: AdminApi }) {
  const tenants = useLoad(useCallback(() => admin.platformTenants(), [admin]))
  const log = useLoad(useCallback(() => admin.platformAudit(), [admin]))
  const reloadAll = useCallback(() => {
    tenants.reload()
    log.reload()
  }, [tenants, log])
  const action = useAction(reloadAll)
  const [form, setForm] = useState(emptyTenant)
  const [company, setCompany] = useState('')
  const [reason, setReason] = useState('')
  const [actionFilter, setActionFilter] = useState('')
  const [entries, setEntries] = useState<TenantAuditEntry[] | null>(null)

  async function add(event: FormEvent) {
    event.preventDefault()
    const done = await action.run(async () => (await admin.createTenant({ ...form, code: form.code.trim(), name: form.name.trim(), managerEmail: form.managerEmail.trim() }), true))
    if (done) setForm(emptyTenant)
  }

  function rename(tenant: Tenant) {
    const name = window.prompt(t('web.platform.renamePrompt'), tenant.name)?.trim()
    const why = name && name !== tenant.name ? window.prompt(t('web.platform.reasonPrompt'))?.trim() : undefined
    if (name && why) void action.run(() => admin.updateTenant(tenant.id, { name, reason: why }))
  }

  function toggle(tenant: Tenant) {
    const why = window.prompt(t('web.platform.reasonPrompt'))?.trim()
    if (why) void action.run(() => admin.updateTenant(tenant.id, { isActive: !tenant.isActive, reason: why }))
  }

  async function read(event: FormEvent) {
    event.preventDefault()
    const result = await action.run(() => admin.tenantAudit(company, reason.trim(), actionFilter.trim()))
    if (result) setEntries(result)
  }

  return (
    <section className="space-y-8 p-6">
      <h2 className="text-xl font-semibold">{t('web.platform.title')}</h2>
      {(tenants.error || log.error || action.error) && <p role="alert" className="text-sm text-red-700">{tenants.error ?? log.error ?? action.error}</p>}

      <form className="grid max-w-3xl grid-cols-2 gap-3 rounded border p-4" onSubmit={(e) => void add(e)}>
        <h3 className="col-span-2 font-medium">{t('web.platform.create')}</h3>
        <label className="text-sm">
          {t('web.platform.code')}
          <input className="mt-1 block w-full rounded border px-2 py-1" value={form.code} required pattern="[a-z0-9][a-z0-9\-]{1,31}" maxLength={32} onChange={(e) => setForm({ ...form, code: e.target.value })} />
        </label>
        <label className="text-sm">
          {t('web.platform.name')}
          <input className="mt-1 block w-full rounded border px-2 py-1" value={form.name} required maxLength={100} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </label>
        <label className="text-sm">
          {t('web.platform.managerEmail')}
          <input type="email" className="mt-1 block w-full rounded border px-2 py-1" value={form.managerEmail} required maxLength={200} onChange={(e) => setForm({ ...form, managerEmail: e.target.value })} />
        </label>
        <label className="text-sm">
          {t('web.platform.managerPassword')}
          <input type="password" autoComplete="new-password" className="mt-1 block w-full rounded border px-2 py-1" value={form.managerPassword} required minLength={12} maxLength={200} onChange={(e) => setForm({ ...form, managerPassword: e.target.value })} />
        </label>
        <div className="col-span-2 text-end">
          <button className="rounded bg-slate-900 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy}>
            {t('web.platform.add')}
          </button>
        </div>
      </form>

      {tenants.data && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.platform.code')}</th>
              <th>{t('web.platform.name')}</th>
              <th>{t('web.platform.status')}</th>
              <th>{t('web.platform.users')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {tenants.data.map((x) => (
              <tr key={x.id} className="border-b">
                <td className="py-2">{x.code}</td>
                <td>{x.name}</td>
                <td>{x.isActive ? t('web.platform.active') : t('web.platform.inactive')}</td>
                <td>
                  {x.activeUsers} / {x.users}
                </td>
                <td className="space-x-2 py-2 text-end">
                  <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => rename(x)}>
                    {t('web.platform.rename')}
                  </button>
                  {x.code !== 'platform' && (
                    <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => toggle(x)}>
                      {x.isActive ? t('web.platform.deactivate') : t('web.platform.activate')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <form className="grid max-w-3xl grid-cols-2 gap-3 rounded border p-4" onSubmit={(e) => void read(e)}>
        <h3 className="col-span-2 font-medium">{t('web.platform.audit.title')}</h3>
        <label className="text-sm">
          {t('web.platform.audit.company')}
          <select className="mt-1 block w-full rounded border px-2 py-1" value={company} required onChange={(e) => setCompany(e.target.value)}>
            <option value="" />
            {(tenants.data ?? []).map((x) => (
              <option key={x.id} value={x.id}>
                {x.code} · {x.name}
              </option>
            ))}
          </select>
        </label>
        <label className="text-sm">
          {t('web.platform.audit.action')}
          <input className="mt-1 block w-full rounded border px-2 py-1" value={actionFilter} maxLength={100} onChange={(e) => setActionFilter(e.target.value)} />
        </label>
        <label className="col-span-2 text-sm">
          {t('web.platform.audit.reason')}
          <input className="mt-1 block w-full rounded border px-2 py-1" value={reason} required minLength={10} maxLength={500} onChange={(e) => setReason(e.target.value)} />
        </label>
        <div className="col-span-2 text-end">
          <button className="rounded bg-slate-900 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || !company || reason.trim().length < 10}>
            {t('web.platform.audit.read')}
          </button>
        </div>
      </form>
      {entries && (
        <div>
          {entries.length === 0 && <p className="text-slate-600">{t('web.platform.audit.empty')}</p>}
          {entries.length > 0 && (
            <table className="w-full text-start text-sm">
              <thead>
                <tr className="border-b">
                  <th className="py-2">{t('web.platform.audit.time')}</th>
                  <th>{t('web.platform.audit.actor')}</th>
                  <th>{t('web.platform.audit.action.col')}</th>
                  <th>{t('web.platform.audit.entity')}</th>
                </tr>
              </thead>
              <tbody>
                {entries.map((e) => (
                  <tr key={e.id} className="border-b">
                    <td className="py-1">{when(e.occurredAt)}</td>
                    <td>{e.actorEmail ?? e.actorId}</td>
                    <td>{e.action}</td>
                    <td>{e.entityId}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      <div>
        <h3 className="mb-2 font-medium">{t('web.platform.log.title')}</h3>
        {log.data?.length === 0 && <p className="text-slate-600">{t('web.platform.audit.empty')}</p>}
        {log.data && log.data.length > 0 && (
          <table className="w-full text-start text-sm">
            <thead>
              <tr className="border-b">
                <th className="py-2">{t('web.platform.audit.time')}</th>
                <th>{t('web.platform.audit.actor')}</th>
                <th>{t('web.platform.audit.action.col')}</th>
                <th>{t('web.platform.log.company')}</th>
                <th>{t('web.platform.log.reason')}</th>
              </tr>
            </thead>
            <tbody>
              {log.data.map((e) => (
                <tr key={e.id} className="border-b">
                  <td className="py-1">{when(e.occurredAt)}</td>
                  <td>{e.actorEmail ?? e.actorId}</td>
                  <td>{e.action}</td>
                  <td>{e.targetTenantCode ?? ''}</td>
                  <td>{e.reason ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  )
}
