import { useCallback, useState } from 'react'
import { AdminApi, can, Permissions, reviewedAlertKinds, type Alert, type Me, type ShiftAlertSettings } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

// Alerts (plan step 2.9): operational and security alerts for the managers whose area they are about. One event makes one alert; a manager marks it seen and then
// handled with a note. The server looks for new alerts every few minutes; "Check now" does it at once.
export function AlertsPage({ admin, me, onChanged }: { admin: AdminApi; me: Me; onChanged: () => void }) {
  const [category, setCategory] = useState('')
  const [status, setStatus] = useState('')
  const [scanned, setScanned] = useState<string | null>(null)
  const list = useLoad(useCallback(() => admin.alerts(category, status), [admin, category, status]))
  const action = useAction(() => {
    list.reload()
    onChanged()
  })
  const canHandle = can(me, ...Permissions.handleAlerts)
  const settings = useLoad(useCallback(() => admin.shiftAlertSettings(), [admin]))
  const [outcome, setOutcome] = useState<Record<string, string>>({})

  function handle(alert: Alert) {
    const note = window.prompt(t('web.alerts.notePrompt'))?.trim()
    if (note) void action.run(() => admin.handleAlert(alert.id, note, reviewedAlertKinds.includes(alert.kind) ? (outcome[alert.id] ?? null) : null))
  }

  async function scan() {
    const result = await action.run(() => admin.scanAlerts())
    if (result) setScanned(t('web.alerts.scanned', { count: result.created }))
  }

  // The evidence the scan kept, as short "name: value" pairs (numbers and times that were computed).
  const evidence = (a: Alert) => {
    if (!a.evidence) return ''
    try {
      return Object.entries(JSON.parse(a.evidence) as Record<string, unknown>)
        .map(([k, v]) => `${k}: ${typeof v === 'string' ? v.replace('T', ' ').slice(0, 19) : String(v)}`)
        .join(' · ')
    } catch {
      return ''
    }
  }
  const detail = (a: Alert) => (a.detail && (a.kind === 'visit_long_stay') ? ` (${t(`web.alerts.detail.${a.detail}` as TextKey)})` : a.detail ? ` (${a.detail})` : '')

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.alerts.title')}</h2>
        <select aria-label={t('web.alerts.category')} className="rounded border px-2 py-1 text-sm" value={category} onChange={(e) => setCategory(e.target.value)}>
          <option value="">{t('web.alerts.allCategories')}</option>
          {['operational', 'security'].map((c) => (
            <option key={c} value={c}>
              {t(`web.alerts.category.${c}` as TextKey)}
            </option>
          ))}
        </select>
        <select aria-label={t('web.alerts.status')} className="rounded border px-2 py-1 text-sm" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">{t('web.alerts.allStatuses')}</option>
          {['new', 'seen', 'handled'].map((s) => (
            <option key={s} value={s}>
              {t(`web.alerts.status.${s}` as TextKey)}
            </option>
          ))}
        </select>
        {canHandle && (
          <button className="rounded border px-3 py-1 text-sm" disabled={action.busy} onClick={() => void scan()}>
            {t('web.alerts.scan')}
          </button>
        )}
        {scanned && <span role="status" className="text-sm text-slate-600">{scanned}</span>}
      </div>
      {settings.data && canHandle && <ShiftSettingsForm key={`${settings.data.shiftWindowHours}-${settings.data.stationaryMinutes}`} admin={admin} current={settings.data} onSaved={settings.reload} />}
      {(list.error || action.error) && <p role="alert" className="text-sm text-red-700">{list.error ?? action.error}</p>}
      {list.data?.length === 0 && <p className="text-slate-600">{t('web.alerts.none')}</p>}
      {list.data && list.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.alerts.kind')}</th>
              <th>{t('web.alerts.category')}</th>
              <th>{t('web.alerts.about')}</th>
              <th>{t('web.alerts.time')}</th>
              <th>{t('web.alerts.status')}</th>
              {canHandle && <th />}
            </tr>
          </thead>
          <tbody>
            {list.data.map((a) => {
              const kind = t(`web.alerts.kind.${a.kind}` as TextKey)
              return (
                <tr key={a.id} className={`border-b align-top ${a.status === 'handled' ? 'text-slate-500' : ''}`}>
                  <td className="py-2">
                    {kind}
                    {detail(a)}
                    {a.evidence && (
                      <details className="text-xs text-slate-600">
                        <summary>{t('web.alerts.evidence')}</summary>
                        {evidence(a)}
                      </details>
                    )}
                  </td>
                  <td>{t(`web.alerts.category.${a.category}` as TextKey)}</td>
                  <td>
                    {a.subjectEmail ?? ''}
                    {a.customerCode ? ` · ${a.customerCode} ${a.customerName ?? ''}` : ''}
                  </td>
                  <td>{a.occurredAt.slice(0, 16).replace('T', ' ')}</td>
                  <td>
                    {t(`web.alerts.status.${a.status}` as TextKey)}
                    {a.handledOutcome ? ` · ${t(`web.alerts.outcome.${a.handledOutcome}` as TextKey)}` : ''}
                    {a.handledNote ? ` · ${a.handledNote}` : ''}
                  </td>
                  {canHandle && (
                    <td className="space-x-2 py-2 text-end">
                      {a.status === 'new' && (
                        <button className="rounded border px-2 py-1" aria-label={t('web.alerts.seeAction', { kind })} disabled={action.busy} onClick={() => void action.run(() => admin.seeAlert(a.id))}>
                          {t('web.alerts.see')}
                        </button>
                      )}
                      {a.status !== 'handled' && reviewedAlertKinds.includes(a.kind) && (
                        <select aria-label={t('web.alerts.outcomeFor', { kind })} className="rounded border px-1 py-1" value={outcome[a.id] ?? ''} onChange={(e) => setOutcome({ ...outcome, [a.id]: e.target.value })}>
                          <option value="">{t('web.alerts.outcome')}</option>
                          {['valid', 'suspicious', 'fraud'].map((o) => (
                            <option key={o} value={o}>
                              {t(`web.alerts.outcome.${o}` as TextKey)}
                            </option>
                          ))}
                        </select>
                      )}
                      {a.status !== 'handled' && (
                        <button className="rounded border px-2 py-1" aria-label={t('web.alerts.handleAction', { kind })} disabled={action.busy || (reviewedAlertKinds.includes(a.kind) && !outcome[a.id])} onClick={() => handle(a)}>
                          {t('web.alerts.handle')}
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </section>
  )
}

// The sales manager sets how long the first part of a shift is and how many minutes in one place make an alert (plan step 4.33).
function ShiftSettingsForm({ admin, current, onSaved }: { admin: AdminApi; current: ShiftAlertSettings; onSaved: () => void }) {
  const [hours, setHours] = useState(String(current.shiftWindowHours))
  const [minutes, setMinutes] = useState(String(current.stationaryMinutes))
  const action = useAction(onSaved)
  return (
    <form
      className="flex flex-wrap items-center gap-3 rounded border p-3 text-sm"
      onSubmit={(e) => {
        e.preventDefault()
        void action.run(() => admin.setShiftAlertSettings({ shiftWindowHours: Number(hours), stationaryMinutes: Number(minutes) }))
      }}
    >
      <span className="font-semibold">{t('web.alerts.shiftSettings')}</span>
      <label className="flex items-center gap-1">
        {t('web.alerts.shiftHours')}
        <input type="number" min={1} max={12} className="w-20 rounded border px-2 py-1" value={hours} onChange={(e) => setHours(e.target.value)} />
      </label>
      <label className="flex items-center gap-1">
        {t('web.alerts.stationaryMinutes')}
        <input type="number" min={5} max={120} className="w-20 rounded border px-2 py-1" value={minutes} onChange={(e) => setMinutes(e.target.value)} />
      </label>
      <button type="submit" className="rounded border px-3 py-1" disabled={action.busy}>
        {t('web.catalog.save')}
      </button>
      {action.error && <span role="alert" className="text-red-700">{action.error}</span>}
    </form>
  )
}
