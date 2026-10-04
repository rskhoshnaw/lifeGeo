import { useEffect, useState, type FormEvent } from 'react'
import { t } from '../i18n.ts'
import { auditPageSize, errorMessage, type AdminApi, type AuditEntry, type AuditFilter } from '../api/admin.ts'

// Actions written by the server today; the field also accepts any other text.
const knownActions = [
  'auth.device_rejected', 'auth.refresh_replay', 'auth.refresh_rotated', 'auth.session_issued', 'auth.session_revoked', 'auth.sessions_killed',
  'customer.approved', 'customer.created', 'customer.created_pending', 'customer.geofence_changed', 'customer.location_changed', 'customer.rejected',
  'device.approved', 'device.blocked', 'device.registered', 'region.created', 'shift.ended', 'shift.started',
  'team.created', 'team.member_assigned', 'team.member_removed', 'team.region_changed', 'user.role_assigned',
  'visit.arrived', 'visit.cancelled', 'visit.checked_in', 'visit.checked_out', 'visit.created', 'visit.created_unplanned', 'visit.en_route',
  'visit.failed', 'visit.offline_review_approved', 'visit.offline_review_rejected', 'visit.offline_review_required', 'visit.reassigned',
  'visit.report_saved', 'visit.status_changed',
]

const trim = (filter: AuditFilter) =>
  Object.fromEntries(Object.entries(filter).map(([k, v]) => [k, v?.trim() || undefined])) as AuditFilter

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'medium' })

// Audit viewer (plan step 1.20, PRD §12): search and filter only. The server limits rows to the audit.read scope
// and has no route to change or delete them, so this page offers none.
export function AuditPage({ admin }: { admin: AdminApi }) {
  const [form, setForm] = useState<AuditFilter>({})
  const [rows, setRows] = useState<AuditEntry[] | null>(null)
  const [filter, setFilter] = useState<AuditFilter>({})
  const [hasMore, setHasMore] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    admin.auditLogs({}).then(
      (page) => active && (setRows(page), setHasMore(page.length === auditPageSize)),
      (e: unknown) => active && setError(errorMessage(e)),
    )
    return () => {
      active = false
    }
  }, [admin])

  async function load(next: AuditFilter, after?: AuditEntry) {
    setBusy(true)
    setError(null)
    try {
      const page = await admin.auditLogs(next, after)
      setRows(after ? [...(rows ?? []), ...page] : page)
      setFilter(next)
      setHasMore(page.length === auditPageSize)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  function search(e?: FormEvent) {
    e?.preventDefault()
    const trimmed = trim(form)
    if (trimmed.fromDate && trimmed.toDate && trimmed.fromDate > trimmed.toDate) {
      setError(t('web.audit.dateOrder'))
      return
    }
    void load(trimmed)
  }

  function byActor(entry: AuditEntry) {
    const next = { ...form, actorId: entry.actorId }
    setForm(next)
    void load(trim(next))
  }

  const field = (key: keyof AuditFilter) => ({ value: form[key] ?? '', onChange: (e: { target: { value: string } }) => setForm({ ...form, [key]: e.target.value }) })
  return (
    <section className="space-y-4 p-6">
      <h2 className="text-xl font-semibold">{t('web.audit.title')}</h2>
      <form onSubmit={search} className="flex flex-wrap items-end gap-2 text-sm">
        <input aria-label={t('web.audit.action')} list="audit-actions" placeholder={t('web.audit.action')} className="rounded border px-2 py-1" {...field('action')} />
        <datalist id="audit-actions">
          {knownActions.map((a) => (
            <option key={a} value={a} />
          ))}
        </datalist>
        <input aria-label={t('web.audit.actorId')} placeholder={t('web.audit.actorId')} className="w-80 rounded border px-2 py-1" {...field('actorId')} />
        <input aria-label={t('web.audit.entityId')} placeholder={t('web.audit.entityId')} className="rounded border px-2 py-1" {...field('entityId')} />
        <input type="date" aria-label={t('web.audit.fromDate')} className="rounded border px-2 py-1" {...field('fromDate')} />
        <input type="date" aria-label={t('web.audit.toDate')} className="rounded border px-2 py-1" {...field('toDate')} />
        <button type="submit" className="rounded border px-3 py-1" disabled={busy}>
          {t('web.action.search')}
        </button>
        <button type="button" className="rounded border px-3 py-1" disabled={busy} onClick={() => (setForm({}), void load({}))}>
          {t('web.action.clear')}
        </button>
      </form>
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      {rows?.length === 0 && <p className="text-slate-600">{t('web.audit.none')}</p>}
      {rows && rows.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.audit.time')}</th>
              <th>{t('web.audit.actor')}</th>
              <th>{t('web.audit.action')}</th>
              <th>{t('web.audit.entity')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="border-b">
                <td className="py-2 whitespace-nowrap">{when.format(new Date(r.occurredAt))}</td>
                <td>
                  <button className="text-start underline" title={t('web.audit.onlyActor')} onClick={() => byActor(r)}>
                    {r.actorEmail ?? r.actorId}
                  </button>
                </td>
                <td>{r.action}</td>
                <td className="font-mono text-xs">{r.entityId}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {rows && hasMore && (
        <button className="rounded border px-3 py-1 text-sm" disabled={busy} onClick={() => void load(filter, rows[rows.length - 1])}>
          {t('web.action.loadMore')}
        </button>
      )}
    </section>
  )
}
