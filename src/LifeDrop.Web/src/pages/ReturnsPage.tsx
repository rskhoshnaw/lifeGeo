import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { AdminApi, can, iraqToday, Permissions, returnReasons, type CustomerReturn, type Me, type ReturnDestination, type ReturnReason } from '../api/admin.ts'
import { Badge, type Tone } from '../components/Badge.tsx'
import { useAction, useLoad } from '../components/useLoad.ts'
import { t, type TextKey } from '../i18n.ts'
import { unitLabel } from './productView.ts'

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'short' })
const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })
const tones: Record<CustomerReturn['status'], Tone> = { requested: 'warn', approved: 'good', rejected: 'bad', cancelled: 'neutral' }
const statuses = ['', 'requested', 'approved', 'rejected', 'cancelled']

// Damaged and expired goods go apart by default; every other reason goes back to the warehouse (the author may change it per line).
export const defaultDestination = (reason: ReturnReason): ReturnDestination => (reason === 'damaged' || reason === 'expired' ? 'damaged' : 'warehouse')

// Customer returns (plan step 5.54): the list of what the field sent back, with the goods of each, and for the sales manager approve / reject. Below, the damaged and expired goods of a period.
export function ReturnsPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [status, setStatus] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const list = useLoad(useCallback(() => admin.returns(status), [admin, status]))
  const canApprove = can(me, ...Permissions.approveReturns)
  const canCreate = can(me, ...Permissions.createReturns)
  const action = useAction(() => list.reload())

  function approve(r: CustomerReturn) {
    const note = window.prompt(t('web.returns.approveNote'))
    if (note !== null) void action.run(() => admin.approveReturn(r.id, note.trim() || null))
  }
  function approveLines(r: CustomerReturn, lines: number[]) {
    const note = window.prompt(t('web.returns.approveNote'))
    if (note !== null) void action.run(() => admin.approveReturn(r.id, note.trim() || null, lines))
  }
  function reject(r: CustomerReturn) {
    const reason = window.prompt(t('web.returns.rejectReason'))?.trim()
    if (reason) void action.run(() => admin.rejectReturn(r.id, reason))
  }
  function cancel(r: CustomerReturn) {
    const reason = window.prompt(t('web.returns.cancelReason'))?.trim()
    if (reason) void action.run(() => admin.cancelReturn(r.id, reason))
  }

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.returns.title')}</h2>
        <select aria-label={t('web.returns.status')} className="rounded border px-2 py-1 text-sm" value={status} onChange={(e) => setStatus(e.target.value)}>
          {statuses.map((s) => (
            <option key={s} value={s}>{s ? t(`web.returns.status.${s}` as TextKey) : t('web.returns.all')}</option>
          ))}
        </select>
        {canCreate && (
          <button className="rounded border px-3 py-1 text-sm" onClick={() => setCreating(!creating)}>
            {t('web.returns.new')}
          </button>
        )}
      </div>
      {message && <p role="status" className="text-sm text-green-700">{message}</p>}
      {(list.error ?? action.error) && <p role="alert" className="text-sm text-red-700">{list.error ?? action.error}</p>}
      {creating && (
        <NewReturnForm
          admin={admin}
          onSent={(text) => {
            setMessage(text)
            setCreating(false)
            list.reload()
          }}
        />
      )}
      {list.data && list.data.length === 0 && <p className="text-sm text-slate-500">{t('web.returns.none')}</p>}
      {list.data && list.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.returns.number')}</th>
              <th>{t('web.returns.date')}</th>
              <th>{t('web.returns.customer')}</th>
              <th>{t('web.returns.status')}</th>
              <th>{t('web.returns.total')}</th>
              <th>{t('web.returns.by')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {list.data.map((r) => (
              <ReturnRow key={r.id} admin={admin} r={r} me={me} open={open === r.id} onToggle={() => setOpen(open === r.id ? null : r.id)}
                onApprove={canApprove ? () => approve(r) : undefined} onApproveLines={canApprove ? (lines) => approveLines(r, lines) : undefined} onReject={canApprove ? () => reject(r) : undefined} onCancel={r.createdBy === me.id ? () => cancel(r) : undefined} busy={action.busy} />
            ))}
          </tbody>
        </table>
      )}
      <DamagedGoods admin={admin} />
    </section>
  )
}

