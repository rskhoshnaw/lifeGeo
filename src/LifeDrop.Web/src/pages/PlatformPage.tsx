import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, companyModules, type Tenant, type TenantAuditEntry } from '../api/admin.ts'
import { formatWording, parseWording } from '../components/companyProfile.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { appLink, panelLink, qrSvg } from '../components/inviteLinks.ts'

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
  const [inviting, setInviting] = useState<string | null>(null)
  const [profileFor, setProfileFor] = useState<string | null>(null)

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
          <button className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy}>
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
                  <button className="rounded border px-2 py-1" aria-expanded={profileFor === x.id} onClick={() => setProfileFor(profileFor === x.id ? null : x.id)}>
                    {t('web.platform.profile')}
                  </button>
                  {x.code !== 'platform' && (
                    <button className="rounded border px-2 py-1" aria-expanded={inviting === x.id} onClick={() => setInviting(inviting === x.id ? null : x.id)}>
                      {t('web.platform.invite')}
                    </button>
                  )}
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
      {tenants.data?.filter((x) => x.id === profileFor).map((x) => <ProfileEditor key={x.id} admin={admin} tenant={x} onSaved={() => { setProfileFor(null); reloadAll() }} />)}
      {tenants.data?.filter((x) => x.id === inviting).map((x) => <InviteCard key={x.id} code={x.code} />)}

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
          <button className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || !company || reason.trim().length < 10}>
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

// The links of one company: the panel link (opens the sign-in with the code filled) and the app link, which is also shown as a QR code.
function InviteCard({ code }: { code: string }) {
  const panel = panelLink(window.location.origin, code)
  const app = appLink(code)
  const [copied, setCopied] = useState('')
  const copy = (text: string) => void navigator.clipboard?.writeText(text).then(() => setCopied(text), () => {})
  return (
    <div className="flex max-w-3xl flex-wrap items-start gap-6 rounded border p-4 text-sm" aria-label={t('web.platform.invite')}>
      <div className="min-w-0 flex-1 space-y-3">
        <h3 className="font-medium">{t('web.platform.inviteTitle', { code })}</h3>
        {[[t('web.platform.invitePanel'), panel], [t('web.platform.inviteApp'), app]].map(([label, link]) => (
          <div key={link}>
            <p className="text-slate-600">{label}</p>
            <div className="mt-1 flex items-center gap-2">
              <code dir="ltr" className="min-w-0 flex-1 break-all rounded bg-slate-100 px-2 py-1">{link}</code>
              <button className="rounded border px-2 py-1" onClick={() => copy(link!)}>{copied === link ? t('web.platform.copied') : t('web.platform.copy')}</button>
            </div>
          </div>
        ))}
        <p className="text-slate-600">{t('web.platform.inviteNote')}</p>
      </div>
      <div className="w-40 shrink-0" role="img" aria-label={t('web.platform.inviteQr', { code })} dangerouslySetInnerHTML={{ __html: qrSvg(app) }} />
    </div>
  )
}

// The profile of one company (plan step 4.48): the wording profile, the parts it does not use and its own wording. Saving replaces the whole profile and needs a written reason.
function ProfileEditor({ admin, tenant, onSaved }: { admin: AdminApi; tenant: Tenant; onSaved: () => void }) {
  const [industry, setIndustry] = useState(tenant.industry ?? '')
  const [hidden, setHidden] = useState<string[]>(tenant.hiddenModules ?? [])
  const [wording, setWording] = useState(formatWording(tenant.textOverrides ?? {}))
  const [reason, setReason] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const action = useAction(onSaved)

  function toggle(module: string) {
    setHidden(hidden.includes(module) ? hidden.filter((m) => m !== module) : [...hidden, module])
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    const parsed = parseWording(wording)
    if ('badLine' in parsed) return setProblem(t('web.platform.badWording', { line: parsed.badLine }))
    setProblem(null)
    void action.run(() => admin.updateTenantProfile(tenant.id, { industry: industry || null, hiddenModules: hidden, textOverrides: parsed.wording, reason: reason.trim() }))
  }

  return (
    <form onSubmit={submit} className="max-w-3xl space-y-3 rounded border p-4 text-sm">
      <h3 className="font-medium">{t('web.platform.profileOf', { company: tenant.code })}</h3>
      {(problem ?? action.error) && <p role="alert" className="text-red-700">{problem ?? action.error}</p>}
      <label className="block">
        {t('web.platform.industry')}
        <select className="mt-1 block rounded border px-2 py-1" value={industry} onChange={(e) => setIndustry(e.target.value)}>
          <option value="">{t('web.platform.industryDefault')}</option>
          <option value="pharma">{t('web.platform.industryPharma')}</option>
          <option value="food">{t('web.platform.industryFood')}</option>
        </select>
      </label>
      <fieldset className="rounded border p-2">
        <legend className="px-1">{t('web.platform.hiddenModules')}</legend>
        <div className="flex flex-wrap gap-4">
          {companyModules.map((module) => (
            <label key={module} className="flex items-center gap-1">
              <input type="checkbox" checked={hidden.includes(module)} onChange={() => toggle(module)} />
              {t(`web.platform.module.${module}` as never)}
            </label>
          ))}
        </div>
      </fieldset>
      <label className="block">
        {t('web.platform.wording')}
        <textarea className="mt-1 block h-32 w-full rounded border px-2 py-1 font-mono" dir="ltr" value={wording} onChange={(e) => setWording(e.target.value)} placeholder="app.home.customers | en | My shops" />
      </label>
      <label className="block">
        {t('web.platform.reason')}
        <input className="mt-1 block w-full rounded border px-2 py-1" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} />
      </label>
      <button type="submit" className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || reason.trim().length < 5}>
        {t('web.places.save')}
      </button>
    </form>
  )
}
