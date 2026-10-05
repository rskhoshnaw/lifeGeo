import { useCallback, useEffect } from 'react'
import type { AdminApi } from '../api/admin.ts'
import { useLoad } from '../components/useLoad.ts'
import { LiveMapView } from '../components/LiveMapView.tsx'
import { t } from '../i18n.ts'
import { status } from './liveStatus.ts'

const refreshMs = 30_000
const time = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' })

// Basic live map (plan step 1.19, decision D3): users on shift, refreshed every 30 s. A point older than
// 5 minutes is never shown as live. Users without tracking.read see status only; the server sends no coordinates.
export function LiveMapPage({ admin }: { admin: AdminApi }) {
  const live = useLoad(useCallback(() => admin.liveMap(), [admin]))
  const { reload } = live
  useEffect(() => {
    const timer = setInterval(reload, refreshMs)
    return () => clearInterval(timer)
  }, [reload])

  const entries = live.data ?? []
  const anyPrecise = entries.some((e) => e.precise)
  return (
    <section className="space-y-4 p-6">
      <h2 className="text-xl font-semibold">{t('web.map.title')}</h2>
      {live.error && <p role="alert" className="text-sm text-red-700">{live.error}</p>}
      {live.data && anyPrecise && <LiveMapView entries={entries} />}
      {live.data && !anyPrecise && entries.length > 0 && <p className="text-sm text-slate-600">{t('web.map.statusOnly')}</p>}
      {live.data?.length === 0 && <p className="text-slate-600">{t('web.map.nobody')}</p>}
      {entries.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.map.user')}</th>
              <th>{t('web.map.team')}</th>
              <th>{t('web.map.status')}</th>
              <th>{t('web.map.lastUpdate')}</th>
              <th>{t('web.map.shiftStarted')}</th>
              <th>{t('web.map.lastCustomer')}</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((e) => (
              <tr key={e.userId} className="border-b">
                <td className="py-2">{e.email ?? e.userId}</td>
                <td>{e.teamName ?? '—'}</td>
                <td className={e.isLive ? 'text-green-700' : 'text-slate-500'}>
                  {status(e)}
                  {e.isMocked && <span className="ms-2 text-red-700">{t('web.map.mock')}</span>}
                </td>
                <td>{e.lastUpdateAt ? time.format(new Date(e.lastUpdateAt)) : '—'}</td>
                <td>{time.format(new Date(e.shiftStartedAt))}</td>
                <td>{e.lastCustomerName ? `${e.lastCustomerName} (${time.format(new Date(e.lastVisitAt!))})` : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