// `pick` (step 5.56): the line numbers the sales manager has ticked to approve; the table then has a tick box in front of every line.
export function ReturnLinesTable({ r, pick }: { r: CustomerReturn; pick?: { selected: ReadonlySet<number>; onToggle: (lineNo: number) => void } }) {
  return (
    <table className="w-full text-start text-sm">
      <thead>
        <tr className="border-b">
          {pick && <th />}
          <th className="py-1">{t('web.returns.product')}</th>
          <th>{t('web.returns.quantity')}</th>
          <th>{t('web.returns.reason')}</th>
          <th>{t('web.returns.destination')}</th>
          <th>{t('web.returns.total')}</th>
        </tr>
      </thead>
      <tbody>
        {r.items.map((l) => (
          <tr key={l.lineNo} className={`border-b ${l.decision === 'rejected' ? 'text-slate-400 line-through' : ''}`}>
            {pick && (
              <td>
                <input type="checkbox" aria-label={`${t('web.returns.approve')} ${l.productName}`} checked={pick.selected.has(l.lineNo)} onChange={() => pick.onToggle(l.lineNo)} />
              </td>
            )}
            <td className="py-1">{l.productName}{l.decision === 'rejected' && <span className="ms-1 no-underline">· {t('web.returns.lineRejected')}</span>}</td>
            <td>{money(l.quantity)} {unitLabel(l.unit)}</td>
            <td>{t(`web.returns.reason.${l.reason}` as TextKey)}</td>
            <td>
              {t(`web.returns.dest.${l.destination}` as TextKey)}
              {r.status === 'approved' && l.destination === 'warehouse' && l.stockApplied === false && <div className="text-xs text-amber-700">{t('web.returns.notOnShelf')}</div>}
            </td>
            <td>{r.pricesHidden ? '—' : `${money(l.lineTotal)} ${r.currency}`}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function ReturnRow({ admin, r, open, onToggle, onApprove, onApproveLines, onReject, onCancel, busy }: {
  admin: AdminApi; r: CustomerReturn; me: Me; open: boolean; onToggle: () => void; onApprove?: () => void; onApproveLines?: (lines: number[]) => void; onReject?: () => void; onCancel?: () => void; busy: boolean
}) {
  const requested = r.status === 'requested'
  // Row by row (step 5.56): every line starts ticked; "Approve selected" approves the ticked ones and rejects the others.
  const [selected, setSelected] = useState<Set<number>>(() => new Set(r.items.map((l) => l.lineNo)))
  const toggle = (lineNo: number) => setSelected((s) => { const next = new Set(s); if (next.has(lineNo)) next.delete(lineNo); else next.add(lineNo); return next })
  const picking = requested && onApproveLines !== undefined
  return (
    <>
      <tr className="border-b">
        <td className="py-2">
          <button className="text-brand-700 underline" aria-expanded={open} aria-label={`${t('web.returns.lines')} ${r.number}`} onClick={onToggle}>{open ? '▾ ' : '▸ '}{r.number}</button>
        </td>
        <td>{when.format(new Date(r.createdAt))}</td>
        <td>{r.customerCode} · {r.customerName}</td>
        <td><Badge tone={tones[r.status]}>{t(`web.returns.status.${r.status}` as TextKey)}</Badge></td>
        <td>{r.pricesHidden ? '—' : `${money(r.total)} ${r.currency}`}</td>
        <td>{r.createdByEmail ?? '—'}</td>
        <td className="space-x-2 text-end">
          {requested && onApprove && <button className="rounded border px-2 py-1" aria-label={`${t('web.returns.approve')} ${r.number}`} disabled={busy} onClick={onApprove}>{t('web.returns.approve')}</button>}
          {requested && onReject && <button className="rounded border px-2 py-1" aria-label={`${t('web.returns.reject')} ${r.number}`} disabled={busy} onClick={onReject}>{t('web.returns.reject')}</button>}
          {requested && onCancel && <button className="rounded border px-2 py-1" aria-label={`${t('web.returns.cancel')} ${r.number}`} disabled={busy} onClick={onCancel}>{t('web.returns.cancel')}</button>}
        </td>
      </tr>
      {open && (
        <tr className="border-b bg-slate-50">
          <td colSpan={7} className="p-3">
            {r.orderNumber && <p className="mb-1 text-xs text-slate-600">{t('web.returns.order')}: {r.orderNumber}{r.warehouseName ? ` · ${r.warehouseName}` : ''}</p>}
            {r.note && <p className="mb-1 text-sm">{r.note}</p>}
            <ReturnLinesTable r={r} pick={picking ? { selected, onToggle: toggle } : undefined} />
            {picking && selected.size > 0 && selected.size < r.items.length && (
              <button className="mt-2 rounded border px-3 py-1" disabled={busy} onClick={() => onApproveLines([...selected].sort((a, b) => a - b))}>
                {t('web.returns.approveSelected', { count: selected.size, total: r.items.length })}
              </button>
            )}
            <ReturnPhotos admin={admin} r={r} />
            {r.decidedAt && <p className="mt-2 text-xs text-slate-600">{t('web.returns.decision')}: {t(`web.returns.status.${r.status}` as TextKey)} · {r.decidedByEmail ?? '—'} · {when.format(new Date(r.decidedAt))}{r.decisionNote ? ` — ${r.decisionNote}` : ''}</p>}
          </td>
        </tr>
      )}
    </>
  )
}

interface DraftLine {
  productId: string
  quantity: string
  reason: ReturnReason
  destination: ReturnDestination
}

// The form of a new return. `fixedCustomer`: for this customer only (opened from the customer's file). With an order the products and prices are the order's and no more than was delivered
// (minus what other returns took) can come back; without an order the customer's price of today is used. The server checks all of it again.
export function NewReturnForm({ admin, onSent, fixedCustomer }: { admin: AdminApi; onSent: (message: string) => void; fixedCustomer?: { id: string; code: string; name: string } }) {
  const customers = useLoad(useCallback(() => (fixedCustomer ? Promise.resolve([]) : admin.customers()), [admin, fixedCustomer]))
  const orders = useLoad(useCallback(() => admin.orders(), [admin]))
  const warehouses = useLoad(useCallback(() => admin.warehouses(), [admin]))
  const products = useLoad(useCallback(() => admin.products(false), [admin]))
  const [customer, setCustomer] = useState(fixedCustomer?.id ?? '')
  const [orderId, setOrderId] = useState('')
  const [warehouse, setWarehouse] = useState('')
  const [note, setNote] = useState('')
  const [lines, setLines] = useState<DraftLine[]>([])
  const [draft, setDraft] = useState<DraftLine>({ productId: '', quantity: '', reason: 'damaged', destination: 'damaged' })
  // One reference per form: a retry of a lost answer returns the same return instead of a second one.
  const [clientRef, setClientRef] = useState(() => crypto.randomUUID())
  const action = useAction()

  const delivered = (orders.data ?? []).filter((o) => o.customerId === customer && o.status === 'delivered')
  // The list of orders has no lines: the lines of the chosen order are read when it is chosen.
  const orderDetails = useLoad(useCallback(() => (orderId ? admin.order(orderId) : Promise.resolve(null)), [admin, orderId]))
  const order = orderId ? orderDetails.data : null
  const choices = order
    ? order.items.map((l) => ({ id: l.productId, label: `${l.productCode} · ${l.productName} (${money(l.deliveredQuantity ?? l.quantity)})` }))
    : orderId ? [] : (products.data ?? []).map((p) => ({ id: p.id, label: `${p.code} · ${p.name}` }))
  const nameOf = (id: string) => choices.find((c) => c.id === id)?.label ?? id
  // The warehouse is always needed: goods that go back join its stock, damaged and expired goods are held apart in it (step 5.56).
  const ready = customer !== '' && lines.length > 0 && warehouse !== ''

  function addLine(e: FormEvent) {
    e.preventDefault()
    if (!draft.productId || !(Number(draft.quantity) > 0) || lines.some((l) => l.productId === draft.productId)) return
    setLines([...lines, draft])
    setDraft({ ...draft, productId: '', quantity: '' })
  }

  async function send() {
    const created = await action.run(() =>
      admin.createReturn({
        customerId: customer, orderId: orderId || null, warehouseId: warehouse || null, note: note.trim() || null, clientRef,
        items: lines.map((l) => ({ productId: l.productId, quantity: Number(l.quantity), reason: l.reason, destination: l.destination })),
      }),
    )
    if (!created) return
    onSent(t('web.returns.sent', { number: created.number }))
    setLines([])
    setNote('')
    setOrderId('')
    setClientRef(crypto.randomUUID())
  }

  return (
    <div className="space-y-3 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.returns.new')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.returns.customer')} className="rounded border px-2 py-1" disabled={!!fixedCustomer} value={customer} onChange={(e) => { setCustomer(e.target.value); setOrderId(''); setLines([]) }}>
          {!fixedCustomer && <option value="">{t('web.payments.chooseCustomer')}</option>}
          {(fixedCustomer ? [fixedCustomer] : (customers.data ?? [])).map((c) => (
            <option key={c.id} value={c.id}>{c.code} · {c.name}</option>
          ))}
        </select>
        <select aria-label={t('web.returns.order')} className="rounded border px-2 py-1" value={orderId} onChange={(e) => { setOrderId(e.target.value); setLines([]) }}>
          <option value="">{t('web.returns.noOrder')}</option>
          {delivered.map((o) => (
            <option key={o.id} value={o.id}>{o.number} ({money(o.total)} {o.currency})</option>
          ))}
        </select>
        <select aria-label={t('web.returns.warehouse')} className="rounded border px-2 py-1" value={warehouse} onChange={(e) => setWarehouse(e.target.value)}>
          <option value="">{t('web.returns.chooseWarehouse')}</option>
          {(warehouses.data ?? []).filter((w) => w.status === 'active' && (w.kind ?? 'company') === 'company').map((w) => (
            <option key={w.id} value={w.id}>{w.code} · {w.name}</option>
          ))}
        </select>
      </div>
      <form onSubmit={addLine} className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.returns.product')} className="rounded border px-2 py-1" value={draft.productId} onChange={(e) => setDraft({ ...draft, productId: e.target.value })}>
          <option value="">{t('web.returns.chooseProduct')}</option>
          {choices.map((c) => (
            <option key={c.id} value={c.id}>{c.label}</option>
          ))}
        </select>
        <input aria-label={t('web.returns.quantity')} placeholder={t('web.returns.quantity')} inputMode="decimal" className="w-24 rounded border px-2 py-1" value={draft.quantity} onChange={(e) => setDraft({ ...draft, quantity: e.target.value })} />
        <select aria-label={t('web.returns.reason')} className="rounded border px-2 py-1" value={draft.reason} onChange={(e) => { const reason = e.target.value as ReturnReason; setDraft({ ...draft, reason, destination: defaultDestination(reason) }) }}>
          {returnReasons.map((r) => (
            <option key={r} value={r}>{t(`web.returns.reason.${r}` as TextKey)}</option>
          ))}
        </select>
        <select aria-label={t('web.returns.destination')} className="rounded border px-2 py-1" value={draft.destination} onChange={(e) => setDraft({ ...draft, destination: e.target.value as ReturnDestination })}>
          <option value="warehouse">{t('web.returns.dest.warehouse')}</option>
          <option value="damaged">{t('web.returns.dest.damaged')}</option>
        </select>
        <button type="submit" className="rounded border px-3 py-1">{t('web.returns.addLine')}</button>
      </form>
      {lines.length > 0 && (
        <ul className="space-y-1">
          {lines.map((l) => (
            <li key={l.productId} className="flex flex-wrap items-center gap-3">
              <span>{nameOf(l.productId)} × {l.quantity}</span>
              <span className="text-slate-600">{t(`web.returns.reason.${l.reason}` as TextKey)} → {t(`web.returns.dest.${l.destination}` as TextKey)}</span>
              <button type="button" className="rounded border px-2 py-0.5" aria-label={`${t('web.returns.removeLine')} ${nameOf(l.productId)}`} onClick={() => setLines(lines.filter((x) => x !== l))}>{t('web.returns.removeLine')}</button>
            </li>
          ))}
        </ul>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <input aria-label={t('web.returns.note')} placeholder={t('web.returns.note')} className="w-72 rounded border px-2 py-1" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        <button type="button" className="rounded border px-3 py-1" disabled={!ready || action.busy} onClick={() => void send()}>{t('web.returns.send')}</button>
      </div>
    </div>
  )
}

// The pictures of the goods on a return (the author adds up to three in the app), fetched with the sign-in and shown from local addresses.
export function ReturnPhotos({ admin, r }: { admin: AdminApi; r: CustomerReturn }) {
  const [urls, setUrls] = useState<string[]>([])
  const key = (r.photoIds ?? []).join(',')
  useEffect(() => {
    let active = true
    const made: string[] = []
    const ids = key ? key.split(',') : []
    void Promise.all(ids.map((photoId) => admin.returnPhotoUrl(r.id, photoId).catch(() => null))).then((loaded) => {
      const ok = loaded.filter((u): u is string => u !== null)
      made.push(...ok)
      if (active) setUrls(ok)
      else ok.forEach((u) => URL.revokeObjectURL(u))
    })
    return () => {
      active = false
      made.forEach((u) => URL.revokeObjectURL(u))
    }
  }, [admin, r.id, key])
  if (urls.length === 0) return null
  return (
    <div className="mt-2 flex flex-wrap justify-center gap-2">
      {urls.map((url, i) => (
        <a key={url} href={url} target="_blank" rel="noreferrer">
          <img src={url} alt={t('web.returns.photoAlt', { number: i + 1 })} className="h-28 rounded border bg-slate-100 object-contain" />
        </a>
      ))}
    </div>
  )
}

// The approved damaged and expired goods of a period, by product and reason: what was taken back and does not go on the shelf.
function DamagedGoods({ admin }: { admin: AdminApi }) {
  const today = iraqToday()
  const [from, setFrom] = useState(() => new Date(Date.parse(`${today}T00:00:00Z`) - 30 * 86_400_000).toISOString().slice(0, 10))
  const [to, setTo] = useState(today)
  const goods = useLoad(useCallback(() => admin.damagedGoods(from, to).catch(() => []), [admin, from, to]))
  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <div className="flex flex-wrap items-center gap-3">
        <h3 className="font-semibold">{t('web.returns.damagedTitle')}</h3>
        <label className="flex items-center gap-1">{t('web.returns.from')}<input type="date" className="rounded border px-2 py-1" value={from} onChange={(e) => e.target.value && setFrom(e.target.value)} /></label>
        <label className="flex items-center gap-1">{t('web.returns.to')}<input type="date" className="rounded border px-2 py-1" value={to} onChange={(e) => e.target.value && setTo(e.target.value)} /></label>
      </div>
      {goods.data && goods.data.length === 0 && <p className="text-slate-500">{t('web.returns.damagedNone')}</p>}
      {goods.data && goods.data.length > 0 && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-1">{t('web.returns.product')}</th>
              <th>{t('web.returns.reason')}</th>
              <th>{t('web.returns.quantity')}</th>
              <th>{t('web.returns.value')}</th>
            </tr>
          </thead>
          <tbody>
            {goods.data.map((g) => (
              <tr key={`${g.productId}:${g.reason}:${g.currency}`} className="border-b">
                <td className="py-1">{g.productCode} · {g.productName}</td>
                <td>{t(`web.returns.reason.${g.reason}` as TextKey)}</td>
                <td>{money(g.quantity)} {unitLabel(g.unit)}</td>
                <td>{money(g.value)} {g.currency}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
