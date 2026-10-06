import { useCallback, useEffect, useState } from 'react'
import { AdminApi, iraqToday, type TrackableUser } from '../api/admin.ts'
import { formatDuration, nextIndex } from '../api/geo.ts'
import { useLoad } from '../components/useLoad.ts'
import { RouteMapView } from '../components/RouteMapView.tsx'
import { t, type TextKey } from '../i18n.ts'
import { errorMessage, type RouteHistory } from '../api/admin.ts'

const playMs = 150
const time = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit', second: '2-digit' })
const minutes = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' })

// Route history and playback (plan step 2.11, decision D6): one user, one day; the line, the stops, the check-ins and a slider with a play button.
// Needs tracking.read and a user inside the reader's area. Exact points are kept for 90 days; every opening is audited by the server.
export function RouteHistoryPage({ admin }: { admin: AdminApi }) {
  const users = useLoad(useCallback(() => admin.trackingUsers(), [admin]))
  const [user, setUser] = useState('')
  const [date, setDate] = useState(iraqToday())
  const [route, setRoute] = useState<RouteHistory | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [index, setIndex] = useState(0)
  const [playing, setPlaying] = useState(false)

  // Choosing another user or day clears the route and the playback; the route is then loaded for the new choice.
  const reset = () => {
    setRoute(null)
    setIndex(0)
    setPlaying(false)
  }

  useEffect(() => {
    if (!user) return
    let active = true
    admin.routeHistory(user, date).then(
      (r) => active && (setRoute(r), setError(null)),
      (e: unknown) => active && setError(errorMessage(e)),
    )
    return () => {
      active = false
    }
  }, [admin, user, date])

  const count = route?.points.length ?? 0
  useEffect(() => {
    if (!playing || index >= count - 1) return
    const timer = setTimeout(() => {
      const next = nextIndex(index, count)
      setIndex(next)
      if (next >= count - 1) setPlaying(false)
    }, playMs)
    return () => clearTimeout(timer)
  }, [playing, index, count])

  const point = route?.points[index]
  return (
    <section className="space-y-4 p-6">
      <h2 className="text-xl font-semibold">{t('web.route.title')}</h2>
      <p className="text-xs text-slate-600">{t('web.route.retention')}</p>
      {(users.error || error) && <p role="alert" className="text-sm text-red-700">{users.error ?? error}</p>}
      <div className="flex flex-wrap items-center gap-3">
        <select aria-label={t('web.route.user')} className="rounded border px-2 py-1 text-sm" value={user} onChange={(e) => (reset(), setUser(e.target.value))}>
          <option value="">{t('web.route.chooseUser')}</option>
          {(users.data ?? []).map((u: TrackableUser) => (
            <option key={u.id} value={u.id}>
              {u.email ?? u.id}
              {u.teamName ? ` · ${u.teamName}` : ''}
            </option>
          ))}
        </select>
        <input type="date" aria-label={t('web.route.date')} className="rounded border px-2 py-1 text-sm" max={iraqToday()} value={date} onChange={(e) => e.target.value && (reset(), setDate(e.target.value))} />
      </div>
      {route && route.shifts.some((s) => s.pointsPurged) && <p className="text-sm text-amber-700">{t('web.route.purged')}</p>}
      {route && route.truncated && <p className="text-sm text-amber-700">{t('web.route.truncated')}</p>}
      {route && count === 0 && <p className="text-slate-600">{t('web.route.nopoints')}</p>}
      {route && count > 0 && (
        <>
          <RouteMapView points={route.points} stops={route.stops} events={route.events} index={index} />
          <div className="flex flex-wrap items-center gap-3 text-sm">
            <button className="rounded border px-3 py-1" onClick={() => (index >= count - 1 ? (setIndex(0), setPlaying(true)) : setPlaying(!playing))}>
              {playing ? t('web.route.pause') : t('web.route.play')}
            </button>
            <input type="range" aria-label={t('web.route.position')} className="min-w-64 flex-1" min={0} max={count - 1} value={index} onChange={(e) => (setPlaying(false), setIndex(Number(e.target.value)))} />
            <span>
              {t('web.route.point', { index: index + 1, count })}
              {point ? ` · ${time.format(new Date(point.capturedAt))} · ${t('web.route.accuracy', { meters: Math.round(point.accuracyMeters) })}` : ''}
              {point?.isMocked && <strong className="ms-2 text-red-700">{t('web.route.mock')}</strong>}
            </span>
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            <div>
              <h3 className="font-medium">{t('web.route.stops')}</h3>
              <ul className="text-sm">
                {route.stops.map((s) => (
                  <li key={s.from}>
                    {t('web.route.stop', { from: minutes.format(new Date(s.from)), to: minutes.format(new Date(s.to)), duration: formatDuration(s.seconds) })}
                    {s.customerName ? ` · ${t('web.route.nearCustomer', { customer: s.customerName, meters: s.customerDistanceMeters ?? 0 })}` : ''}
                  </li>
                ))}
              </ul>
            </div>
            <div>
              <h3 className="font-medium">{t('web.route.events')}</h3>
              <ul className="text-sm">
                {route.events.map((e) => (
                  <li key={`${e.kind}-${e.capturedAt}`}>
                    {minutes.format(new Date(e.capturedAt))} · {t(`web.route.event.${e.kind}` as TextKey)} · {e.customerName ?? ''}
                    {!e.accepted ? ` · ${t('web.route.refused', { reason: e.rejectionReason ?? '' })}` : ''}
                  </li>
                ))}
              </ul>
            </div>
          </div>
        </>
      )}
    </section>
  )
}
