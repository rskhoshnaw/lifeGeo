import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, type KpiReport, type ReportRun, type SavedReport, type SaveReport } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

const ranges = ['yesterday', 'last_7_days', 'last_30_days', 'this_month', 'last_month', 'fixed']
const frequencies = ['none', 'daily', 'weekly', 'monthly']
const weekdays = [1, 2, 3, 4, 5, 6, 7]
const when = (iso: string) => new Date(iso).toISOString().replace('T', ' ').slice(0, 16)

export interface ReportFilter {
  from: string
  to: string
  regionId: string
  teamId: string
  customerType: string
}

// The same definition again with another schedule state (used to pause and resume).
const definition = (r: SavedReport, isActive: boolean): SaveReport => ({
  name: r.name,
  range: r.range,
  ...(r.from ? { from: r.from } : {}),
  ...(r.to ? { to: r.to } : {}),
  ...(r.regionId ? { regionId: r.regionId } : {}),
  ...(r.teamId ? { teamId: r.teamId } : {}),
  ...(r.customerType ? { customerType: r.customerType } : {}),
  ...(r.schedule ? { schedule: { frequency: r.schedule.frequency, ...(r.schedule.weekday ? { weekday: r.schedule.weekday } : {}), ...(r.schedule.dayOfMonth ? { dayOfMonth: r.schedule.dayOfMonth } : {}), hour: r.schedule.hour, isActive } } : {}),
})

