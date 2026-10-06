import { useCallback, useEffect, useState } from 'react'
import { AdminApi, can, iraqToday, Permissions, type KpiReport, type Me } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'

export const refreshMs = 60_000
const percent = (value: number | null) => (value === null ? t('web.reports.noValue') : `${value}%`)

// Wall screen (plan step 3.9): today's totals in large type for a TV in the office, refreshed every minute. Privacy by design: only totals from the KPI report
// of the signed-in manager (their own area) and the number of people on shift; no names, e-mails, positions, customers or phone numbers are ever shown or kept here.
export function TvPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [report, setReport] = useState<KpiReport | null>(null)
  const [onShift, setOnShift] = useState<{ live: number; total: number } | null>(null)
  const [updated, setUpdated] = useState<Date | null>(null)
  const [failed, setFailed] = useState(false)
  const showShift = can(me, ...Permissions.liveMap)

  const load = useCallback(async () => {
    const today = iraqToday()
    try {
      setReport(await admin.kpis(today, today, '', '', ''))
      setFailed(false)
      setUpdated(new Date())
    } catch {
      setFailed(true)
    }
    if (showShift) {
      try {
        const entries = await admin.liveMap()
        setOnShift({ live: entries.filter((e) => e.isLive).length, total: entries.length })
      } catch {
        setOnShift(null)
      }
    }
  }, [admin, showShift])

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), refreshMs)
    return () => clearInterval(timer)
  }, [load])

  const total = report?.total
  const cards: { label: TextKey; value: string; basis: string }[] = total
    ? [
        { label: 'web.reports.completion', value: percent(total.rates.visitCompletion), basis: t('web.reports.of', { numerator: total.counts.visitsCompleted, denominator: total.counts.visitsPlanned }) },
        { label: 'web.reports.orders', value: String(total.counts.orders), basis: total.counts.orderTotals.map((x) => `${x.total.toLocaleString('en-US', { maximumFractionDigits: 3 })} ${x.currency}`).join(' · ') },
        { label: 'web.reports.delivery', value: percent(total.rates.orderDelivery), basis: t('web.reports.of', { numerator: total.counts.ordersDelivered, denominator: total.counts.ordersDelivered + total.counts.ordersDeliveryFailed }) },
        { label: 'web.reports.followups', value: percent(total.rates.followUpOverdue), basis: t('web.reports.of', { numerator: total.counts.followUpsOverdue, denominator: total.counts.followUpBase }) },
      ]
    : []

  return (
    <section className="space-y-6 p-8">
      <div className="flex flex-wrap items-center gap-4">
        <h2 className="text-3xl font-semibold">{t('web.tv.title')}</h2>
        <span className="text-sm text-slate-600">{t('web.tv.privacy')}</span>
        {updated && <span className="text-sm text-slate-600">{t('web.tv.updated', { time: updated.toLocaleTimeString('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' }) })}</span>}
        <button className="ms-auto rounded border px-3 py-1 text-sm" onClick={() => void document.documentElement.requestFullscreen?.()}>
          {t('web.tv.fullscreen')}
        </button>
      </div>
      {failed && <p role="alert" className="text-lg text-red-700">{t('web.error.network')}</p>}
      <ul className="grid gap-6 sm:grid-cols-2 xl:grid-cols-3">
        {cards.map((c) => (
          <li key={c.label} className="rounded-lg border p-6">
            <div className="text-lg text-slate-600">{t(c.label)}</div>
            <div className="text-6xl font-bold">{c.value}</div>
            <div className="text-lg text-slate-600">{c.basis}</div>
          </li>
        ))}
        {onShift && (
          <li className="rounded-lg border p-6">
            <div className="text-lg text-slate-600">{t('web.tv.onShift')}</div>
            <div className="text-6xl font-bold">{onShift.total}</div>
            <div className="text-lg text-slate-600">{t('web.tv.liveOf', { live: onShift.live, total: onShift.total })}</div>
          </li>
        )}
      </ul>
    </section>
  )
}
