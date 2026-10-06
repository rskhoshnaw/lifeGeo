import { useCallback, useEffect, useState } from 'react'
import { AdminApi, errorMessage, iraqToday, type KpiReport, type KpiRow, type Region, type Team } from '../api/admin.ts'
import { downloadText } from '../components/download.ts'
import { kpiCsv } from '../components/reportCsv.ts'
import { SavedReports } from '../components/SavedReports.tsx'
import { t, type TextKey } from '../i18n.ts'

const customerTypes = ['Pharmacy', 'Doctor', 'Wholesaler']
const percent = (value: number | null) => (value === null ? t('web.reports.noValue') : `${value}%`)
const money = (row: KpiRow) => row.counts.orderTotals.map((x) => `${x.total.toLocaleString('en-US', { maximumFractionDigits: 3 })} ${x.currency}`).join(' · ')
const monthStart = () => `${iraqToday().slice(0, 8)}01`

// Reports and KPIs (plan step 2.12, PRD §10): a day range (Iraq days), region, team and customer type; the total, a row per user, and alerts by kind. Every number is a
// numerator over a denominator written in docs/KPI.md; nothing to measure shows "—", never 0 %. The download is the same table as a CSV file.
export function ReportsPage({ admin }: { admin: AdminApi }) {
  const [from, setFrom] = useState(monthStart())
  const [to, setTo] = useState(iraqToday())
  const [region, setRegion] = useState('')
  const [team, setTeam] = useState('')
  const [type, setType] = useState('')
  const [regions, setRegions] = useState<Region[]>([])
  const [teams, setTeams] = useState<Team[]>([])
  const [report, setReport] = useState<KpiReport | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    admin.regions().then(setRegions, () => setRegions([]))
    admin.teams().then(setTeams, () => setTeams([]))
  }, [admin])

  const load = useCallback(() => {
    let active = true
    admin.kpis(from, to, region, team, type).then(
      (r) => active && (setReport(r), setError(null)),
      (e: unknown) => active && setError(errorMessage(e)),
    )
    return () => {
      active = false
    }
  }, [admin, from, to, region, team, type])
  useEffect(() => load(), [load])

  const total = report?.total
  const cards: { label: TextKey; value: string; basis: string }[] = total
    ? [
        { label: 'web.reports.completion', value: percent(total.rates.visitCompletion), basis: t('web.reports.of', { numerator: total.counts.visitsCompleted, denominator: total.counts.visitsPlanned }) },
        { label: 'web.reports.failure', value: percent(total.rates.visitFailure), basis: t('web.reports.of', { numerator: total.counts.visitsFailed, denominator: total.counts.visitsPlanned }) },
        { label: 'web.reports.delay', value: percent(total.rates.delay), basis: t('web.reports.of', { numerator: total.counts.delayedVisits, denominator: total.counts.delayBase }) },
        { label: 'web.reports.newCustomers', value: String(total.counts.newCustomers), basis: `${total.counts.approvedNewCustomers} ${t('web.reports.approved')}` },
        { label: 'web.reports.orders', value: String(total.counts.orders), basis: money(total) },
        { label: 'web.reports.delivery', value: percent(total.rates.orderDelivery), basis: t('web.reports.of', { numerator: total.counts.ordersDelivered, denominator: total.counts.ordersDelivered + total.counts.ordersDeliveryFailed }) },
        { label: 'web.reports.followups', value: percent(total.rates.followUpOverdue), basis: t('web.reports.of', { numerator: total.counts.followUpsOverdue, denominator: total.counts.followUpBase }) },
      ]
    : []

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.reports.title')}</h2>
        <label className="text-sm">
          {t('web.reports.from')} <input type="date" className="rounded border px-2 py-1" max={to} value={from} onChange={(e) => e.target.value && setFrom(e.target.value)} />
        </label>
        <label className="text-sm">
          {t('web.reports.to')} <input type="date" className="rounded border px-2 py-1" min={from} max={iraqToday()} value={to} onChange={(e) => e.target.value && setTo(e.target.value)} />
        </label>
        <select aria-label={t('web.reports.region')} className="rounded border px-2 py-1 text-sm" value={region} onChange={(e) => setRegion(e.target.value)}>
          <option value="">{t('web.reports.region')}: {t('web.reports.all')}</option>
          {regions.map((r) => (
            <option key={r.id} value={r.id}>
              {r.name}
            </option>
          ))}
        </select>
        <select aria-label={t('web.reports.team')} className="rounded border px-2 py-1 text-sm" value={team} onChange={(e) => setTeam(e.target.value)}>
          <option value="">{t('web.reports.team')}: {t('web.reports.all')}</option>
          {teams.filter((x) => !region || x.regionId === region).map((x) => (
            <option key={x.id} value={x.id}>
              {x.name}
            </option>
          ))}
        </select>
        <select aria-label={t('web.reports.customerType')} className="rounded border px-2 py-1 text-sm" value={type} onChange={(e) => setType(e.target.value)}>
          <option value="">{t('web.reports.customerType')}: {t('web.reports.all')}</option>
          {customerTypes.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
        {report && report.users.length > 0 && (
          <button className="rounded border px-3 py-1 text-sm" onClick={() => downloadText(`kpis-${report.from}-${report.to}.csv`, kpiCsv(report.users, report.total), 'text/csv;charset=utf-8')}>
            {t('web.reports.download')}
          </button>
        )}
      </div>
      <p className="text-xs text-slate-600">{t('web.reports.definitions')}</p>
      <SavedReports admin={admin} filter={{ from, to, regionId: region, teamId: team, customerType: type }} onOpen={(r) => (setReport(r), setError(null))} />
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      {report && report.users.length === 0 && <p className="text-slate-600">{t('web.reports.none')}</p>}
      {report && report.users.length > 0 && (
        <>
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {cards.map((c) => (
              <li key={c.label} className="rounded border p-3">
                <div className="text-xs text-slate-600">{t(c.label)}</div>
                <div className="text-2xl font-semibold">{c.value}</div>
                <div className="text-xs text-slate-600">{c.basis}</div>
              </li>
            ))}
          </ul>
          <h3 className="font-medium">{t('web.reports.byUser')}</h3>
          <table className="w-full text-start text-sm">
            <thead>
              <tr className="border-b">
                <th className="py-2">{t('web.reports.user')}</th>
                <th>{t('web.reports.completion')}</th>
                <th>{t('web.reports.failure')}</th>
                <th>{t('web.reports.delay')}</th>
                <th>{t('web.reports.newCustomers')}</th>
                <th>{t('web.reports.orders')}</th>
                <th>{t('web.reports.delivery')}</th>
                <th>{t('web.reports.followups')}</th>
              </tr>
            </thead>
            <tbody>
              {[...report.users, report.total].map((r, i) => {
                const last = i === report.users.length
                return (
                  <tr key={last ? 'total' : r.userId} className={`border-b ${last ? 'font-semibold' : ''}`}>
                    <td className="py-2">{last ? t('web.reports.total') : `${r.email ?? r.userId}${r.teamName ? ` · ${r.teamName}` : ''}`}</td>
                    <td>
                      {percent(r.rates.visitCompletion)} ({r.counts.visitsCompleted}/{r.counts.visitsPlanned})
                    </td>
                    <td>
                      {percent(r.rates.visitFailure)} ({r.counts.visitsFailed}/{r.counts.visitsPlanned})
                    </td>
                    <td>
                      {percent(r.rates.delay)} ({r.counts.delayedVisits}/{r.counts.delayBase})
                    </td>
                    <td>
                      {r.counts.newCustomers} ({r.counts.approvedNewCustomers} {t('web.reports.approved')})
                    </td>
                    <td>
                      {r.counts.orders} {money(r)}
                    </td>
                    <td>
                      {percent(r.rates.orderDelivery)} ({r.counts.ordersDelivered}/{r.counts.ordersDelivered + r.counts.ordersDeliveryFailed})
                    </td>
                    <td>
                      {percent(r.rates.followUpOverdue)} ({r.counts.followUpsOverdue}/{r.counts.followUpBase})
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {report.alerts.length > 0 && (
            <>
              <h3 className="font-medium">{t('web.reports.alerts')}</h3>
              <table className="w-full text-start text-sm">
                <thead>
                  <tr className="border-b">
                    <th className="py-2">{t('web.alerts.kind')}</th>
                    <th>{t('web.reports.alertTotal')}</th>
                    <th>{t('web.reports.alertHandled')}</th>
                    <th>{t('web.alerts.outcome.valid')}</th>
                    <th>{t('web.alerts.outcome.suspicious')}</th>
                    <th>{t('web.alerts.outcome.fraud')}</th>
                  </tr>
                </thead>
                <tbody>
                  {report.alerts.map((a) => (
                    <tr key={a.kind} className="border-b">
                      <td className="py-2">{t(`web.alerts.kind.${a.kind}` as TextKey)}</td>
                      <td>{a.total}</td>
                      <td>{a.handled}</td>
                      <td>{a.valid}</td>
                      <td>{a.suspicious}</td>
                      <td>{a.fraud}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </>
      )}
    </section>
  )
}
