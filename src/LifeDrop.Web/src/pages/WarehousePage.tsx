import { useCallback, useState } from 'react'
import { AdminApi, type Order } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })
const filters = ['', 'placed', 'preparing', 'ready', 'handed_over', 'cancelled']

// Warehouse work (plan step 2.3): the orders of the warehouses the user works in. Start preparing, say what was really prepared (less than
// ordered needs a reason), hand the order to a driver. The server checks every step again and allows only the orders of the user's warehouses.
export function WarehousePage({ admin }: { admin: AdminApi }) {
  const [status, setStatus] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const orders = useLoad(useCallback(() => admin.warehouseOrders(status), [admin, status]))

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.warehouse.title')}</h2>
        <select aria-label={t('web.warehouse.filter')} className="rounded border px-2 py-1 text-sm" value={status} onChange={(e) => { setStatus(e.target.value); setOpen(null) }}>
          {filters.map((f) => (
            <option key={f} value={f}>
              {f === '' ? t('web.warehouse.open') : t(`web.orders.status.${f}` as TextKey)}
            </option>
          ))}
        </select>
      </div>
      {orders.error && <p role="alert" className="text-sm text-red-700">{orders.error}</p>}
      {orders.data?.length === 0 && <p className="text-slate-600">{t('web.warehouse.none')}</p>}
      {orders.data && orders.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.orders.number')}</th>
              <th>{t('web.orders.customer')}</th>
              <th>{t('web.orders.warehouse')}</th>
              <th>{t('web.orders.status')}</th>
              <th>{t('web.orders.date')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {orders.data.map((o) => (
              <tr key={o.id} className="border-b">
                <td className="py-2">{o.number}</td>
                <td>
                  {o.customerCode} · {o.customerName}
                </td>
                <td>{o.warehouseName}</td>
                <td>{t(`web.orders.status.${o.status}` as TextKey)}</td>
                <td>{o.createdAt.slice(0, 10)}</td>
                <td className="text-end">
                  <button className="rounded border px-2 py-1" aria-label={t('web.orders.details', { number: o.number })} onClick={() => setOpen(open === o.id ? null : o.id)}>
                    {t('web.orders.view')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {open && <WorkOrder key={open} admin={admin} orderId={open} onChanged={orders.reload} />}
    </section>
  )
}

function WorkOrder({ admin, orderId, onChanged }: { admin: AdminApi; orderId: string; onChanged: () => void }) {
  const order = useLoad(useCallback(() => admin.warehouseOrder(orderId), [admin, orderId]))
  const drivers = useLoad(useCallback(() => admin.drivers(), [admin]))
  const action = useAction(() => {
    order.reload()
    onChanged()
  })
  const [quantities, setQuantities] = useState<Record<number, string>>({})
  const [reasons, setReasons] = useState<Record<number, string>>({})
  const [driver, setDriver] = useState('')
  const o: Order | null = order.data

  // What was prepared, by line; empty means "as ordered".
  const prepared = (lineNo: number, ordered: number) => (quantities[lineNo] === undefined || quantities[lineNo] === '' ? ordered : Number(quantities[lineNo]))
  const lines = (o?.items ?? []).map((l) => ({ line: l, quantity: prepared(l.lineNo, l.quantity) }))
  const valid = lines.every((l) => Number.isFinite(l.quantity) && l.quantity >= 0 && l.quantity <= l.line.quantity && (l.quantity === l.line.quantity || (reasons[l.line.lineNo] ?? '').trim() !== ''))
  const short = lines.some((l) => l.quantity < l.line.quantity && (reasons[l.line.lineNo] ?? '').trim() === '')

  function ready() {
    void action.run(() =>
      admin.markReady(orderId, lines.map((l) => ({ lineNo: l.line.lineNo, quantity: l.quantity, reason: l.quantity < l.line.quantity ? (reasons[l.line.lineNo] ?? '').trim() : null }))),
    )
  }

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      {(order.error ?? action.error) && <p role="alert" className="text-red-700">{order.error ?? action.error}</p>}
      {o && (
        <>
          <h3 className="font-semibold">
            {t('web.orders.details', { number: o.number })} · {t(`web.orders.status.${o.status}` as TextKey)}
          </h3>
          {o.note && <p>{t('web.orders.note')}: {o.note}</p>}
          <table className="w-full text-start">
            <thead>
              <tr className="border-b">
                <th className="py-1">{t('web.orders.product')}</th>
                <th>{t('web.orders.quantity')}</th>
                <th>{t('web.orders.prepared')}</th>
              </tr>
            </thead>
            <tbody>
              {o.items.map((l) => (
                <tr key={l.lineNo} className="border-b align-top">
                  <td className="py-1">
                    {l.productCode} · {l.productName}
                  </td>
                  <td>
                    {money(l.quantity)} {l.unit}
                  </td>
                  <td>
                    {o.status === 'preparing' ? (
                      <div className="flex flex-wrap gap-2">
                        <input aria-label={t('web.warehouse.quantityFor', { code: l.productCode })} type="number" min={0} max={l.quantity} step="any" className="w-24 rounded border px-2 py-1" placeholder={String(l.quantity)} value={quantities[l.lineNo] ?? ''} onChange={(e) => setQuantities({ ...quantities, [l.lineNo]: e.target.value })} />
                        {prepared(l.lineNo, l.quantity) < l.quantity && (
                          <input aria-label={t('web.warehouse.reasonFor', { code: l.productCode })} placeholder={t('web.orders.shortage')} maxLength={300} className="min-w-48 rounded border px-2 py-1" value={reasons[l.lineNo] ?? ''} onChange={(e) => setReasons({ ...reasons, [l.lineNo]: e.target.value })} />
                        )}
                      </div>
                    ) : l.preparedQuantity == null ? (
                      ''
                    ) : (
                      `${money(l.preparedQuantity)}${l.shortageReason ? ` (${l.shortageReason})` : ''}`
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {o.status === 'placed' && (
            <button className="rounded border px-3 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.startPreparing(orderId))}>
              {t('web.warehouse.start')}
            </button>
          )}
          {o.status === 'preparing' && (
            <>
              {short && <p className="text-amber-700">{t('web.warehouse.needReason')}</p>}
              <button className="rounded border px-3 py-1" disabled={action.busy || !valid} onClick={ready}>
                {t('web.warehouse.ready')}
              </button>
            </>
          )}
          {o.status === 'ready' && (
            <div className="flex flex-wrap items-center gap-2">
              <select aria-label={t('web.orders.driver')} className="rounded border px-2 py-1" value={driver} onChange={(e) => setDriver(e.target.value)}>
                <option value="">{t('web.warehouse.chooseDriver')}</option>
                {(drivers.data ?? []).map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.email ?? d.id}
                  </option>
                ))}
              </select>
              <button className="rounded border px-3 py-1" disabled={action.busy || !driver} onClick={() => void action.run(() => admin.handOver(orderId, driver))}>
                {t('web.warehouse.handOver')}
              </button>
              {drivers.data?.length === 0 && <span className="text-amber-700">{t('web.warehouse.noDrivers')}</span>}
            </div>
          )}
          {o.driverEmail && <p>{t('web.orders.driver')}: {o.driverEmail}</p>}
          {o.history && (
            <div>
              <p className="font-semibold">{t('web.orders.history')}</p>
              {o.history.map((h, i) => (
                <p key={i} className="text-slate-600">
                  {h.at.slice(0, 16).replace('T', ' ')} · {t(`web.orders.status.${h.status}` as TextKey)} · {h.actorEmail ?? ''}{h.note ? ` · ${h.note}` : ''}
                </p>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  )
}