// Saved and scheduled reports (plan step 3.2, part 1): the filters above are saved under a name, run any time, and may repeat every day, week or month at an Iraq
// hour. Each run keeps its whole result; opening one shows it in the report above. Everybody sees only their own reports.
export function SavedReports({ admin, filter, onOpen }: { admin: AdminApi; filter: ReportFilter; onOpen: (report: KpiReport) => void }) {
  const list = useLoad(useCallback(() => admin.savedReports(), [admin]))
  const action = useAction(list.reload)
  const [name, setName] = useState('')
  const [range, setRange] = useState('last_7_days')
  const [frequency, setFrequency] = useState('none')
  const [hour, setHour] = useState(8)
  const [weekday, setWeekday] = useState(1)
  const [dayOfMonth, setDayOfMonth] = useState(1)
  const [runs, setRuns] = useState<{ id: string; items: ReportRun[] } | null>(null)

  async function save(event: FormEvent) {
    event.preventDefault()
    const body: SaveReport = {
      name: name.trim(),
      range,
      ...(range === 'fixed' ? { from: filter.from, to: filter.to } : {}),
      ...(filter.regionId ? { regionId: filter.regionId } : {}),
      ...(filter.teamId ? { teamId: filter.teamId } : {}),
      ...(filter.customerType ? { customerType: filter.customerType } : {}),
      ...(frequency === 'none' ? {} : { schedule: { frequency, hour, isActive: true, ...(frequency === 'weekly' ? { weekday } : {}), ...(frequency === 'monthly' ? { dayOfMonth } : {}) } }),
    }
    const done = await action.run(async () => (await admin.saveReport(body), true))
    if (done) setName('')
  }

  async function run(report: SavedReport) {
    const detail = await action.run(() => admin.runSavedReport(report.id))
    if (detail?.report) onOpen(detail.report)
    if (runs?.id === report.id) void showRuns(report.id)
  }

  async function showRuns(id: string) {
    const items = await action.run(() => admin.savedReportRuns(id))
    if (items) setRuns({ id, items })
  }

  async function open(id: string, runId: string) {
    const detail = await action.run(() => admin.savedReportRun(id, runId))
    if (detail?.report) onOpen(detail.report)
  }

  function remove(report: SavedReport) {
    if (window.confirm(t('web.saved.confirmDelete', { name: report.name }))) void action.run(() => admin.deleteSavedReport(report.id))
  }

  return (
    <section className="space-y-4 rounded border p-4" aria-label={t('web.saved.title')}>
      <h3 className="font-medium">{t('web.saved.title')}</h3>
      {(list.error || action.error) && <p role="alert" className="text-sm text-red-700">{list.error ?? action.error}</p>}
      <form className="flex flex-wrap items-end gap-3" onSubmit={(e) => void save(e)}>
        <label className="text-sm">
          {t('web.saved.name')}
          <input className="mt-1 block rounded border px-2 py-1" value={name} required maxLength={100} onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="text-sm">
          {t('web.saved.range')}
          <select className="mt-1 block rounded border px-2 py-1" value={range} onChange={(e) => setRange(e.target.value)}>
            {ranges.map((r) => (
              <option key={r} value={r}>
                {t(`web.saved.range.${r}` as TextKey)}
              </option>
            ))}
          </select>
        </label>
        <label className="text-sm">
          {t('web.saved.schedule')}
          <select className="mt-1 block rounded border px-2 py-1" value={frequency} onChange={(e) => setFrequency(e.target.value)}>
            {frequencies.map((f) => (
              <option key={f} value={f}>
                {t(`web.saved.schedule.${f}` as TextKey)}
              </option>
            ))}
          </select>
        </label>
        {frequency === 'weekly' && (
          <label className="text-sm">
            {t('web.saved.weekday')}
            <select className="mt-1 block rounded border px-2 py-1" value={weekday} onChange={(e) => setWeekday(Number(e.target.value))}>
              {weekdays.map((d) => (
                <option key={d} value={d}>
                  {t(`web.saved.day.${d}` as TextKey)}
                </option>
              ))}
            </select>
          </label>
        )}
        {frequency === 'monthly' && (
          <label className="text-sm">
            {t('web.saved.dayOfMonth')}
            <input type="number" min={1} max={28} className="mt-1 block w-20 rounded border px-2 py-1" value={dayOfMonth} onChange={(e) => setDayOfMonth(Math.min(28, Math.max(1, Number(e.target.value) || 1)))} />
          </label>
        )}
        {frequency !== 'none' && (
          <label className="text-sm">
            {t('web.saved.hour')}
            <input type="number" min={0} max={23} className="mt-1 block w-20 rounded border px-2 py-1" value={hour} onChange={(e) => setHour(Math.min(23, Math.max(0, Number(e.target.value) || 0)))} />
          </label>
        )}
        <button className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || !name.trim()}>
          {t('web.saved.save')}
        </button>
      </form>

      {list.data?.length === 0 && <p className="text-sm text-slate-600">{t('web.saved.none')}</p>}
      {list.data?.map((r) => (
        <div key={r.id} className="space-y-2 border-t pt-2 text-sm">
          <div className="flex flex-wrap items-center gap-3">
            <strong>{r.name}</strong>
            <span>{t(`web.saved.range.${r.range}` as TextKey)}</span>
            {r.schedule && (
              <span className="text-slate-600">
                {t(`web.saved.schedule.${r.schedule.frequency}` as TextKey)} · {String(r.schedule.hour).padStart(2, '0')}:00
                {r.schedule.isActive && r.schedule.nextRunAt ? ` · ${t('web.saved.nextRun')}: ${when(r.schedule.nextRunAt)}` : ` · ${t('web.saved.paused')}`}
              </span>
            )}
            <span className="ms-auto space-x-2">
              <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void run(r)}>
                {t('web.saved.run')}
              </button>
              <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void showRuns(r.id)}>
                {t('web.saved.results')}
              </button>
              {r.schedule && (
                <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.updateSavedReport(r.id, definition(r, !r.schedule!.isActive)))}>
                  {r.schedule.isActive ? t('web.saved.pause') : t('web.saved.resume')}
                </button>
              )}
              <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => remove(r)}>
                {t('web.saved.delete')}
              </button>
            </span>
          </div>
          {runs?.id === r.id && (
            <ul className="space-y-1 ps-4">
              {runs.items.map((x) => (
                <li key={x.id} className="flex items-center gap-3">
                  <span>{when(x.ranAt)}</span>
                  <span>{t(`web.saved.trigger.${x.trigger}` as TextKey)}</span>
                  <span className={x.status === 'failed' ? 'text-red-700' : ''}>{t(`web.saved.status.${x.status}` as TextKey)}</span>
                  <span>
                    {x.from} → {x.to}
                  </span>
                  {x.status === 'ok' && (
                    <button className="rounded border px-2 py-0.5" onClick={() => void open(r.id, x.id)}>
                      {t('web.saved.open')}
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      ))}
    </section>
  )
}
