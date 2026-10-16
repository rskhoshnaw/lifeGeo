import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, errorMessage, iraqToday, Permissions, type EffectivePrice, type Me, type Order, type Warehouse } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })
const discountText = (d: { kind: 'percent' | 'amount'; value: number }) => (d.kind === 'percent' ? `-${money(d.value)}%` : `-${money(d.value)}`)

// Orders (plan step 2.2): the list, one order with its lines and cancel, a new order, and the warehouses. Prices always come from the
// server; the form shows what the server gives now, and the server sets the final price when the order is placed.
export function OrdersPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const first = useLoad(useCallback(() => admin.orders(), [admin]))
  const [more, setMore] = useState<Order[]>([])
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const [placed, setPlaced] = useState<string | null>(null)
  const items = [...(first.data ?? []), ...more]

  async function loadMore() {
    try {
      const page = await admin.orders(items[items.length - 1])
      setMore([...more, ...page])
      if (page.length < 50) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const refresh = () => {
    setMore([])
    setDone(false)
    first.reload()
  }

  return (
    <section className="space-y-6 p-6">
      <h2 className="text-xl font-semibold">{t('web.orders.title')}</h2>
      {placed && <p role="status" className="text-sm text-green-700">{placed}</p>}
      {(error ?? first.error) && <p role="alert" className="text-sm text-red-700">{error ?? first.error}</p>}
      {first.data?.length === 0 && <p className="text-slate-600">{t('web.orders.none')}</p>}
      {first.data && first.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.orders.number')}</th>
              <th>{t('web.orders.customer')}</th>
              <th>{t('web.orders.warehouse')}</th>
              <th>{t('web.orders.total')}</th>
              <th>{t('web.orders.status')}</th>
              <th>{t('web.orders.date')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((o) => (
              <tr key={o.id} className="border-b">
                <td className="py-2">{o.number}</td>
                <td>
                  {o.customerCode} · {o.customerName}
                </td>
                <td>{o.warehouseName}</td>
                <td>
                  {money(o.total)} {o.currency}
                </td>
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
      {first.data && first.data.length === 50 && !done && (
        <button className="rounded border px-3 py-1 text-sm" onClick={() => void loadMore()}>
          {t('web.action.loadMore')}
        </button>
      )}
      {open && <OrderDetails key={open} admin={admin} me={me} orderId={open} onChanged={refresh} />}
      {can(me, ...Permissions.createOrders) && (
        <NewOrderForm
          admin={admin}
          onPlaced={(message) => {
            setPlaced(message)
            refresh()
          }}
        />
      )}
      {can(me, ...Permissions.manageWarehouses) && <Warehouses admin={admin} />}
    </section>
  )
}

function OrderDetails({ admin, me, orderId, onChanged }: { admin: AdminApi; me: Me; orderId: string; onChanged: () => void }) {
  const order = useLoad(useCallback(() => admin.order(orderId), [admin, orderId]))
  const action = useAction(() => {
    order.reload()
    onChanged()
  })
  const o = order.data
  // The creator cancels while it is only placed; a manager until it is handed to a driver.
  const canCancel = o !== undefined && o !== null && ((o.status === 'placed' && o.createdBy === me.id) || (['placed', 'preparing', 'ready'].includes(o.status) && can(me, ...Permissions.cancelOrders)))

  function cancel() {
    if (!o) return
    const reason = window.prompt(t('web.orders.cancelPrompt', { number: o.number }))?.trim()
    if (reason) void action.run(() => admin.cancelOrder(o.id, reason))
  }

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      {(order.error ?? action.error) && <p role="alert" className="text-red-700">{order.error ?? action.error}</p>}
      {o && (
        <>
          <h3 className="font-semibold">{t('web.orders.details', { number: o.number })}</h3>
          {o.note && <p>{t('web.orders.note')}: {o.note}</p>}
          {o.status === 'cancelled' && <p className="text-red-700">{t('web.orders.cancelled', { reason: o.cancelReason ?? '' })}</p>}
          <table className="w-full text-start">
            <thead>
              <tr className="border-b">
                <th className="py-1">{t('web.orders.product')}</th>
                <th>{t('web.orders.quantity')}</th>
                <th>{t('web.orders.unitPrice')}</th>
                <th>{t('web.orders.discounts')}</th>
                <th>{t('web.orders.lineTotal')}</th>
                <th>{t('web.orders.prepared')}</th>
                <th>{t('web.orders.deliveredQuantity')}</th>
              </tr>
            </thead>
            <tbody>
              {o.items.map((l) => (
                <tr key={l.lineNo} className="border-b">
                  <td className="py-1">
                    {l.productCode} · {l.productName}
                  </td>
                  <td>
                    {money(l.quantity)} {l.unit}
                  </td>
                  <td>{money(l.unitPrice)}</td>
                  <td>{l.discounts.map(discountText).join(' ')}</td>
                  <td>{money(l.lineTotal)}</td>
                  <td>{l.preparedQuantity == null ? '' : `${money(l.preparedQuantity)}${l.shortageReason ? ` (${l.shortageReason})` : ''}`}</td>
                  <td>{l.deliveredQuantity == null ? '' : `${money(l.deliveredQuantity)}${l.deliveryReason ? ` (${l.deliveryReason})` : ''}`}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="font-semibold">
            {t('web.orders.total')}: {money(o.total)} {o.currency}
          </p>
          {o.driverEmail && <p>{t('web.orders.driver')}: {o.driverEmail}</p>}
          {o.delivery?.receiverName && <p>{t('web.orders.receiver')}: {o.delivery.receiverName}{o.delivery.deliveryNote ? ` · ${o.delivery.deliveryNote}` : ''}</p>}
          {o.delivery?.failureReason && <p className="text-red-700">{t('web.orders.failure', { reason: o.delivery.failureReason })}</p>}
          {o.history && o.history.length > 0 && (
            <div>
              <p className="font-semibold">{t('web.orders.history')}</p>
              {o.history.map((h, i) => (
                <p key={i} className="text-slate-600">
                  {h.at.slice(0, 16).replace('T', ' ')} · {t(`web.orders.status.${h.status}` as TextKey)} · {h.actorEmail ?? ''}{h.note ? ` · ${h.note}` : ''}{h.distanceMeters != null ? ` · ${t('web.orders.distance', { meters: h.distanceMeters })}` : ''}
                </p>
              ))}
            </div>
          )}
          {canCancel && (
            <button className="rounded border px-3 py-1 text-red-700" disabled={action.busy} onClick={cancel}>
              {t('web.orders.cancel')}
            </button>
          )}
        </>
      )}
    </div>
  )
}

interface Line {
  productId: string
  quantity: string
}

function NewOrderForm({ admin, onPlaced }: { admin: AdminApi; onPlaced: (message: string) => void }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const warehouses = useLoad(useCallback(() => admin.warehouses(), [admin]))
  const [customer, setCustomer] = useState('')
  const [warehouse, setWarehouse] = useState('')
  const [lines, setLines] = useState<Line[]>([])
  const [productId, setProductId] = useState('')
  const [quantity, setQuantity] = useState('')
  const [note, setNote] = useState('')
  // One reference per order form: a retry of a lost answer returns the same order instead of making a second one.
  const [clientRef, setClientRef] = useState(() => crypto.randomUUID())
  const prices = useLoad(useCallback(() => (customer ? admin.customerPrices(customer, iraqToday()) : Promise.resolve(null)), [admin, customer]))
  const action = useAction()
  const priceOf = (id: string): EffectivePrice | undefined => prices.data?.find((p) => p.productId === id)
  const approved = (customers.data ?? []).filter((c) => c.approvalStatus === 'approved')

  const rows = lines.map((l) => ({ line: l, price: priceOf(l.productId), amount: Number(l.quantity) }))
  const currencies = new Set(rows.map((r) => r.price?.currency))
  const mixed = currencies.size > 1
  const total = rows.reduce((sum, r) => sum + (r.price ? r.price.price * r.amount : 0), 0)
  const currency = rows[0]?.price?.currency ?? ''
  const ready = customer !== '' && warehouse !== '' && lines.length > 0 && !mixed && rows.every((r) => r.price && r.amount > 0)

  function addLine(e: FormEvent) {
    e.preventDefault()
    if (!productId || !(Number(quantity) > 0) || lines.some((l) => l.productId === productId)) return
    setLines([...lines, { productId, quantity }])
    setProductId('')
    setQuantity('')
  }

  async function place() {
    const created = await action.run(() =>
      admin.createOrder({
        customerId: customer, warehouseId: warehouse, clientRef, note: note.trim() || null,
        items: lines.map((l) => ({ productId: l.productId, quantity: Number(l.quantity) })),
      }),
    )
    if (!created) return
    onPlaced(t('web.orders.placed', { number: created.number, total: `${money(created.total)} ${created.currency}` }))
    setLines([])
    setNote('')
    setClientRef(crypto.randomUUID())
  }

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.orders.new')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.orders.customer')} className="rounded border px-2 py-1" value={customer} onChange={(e) => { setCustomer(e.target.value); setLines([]) }}>
          <option value="">{t('web.orders.chooseCustomer')}</option>
          {approved.map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {c.name}
            </option>
          ))}
        </select>
        <select aria-label={t('web.orders.warehouse')} className="rounded border px-2 py-1" value={warehouse} onChange={(e) => setWarehouse(e.target.value)}>
          <option value="">{t('web.orders.chooseWarehouse')}</option>
          {(warehouses.data ?? []).map((w) => (
            <option key={w.id} value={w.id}>
              {w.code} · {w.name}
            </option>
          ))}
        </select>
      </div>
      {customer && (
        <form onSubmit={addLine} className="flex flex-wrap items-center gap-2">
          <select aria-label={t('web.orders.product')} className="rounded border px-2 py-1" value={productId} onChange={(e) => setProductId(e.target.value)}>
            <option value="">{t('web.orders.chooseProduct')}</option>
            {(prices.data ?? []).map((p) => (
              <option key={p.productId} value={p.productId}>
                {p.code} · {p.name} ({money(p.price)} {p.currency})
              </option>
            ))}
          </select>
          <input aria-label={t('web.orders.quantity')} placeholder={t('web.orders.quantity')} type="number" min={1} step="any" className="w-28 rounded border px-2 py-1" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
          <button type="submit" className="rounded border px-3 py-1" disabled={!productId || !(Number(quantity) > 0)}>
            {t('web.orders.addLine')}
          </button>
        </form>
      )}
      {prices.error && <p role="alert" className="text-red-700">{prices.error}</p>}
      {rows.length > 0 && (
        <>
          <p className="text-slate-600">{t('web.orders.estimate')}</p>
          <table className="w-full text-start">
            <tbody>
              {rows.map((r) => (
                <tr key={r.line.productId} className="border-b">
                  <td className="py-1">
                    {r.price?.code} · {r.price?.name}
                  </td>
                  <td>
                    {money(r.amount)} × {money(r.price?.price ?? 0)} {r.price?.currency}
                  </td>
                  <td>{r.price ? money(r.price.price * r.amount) : ''}</td>
                  <td className="text-end">
                    <button className="rounded border px-2 py-1" aria-label={t('web.orders.removeLine', { code: r.price?.code ?? '' })} onClick={() => setLines(lines.filter((l) => l.productId !== r.line.productId))}>
                      ×
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="font-semibold">
            {t('web.orders.total')}: {money(total)} {currency}
          </p>
          {mixed && <p role="alert" className="text-red-700">{t('web.orders.mixedCurrency')}</p>}
          <input aria-label={t('web.orders.note')} placeholder={t('web.orders.note')} maxLength={500} className="w-full rounded border px-2 py-1" value={note} onChange={(e) => setNote(e.target.value)} />
          <button className="rounded border px-3 py-1" disabled={!ready || action.busy} onClick={() => void place()}>
            {t('web.orders.place')}
          </button>
        </>
      )}
    </div>
  )
}

function Warehouses({ admin }: { admin: AdminApi }) {
  const list = useLoad(useCallback(() => admin.warehouses(true), [admin]))
  const action = useAction(list.reload)
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [staffFor, setStaffFor] = useState<Warehouse | null>(null)

  function add(e: FormEvent) {
    e.preventDefault()
    void action.run(async () => {
      await admin.createWarehouse(code.trim(), name.trim())
      setCode('')
      setName('')
    })
  }

  const toggle = (w: Warehouse) => void action.run(() => admin.updateWarehouse(w.id, w.name, w.status === 'active' ? 'inactive' : 'active'))

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.orders.warehouses')}</h3>
      {(action.error ?? list.error) && <p role="alert" className="text-red-700">{action.error ?? list.error}</p>}
      {list.data?.map((w) => (
        <div key={w.id} className="flex items-center gap-3 border-b py-1">
          <span className="min-w-20">{w.code}</span>
          <span className="flex-1">
            {w.name} {w.status === 'inactive' && <em>({t('web.orders.inactive')})</em>}
          </span>
          <button className="rounded border px-2 py-1" aria-label={t('web.orders.staffOf', { name: w.name })} onClick={() => setStaffFor(staffFor?.id === w.id ? null : w)}>
            {t('web.orders.staff')}
          </button>
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => toggle(w)}>
            {t(w.status === 'active' ? 'web.orders.deactivate' : 'web.orders.activate')}
          </button>
        </div>
      ))}
      {staffFor && <WarehouseStaff key={staffFor.id} admin={admin} warehouse={staffFor} />}
      <form onSubmit={add} className="flex flex-wrap items-center gap-2">
        <input aria-label={t('web.orders.warehouseCode')} placeholder={t('web.orders.warehouseCode')} maxLength={40} className="rounded border px-2 py-1" value={code} onChange={(e) => setCode(e.target.value)} />
        <input aria-label={t('web.orders.warehouseName')} placeholder={t('web.orders.warehouseName')} maxLength={160} className="rounded border px-2 py-1" value={name} onChange={(e) => setName(e.target.value)} />
        <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !code.trim() || !name.trim()}>
          {t('web.orders.addWarehouse')}
        </button>
      </form>
    </div>
  )
}

// Who works in a warehouse: only users whose role is WarehouseManager can be added (the server checks it).
function WarehouseStaff({ admin, warehouse }: { admin: AdminApi; warehouse: Warehouse }) {
  const staff = useLoad(useCallback(() => admin.warehouseStaff(warehouse.id), [admin, warehouse.id]))
  const users = useLoad(useCallback(() => admin.users(), [admin]))
  const action = useAction(staff.reload)
  const [user, setUser] = useState('')
  const inside = new Set((staff.data ?? []).map((s) => s.userId))
  const candidates = (users.data ?? []).filter((u) => u.roles.includes('WarehouseManager') && !inside.has(u.id))

  return (
    <div className="space-y-2 rounded border bg-slate-50 p-2">
      <h4 className="font-semibold">{t('web.orders.staffOf', { name: warehouse.name })}</h4>
      {(action.error ?? staff.error) && <p role="alert" className="text-red-700">{action.error ?? staff.error}</p>}
      {staff.data?.length === 0 && <p className="text-slate-600">{t('web.orders.noStaff')}</p>}
      {staff.data?.map((s) => (
        <div key={s.userId} className="flex items-center gap-3">
          <span className="flex-1">{s.email ?? s.userId}</span>
          <button className="rounded border px-2 py-1" aria-label={t('web.orders.removeStaff', { name: s.email ?? s.userId })} disabled={action.busy} onClick={() => void action.run(() => admin.removeWarehouseStaff(warehouse.id, s.userId))}>
            ×
          </button>
        </div>
      ))}
      <div className="flex items-center gap-2">
        <select aria-label={t('web.orders.chooseStaff')} className="rounded border px-2 py-1" value={user} onChange={(e) => setUser(e.target.value)}>
          <option value="">{t('web.orders.chooseStaff')}</option>
          {candidates.map((u) => (
            <option key={u.id} value={u.id}>
              {u.email ?? u.id}
            </option>
          ))}
        </select>
        <button className="rounded border px-3 py-1" disabled={action.busy || !user} onClick={() => void action.run(async () => { await admin.addWarehouseStaff(warehouse.id, user); setUser('') })}>
          {t('web.orders.addStaff')}
        </button>
      </div>
    </div>
  )
}
