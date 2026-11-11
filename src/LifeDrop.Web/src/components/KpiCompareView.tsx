import { useEffect, useState } from 'react'
import { AdminApi, errorMessage, iraqToday, type KpiCompare, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { formatKpi, kpiChange, kpiMetrics } from './kpiCompare.ts'

const tone = (better: boolean | null) => (better === null ? '' : better ? 'text-green-700' : 'text-red-700')

// One user's month next to the month before, the same month last year and 12 months of trend (plan step 5.29). With no user chosen it is the signed-in user's own; a manager can
// choose a user of the own area (the server decides who).
export function KpiCompareView({ admin }: { admin: AdminApi }) {
  const [users, setUsers] = useState<UserSummary[]>([])
  const [userId, setUserId] = useState('')
  const [month, setMonth] = useState(iraqToday().slice(0, 7))
  const [data, setData] = useState<KpiCompare | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    admin.users().then(setUsers, () => setUsers([]))
  }, [admin])

  useEffect(() => {
    let active = true
    admin.kpiCompare(month, userId || undefined).then(
      (d) => active && (setData(d), setError(null)),
      (e: unknown) => active && setError(errorMessage(e)),
    )
    return () => {
      active = false
    }
  }, [admin, month, userId])

  const columns = data ? [data.current, data.previous, data.lastYear] : []
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <h3 className="font-medium">{t('web.kpiCompare.title')}</h3>
        {users.length > 0 && (
          <select aria-label={t('web.kpiCompare.user')} className="rounded border px-2 py-1 text-sm" value={userId} onChange={(e) => setUserId(e.target.value)}>
            <option value="">{t('web.kpiCompare.me')}</option>
            {users.map((u) => (
              <option key={u.id} value={u.id}>{u.email ?? u.id}</option>
            ))}
          </select>
        )}
        <label className="text-sm">
          {t('web.kpiCompare.month')}{' '}
          <input type="month" className="rounded border px-2 py-1" max={iraqToday().slice(0, 7)} value={month} onChange={(e) => e.target.value && setMonth(e.target.value)} />
        </label>
      </div>
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      {data && (
        <>
          <p className="text-sm text-slate-600">{data.email ?? data.userId}</p>
          <table className="w-full text-start text-sm">
            <thead>
              <tr className="border-b">
                <th className="py-2">{t('web.kpiCompare.metric')}</th>
                <th>{data.current.month}</th>
                <th>{data.previous.month} ({t('web.kpiCompare.previous')})</th>
                <th>{data.lastYear.month} ({t('web.kpiCompare.lastYear')})</th>
                <th>{t('web.kpiCompare.vsPrevious')}</th>
                <th>{t('web.kpiCompare.vsLastYear')}</th>
              </tr>
            </thead>
            <tbody>
              {kpiMetrics.map((m) => {
                const [current, previous, lastYear] = columns.map((c) => m.value(c))
                const toPrevious = kpiChange(m, current ?? null, previous ?? null)
                const toLastYear = kpiChange(m, current ?? null, lastYear ?? null)
                return (
                  <tr key={m.id} className="border-b">
                    <td className="py-2">{t(m.label)}</td>
                    <td className="font-semibold">{formatKpi(m, current ?? null)}</td>
                    <td>{formatKpi(m, previous ?? null)}</td>
                    <td>{formatKpi(m, lastYear ?? null)}</td>
                    <td className={tone(toPrevious.better)}>{toPrevious.text}</td>
                    <td className={tone(toLastYear.better)}>{toLastYear.text}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          <h4 className="font-medium">{t('web.kpiCompare.trend')}</h4>
          <table className="w-full text-start text-sm">
            <thead>
              <tr className="border-b">
                <th className="py-2">{t('web.kpiCompare.month')}</th>
                {kpiMetrics.map((m) => (
                  <th key={m.id}>{t(m.label)}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {data.trend.map((p) => (
                <tr key={p.month} className={`border-b ${p.month === data.month ? 'font-semibold' : ''}`}>
                  <td className="py-2">{p.month}</td>
                  {kpiMetrics.map((m) => (
                    <td key={m.id}>{formatKpi(m, m.value(p))}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}
