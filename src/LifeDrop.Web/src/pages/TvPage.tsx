import { useCallback, useEffect, useState } from 'react'
import { AdminApi, can, iraqToday, Permissions, type KpiReport, type LiveMapEntry, type Me } from '../api/admin.ts'
import { LiveMapView } from '../components/LiveMapView.tsx'
import { t, type TextKey } from '../i18n.ts'

export const refreshMs = 60_000
export const mapRefreshMs = 30_000
export const tvRole = 'TvDisplay'

// A screen account: the TV role and nothing else. It opens straight on the wall screen.
export const isTvOnly = (me: Me) => me.roles.length > 0 && me.roles.every((r) => r === tvRole)

// Keeps the TV from going to sleep while the wall screen is open. Not every TV browser has it; failing is fine.
function useStayAwake() {
  useEffect(() => {
    let lock: { release: () => Promise<void> } | null = null
    let disposed = false
    const wakeLock = (navigator as Navigator & { wakeLock?: { request: (type: 'screen') => Promise<{ release: () => Promise<void> }> } }).wakeLock
    wakeLock?.request('screen').then((l) => {
      if (disposed) void l.release()
      else lock = l
    }, () => {})
    return () => {
      disposed = true
      void lock?.release()
    }
  }, [])
}
const percent = (value: number | null) => (value === null ? t('web.reports.noValue') : `${value}%`)

// Wall screen (plan step 3.9): today's totals in large type for a TV in the office, refreshed every minute. Privacy by design: only totals from the KPI report
// of the signed-in manager (their own area) and the number of people on shift; no names, e-mails, positions, customers or phone numbers are ever shown or kept here.
export function TvPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const showTotals = can(me, ...Permissions.reports)
  const showMap = can(me, ...Permissions.liveMap)
  const [view, setView] = useState<'totals' | 'map'>(showTotals ? 'totals' : 'map')
  useStayAwake()
  // The map view fills the screen under the title, so a TV (whose browser often shows no scroll bar) needs no scrolling; the totals view scrolls with a visible bar (index.css).
  return (
    <section className={view === 'map' ? 'flex h-[calc(100vh-3.5rem)] flex-col gap-3 p-4' : 'space-y-6 p-8'}>
      <div className="flex flex-wrap items-center gap-4">
        <h2 className="text-3xl font-semibold">{t(view === 'map' ? 'web.tv.viewMap' : 'web.tv.title')}</h2>
        {showTotals && showMap && (
          <div className="flex gap-2" role="group">
            <button className={`rounded border px-3 py-1 text-sm ${view === 'totals' ? 'bg-slate-900 text-white' : ''}`} aria-pressed={view === 'totals'} onClick={() => setView('totals')}>{t('web.tv.viewTotals')}</button>
            <button className={`rounded border px-3 py-1 text-sm ${view === 'map' ? 'bg-slate-900 text-white' : ''}`} aria-pressed={view === 'map'} onClick={() => setView('map')}>{t('web.tv.viewMap')}</button>
          </div>
        )}
        <button className="ms-auto rounded border px-3 py-1 text-sm" onClick={() => void document.documentElement.requestFullscreen?.()}>
          {t('web.tv.fullscreen')}
        </button>
      </div>
      {view === 'totals' && showTotals && <TvTotals admin={admin} me={me} />}
      {view === 'map' && showMap && <TvMap admin={admin} />}
    </section>
  )
}

// The live map for a wall screen: people on shift, large map, refreshed every 30 s. A point older than 10 minutes is never shown as live (see LiveMapPage).
function TvMap({ admin }: { admin: AdminApi }) {
  const [entries, setEntries] = useState<LiveMapEntry[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [updated, setUpdated] = useState<Date | null>(null)
  const load = useCallback(async () => {
    try {
      setEntries(await admin.liveMap())
      setFailed(false)
      setUpdated(new Date())
    } catch {
      setFailed(true)
    }
  }, [admin])
  useEffect(() => {
    const first = setTimeout(() => void load(), 0)
    const timer = setInterval(() => void load(), mapRefreshMs)
    return () => {
      clearTimeout(first)
      clearInterval(timer)
    }
  }, [load])

  const list = entries ?? []
  const anyPrecise = list.some((e) => e.precise)
  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3">
      <div className="flex flex-wrap items-center gap-4 text-lg text-slate-700">
        <span>{t('web.tv.mapNote')}</span>
        <span>{t('web.tv.liveOf', { live: list.filter((e) => e.isLive).length, total: list.length })}</span>
        {updated && <span>{t('web.tv.updated', { time: updated.toLocaleTimeString('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' }) })}</span>}
      </div>
      {failed && <p role="alert" className="text-lg text-red-700">{t('web.error.network')}</p>}
      {entries && anyPrecise && <div className="min-h-0 flex-1"><LiveMapView entries={list} large /></div>}
      {entries && !anyPrecise && list.length === 0 && <p className="text-xl text-slate-600">{t('web.map.nobody')}</p>}
      {entries && !anyPrecise && list.length > 0 && <p className="text-xl text-slate-600">{t('web.map.statusOnly')}</p>}
    </div>
  )
}

function TvTotals({ admin, me }: { admin: AdminApi; me: Me }) {
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
    const first = setTimeout(() => void load(), 0)
    const timer = setInterval(() => void load(), refreshMs)
    return () => {
      clearTimeout(first)
      clearInterval(timer)
    }
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
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-4">
        <span className="text-sm text-slate-600">{t('web.tv.privacy')}</span>
        {updated && <span className="text-sm text-slate-600">{t('web.tv.updated', { time: updated.toLocaleTimeString('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' }) })}</span>}
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
    </div>
  )
}
