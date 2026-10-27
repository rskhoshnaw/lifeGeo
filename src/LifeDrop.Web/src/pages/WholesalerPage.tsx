import { useCallback, useState } from 'react'
import { AdminApi } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })

// Orders of the wholesalers (plan step 5.6) for the wholesaler officer: choose the wholesaler, press "dispatched" when the order is sent, print the
// list or save it as a PDF (the browser print dialog). No preparation and no driver. The server allows only the officer's own wholesalers.
export function WholesalerPage({ admin }: { admin: AdminApi }) {
  const [warehouseId, setWarehouseId] = useState('')
  const [sent, setSent] = useState(false)
  const wholesalers = useLoad(useCallback(() => admin.wholesalers(), [admin]))
  const orders = useLoad(useCallback(() => admin.wholesalerOrders(warehouseId, sent ? 'dispatched' : ''), [admin, warehouseId, sent]))
  const action = useAction(() => {
    orders.reload()
    wholesalers.reload()
  })

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3 print:hidden">
        <h2 className="text-xl font-semibold">{t('web.wholesaler.title')}</h2>
        <select aria-label={t('web.wholesaler.choose')} className="rounded border px-2 py-1 text-sm" value={warehouseId} onChange={(e) => setWarehouseId(e.target.value)}>
          <option value="">{t('web.wholesaler.all')}</option>
          {(wholesalers.data ?? []).map((w) => (
            <option key={w.id} value={w.id}>
              {w.name} ({w.openOrders})
            </option>
          ))}
        </select>
        <label className="flex items-center gap-1 text-sm">
          <input type="checkbox" checked={sent} onChange={(e) => setSent(e.target.checked)} />
          {t('web.wholesaler.showSent')}
        </label>
        <button className="rounded border px-3 py-1 text-sm" disabled={!orders.data?.length} onClick={() => window.print()}>
          {t('web.wholesaler.print')}
        </button>
      </div>
      {(orders.error ?? wholesalers.error ?? action.error) && <p role="alert" className="text-sm text-red-700">{orders.error ?? wholesalers.error ?? action.error}</p>}
      {orders.data?.length === 0 && <p className="text-slate-600">{t('web.wholesaler.none')}</p>}
      {orders.data && orders.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.orders.customer')}</th>
              <th>{t('web.wholesaler.address')}</th>
              <th>{t('web.orders.product')}</th>
              <th>{t('web.orders.quantity')}</th>
              <th>{t('web.orders.date')}</th>
              <th>{t('web.wholesaler.price')}</th>
              <th>{t('web.wholesaler.phone')}</th>
              <th className="print:hidden" />
            </tr>
          </thead>
          <tbody>
            {orders.data.map((o) => (
              <tr key={o.id} className="border-b align-top">
                <td className="py-2">
                  {o.customerName}
                  <br />
                  <span className="text-slate-500">{o.wholesalerName} · {o.number}</span>
                </td>
                <td>{[o.place, o.address].filter(Boolean).join(' · ')}</td>
                <td>
                  {o.lines.map((l) => (
                    <div key={l.productCode}>{l.productName}</div>
                  ))}
                </td>
                <td>
                  {o.lines.map((l) => (
                    <div key={l.productCode}>
                      {money(l.quantity)} {l.unit}
                    </div>
                  ))}
                </td>
                <td>{o.createdAt.slice(0, 10)}</td>
                <td>
                  {money(o.total)} {o.currency}
                </td>
                <td>{[o.phone, o.phone2].filter(Boolean).join(' / ')}</td>
                <td className="text-end print:hidden">
                  {o.status === 'placed' && (
                    <button className="rounded border px-2 py-1" disabled={action.busy} aria-label={t('web.wholesaler.dispatchOf', { number: o.number })} onClick={() => void action.run(() => admin.dispatchOrder(o.id))}>
                      {t('web.wholesaler.dispatch')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
