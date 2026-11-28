import { useEffect, useState } from 'react'
import { AdminApi, errorMessage, type SalesReport, type SalesRow } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { downloadText } from './download.ts'
import { csvCell } from './reportCsv.ts'

const money = (list?: { currency: string; total: number }[] | null) => (list ?? []).map((x) => `${x.total.toLocaleString('en-US', { maximumFractionDigits: 3 })} ${x.currency}`).join(' · ') || '0'

// Sales per warehouse or per city of the customer (plan step 5.74): orders, approved returns and net sales (orders minus returns) per currency, for the day range and filters of the report above.
export function SalesByPlace({ admin, filter }: { admin: AdminApi; filter: { from: string; to: string; regionId: string; teamId: string; customerType: string } }) {
  const [by, setBy] = useState<'warehouse' | 'city'>('warehouse')
  const [report, setReport] = useState<SalesReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let active = true
    admin.salesReport(filter.from, filter.to, by, filter.regionId, filter.teamId, filter.customerType).then(
      (r) => active && (setReport(r), setError(null)),
      (e: unknown) => active && setError(errorMessage(e)),
    )
    return () => {
      active = false
    }
  }, [admin, by, filter.from, filter.to, filter.regionId, filter.teamId, filter.customerType])

  const name = (r: SalesRow) => r.name ?? t('web.reports.noPlace')
  const download = () => {
    if (!report) return
    const head = ['name', 'orders', 'orders delivered', 'order total', 'approved returns', 'returns total', 'net sales']
    const line = (r: SalesRow, label: string) => [label, r.orders, r.ordersDelivered, money(r.orderTotals), r.returnsApproved, money(r.returnTotals), money(r.netSales)].map(csvCell).join(',')
    downloadText(`sales-by-${by}-${report.from}-${report.to}.csv`, ['\ufeff' + head.map(csvCell).join(','), ...report.rows.map((r) => line(r, name(r))), line(report.total, 'TOTAL')].join('\r\n') + '\r\n', 'text/csv;charset=utf-8')
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-3">
        <h3 className="font-medium">{t('web.reports.salesBy')}</h3>
        <select aria-label={t('web.reports.salesBy')} className="rounded border px-2 py-1 text-sm" value={by} onChange={(e) => setBy(e.target.value as 'warehouse' | 'city')}>
          <option value="warehouse">{t('web.reports.byWarehouse')}</option>
          <option value="city">{t('web.reports.byCity')}</option>
        </select>
        {report && report.rows.length > 0 && (
          <button className="rounded border px-3 py-1 text-sm" onClick={download}>
            {t('web.reports.download')}
          </button>
        )}
      </div>
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      {report && report.rows.length === 0 && <p className="text-slate-600">{t('web.reports.none')}</p>}
      {report && report.rows.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t(by === 'warehouse' ? 'web.reports.byWarehouse' : 'web.reports.byCity')}</th>
              <th>{t('web.reports.orders')}</th>
              <th>{t('web.reports.delivery')}</th>
              <th>{t('web.reports.returnsApproved')}</th>
              <th>{t('web.reports.netSales')}</th>
            </tr>
          </thead>
          <tbody>
            {[...report.rows, report.total].map((r, i) => {
              const last = i === report.rows.length
              return (
                <tr key={last ? 'total' : (r.id ?? 'none')} className={`border-b ${last ? 'font-semibold' : ''}`}>
                  <td className="py-2">{last ? t('web.reports.total') : name(r)}</td>
                  <td>
                    {r.orders} {money(r.orderTotals)}
                  </td>
                  <td>
                    {r.ordersDelivered}/{r.orders}
                  </td>
                  <td>
                    {r.returnsApproved} {r.returnsApproved > 0 ? `(${money(r.returnTotals)})` : ''}
                  </td>
                  <td>{money(r.netSales)}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </div>
  )
}
