import { useCallback } from 'react'
import { AdminApi, type Customer } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useLoad } from './useLoad.ts'

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'short' })
const clock = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' })

// The history of one customer (plan step 5.4): every visit with its result (status, report outcome and note, the reason of a failed one, check-in and check-out time) and the orders
// with their products. Needs customers.read; the orders only show for readers who may read orders.
export function CustomerHistoryPanel({ admin, customer, onClose }: { admin: AdminApi; customer: Customer; onClose: () => void }) {
  const history = useLoad(useCallback(() => admin.customerHistory(customer.id), [admin, customer.id]))
  const data = history.data
  return (
    <div className="mt-4 space-y-3 rounded border p-4 text-sm">
      <h3 className="font-semibold">{t('web.history.title', { customer: `${customer.name} (${customer.code})` })}</h3>
      {history.error && <p role="alert" className="text-red-700">{history.error}</p>}
      {data && (
        <>
          <h4 className="font-medium">{t('web.history.visits')}</h4>
          {data.visits.length === 0 && <p className="text-slate-600">{t('web.history.none')}</p>}
          {data.visits.length > 0 && (
            <table className="w-full text-start">
              <thead>
                <tr className="border-b">
                  <th className="py-1">{t('web.history.date')}</th>
                  <th>{t('web.history.by')}</th>
                  <th>{t('web.history.status')}</th>
                  <th>{t('web.history.checkIn')}</th>
                  <th>{t('web.history.result')}</th>
                </tr>
              </thead>
              <tbody>
                {data.visits.map((v) => (
                  <tr key={v.id} className="border-b align-top">
                    <td className="py-1">{v.date}</td>
                    <td>{v.userEmail ?? '—'}</td>
                    <td>{t(`app.status.${v.status}` as TextKey)}</td>
                    <td>{v.checkInAt ? `${clock.format(new Date(v.checkInAt))}${v.checkOutAt ? ` – ${clock.format(new Date(v.checkOutAt))}` : ''}` : '—'}</td>
                    <td>
                      {v.outcome ? <strong>{t(`app.outcome.${v.outcome}` as TextKey)}</strong> : null}
                      {v.note ? ` ${v.note}` : ''}
                      {v.reason ? ` ${v.reason}` : ''}
                      {!v.outcome && !v.note && !v.reason ? '—' : ''}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <h4 className="font-medium">{t('web.history.orders')}</h4>
          {data.orders === null && <p className="text-slate-600">{t('web.history.ordersHidden')}</p>}
          {data.orders?.length === 0 && <p className="text-slate-600">{t('web.history.none')}</p>}
          {data.orders && data.orders.length > 0 && (
            <table className="w-full text-start">
              <thead>
                <tr className="border-b">
                  <th className="py-1">{t('web.history.number')}</th>
                  <th>{t('web.history.date')}</th>
                  <th>{t('web.history.status')}</th>
                  <th>{t('web.history.products')}</th>
                  <th>{t('web.history.total')}</th>
                </tr>
              </thead>
              <tbody>
                {data.orders.map((o) => (
                  <tr key={o.id} className="border-b align-top">
                    <td className="py-1">{o.number}</td>
                    <td>{when.format(new Date(o.createdAt))}</td>
                    <td>{t(`web.orders.status.${o.status}` as TextKey)}</td>
                    <td>{o.lines.map((l) => `${l.productName} × ${l.quantity}${l.deliveredQuantity !== null && l.deliveredQuantity !== l.quantity ? ` (${l.deliveredQuantity})` : ''}`).join('، ')}</td>
                    <td>{o.pricesHidden ? '—' : `${o.total} ${o.currency}`}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
      <button className="rounded border px-2 py-1" onClick={onClose}>
        {t('web.customers.closeHours')}
      </button>
    </div>
  )
}
