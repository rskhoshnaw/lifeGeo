import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, type Transfer, type WarehouseStockRow } from '../api/admin.ts'
import { Badge, type Tone } from '../components/Badge.tsx'
import { useAction, useLoad } from '../components/useLoad.ts'
import { t, type TextKey } from '../i18n.ts'
import { unitLabel } from './productView.ts'

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'short' })
const qty = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })
const tones: Record<Transfer['status'], Tone> = { sent: 'warn', received: 'good', cancelled: 'neutral' }
const statuses = ['', 'sent', 'received', 'cancelled']

// Goods moving between warehouses (plan step 5.60): the central warehouse to the cities, the cities back (returns, damaged goods) and city to city. The sender sends (the goods leave at once),
// the receiver confirms what arrived (a smaller quantity is written down), the sender may cancel until then. The server lets a warehouse worker see and act only for his own warehouses.
export function TransfersPage({ admin }: { admin: AdminApi }) {
  const [status, setStatus] = useState('')
  const [creating, setCreating] = useState(false)
  const [receiving, setReceiving] = useState<Transfer | null>(null)
  const list = useLoad(useCallback(() => admin.transfers(status), [admin, status]))
  const action = useAction(() => list.reload())

  function cancel(tr: Transfer) {
    const note = window.prompt(t('web.transfers.cancelNote'))
    if (note !== null) void action.run(() => admin.cancelTransfer(tr.id, note.trim() || null))
  }

  return (
    <section className="space-y-4 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.transfers.title')}</h2>
        <select aria-label={t('web.transfers.status')} className="rounded border px-2 py-1 text-sm" value={status} onChange={(e) => setStatus(e.target.value)}>
          {statuses.map((s) => (
            <option key={s} value={s}>{s ? t(`web.transfers.status.${s}` as TextKey) : t('web.transfers.all')}</option>
          ))}
        </select>
        <button className="rounded border px-3 py-1 text-sm" onClick={() => setCreating(!creating)}>{t('web.transfers.new')}</button>
      </div>
      {(list.error ?? action.error) && <p role="alert" className="text-sm text-red-700">{list.error ?? action.error}</p>}
      {creating && <NewTransfer admin={admin} onDone={() => { setCreating(false); list.reload() }} />}
      {receiving && (
        <ReceiveForm key={receiving.id} transfer={receiving} busy={action.busy}
          onCancel={() => setReceiving(null)}
          onSave={(lines, note) => void action.run(async () => { await admin.receiveTransfer(receiving.id, lines, note); setReceiving(null) })} />
      )}
      {list.data?.length === 0 && <p className="text-sm text-slate-500">{t('web.transfers.none')}</p>}
      <div className="space-y-3">
        {list.data?.map((tr) => (
          <article key={tr.id} className="rounded border p-3 text-sm">
            <div className="flex flex-wrap items-center gap-3">
              <strong>{tr.number}</strong>
              <span>{tr.fromWarehouseCode} · {tr.fromWarehouseName} → {tr.toWarehouseCode} · {tr.toWarehouseName}</span>
              <Badge tone={tones[tr.status]}>{t(`web.transfers.status.${tr.status}` as TextKey)}</Badge>
              <span className="text-xs text-slate-500">{when.format(new Date(tr.createdAt))}{tr.createdByEmail ? ` · ${tr.createdByEmail}` : ''}</span>
              {tr.status === 'sent' && (
                <span className="ms-auto flex gap-2">
                  <button className="rounded border px-2 py-1" disabled={action.busy} aria-label={t('web.transfers.receiveOf', { number: tr.number })} onClick={() => setReceiving(tr)}>{t('web.transfers.receive')}</button>
                  <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} aria-label={t('web.transfers.cancelOf', { number: tr.number })} onClick={() => cancel(tr)}>{t('web.transfers.cancel')}</button>
                </span>
              )}
            </div>
            <ul className="mt-2 list-disc ps-5">
              {tr.items.map((i) => (
                <li key={i.lineNo}>
                  {i.productCode} · {i.productName} — {qty(i.quantity)} {unitLabel(i.unit)}
                  {i.bucket === 'damaged' && <em> ({t('web.transfers.damaged')})</em>}
                  {i.receivedQuantity !== null && i.receivedQuantity !== i.quantity && <span className="text-red-700"> · {t('web.transfers.arrived', { quantity: qty(i.receivedQuantity) })}</span>}
                </li>
              ))}
            </ul>
            {tr.note && <p className="mt-1 text-slate-600">{tr.note}</p>}
            {tr.closeNote && <p className="mt-1 text-slate-600">{tr.closedByEmail}: {tr.closeNote}</p>}
          </article>
        ))}
      </div>
    </section>
  )
}

function ReceiveForm({ transfer, busy, onSave, onCancel }: { transfer: Transfer; busy: boolean; onSave: (lines: { lineNo: number; receivedQuantity: number }[], note: string | null) => void; onCancel: () => void }) {
  const [arrived, setArrived] = useState<Record<number, string>>(() => Object.fromEntries(transfer.items.map((i) => [i.lineNo, String(i.quantity)])))
  const [note, setNote] = useState('')
  const valid = transfer.items.every((i) => { const v = Number(arrived[i.lineNo]); return arrived[i.lineNo] !== '' && v >= 0 && v <= i.quantity })
  return (
    <form className="space-y-2 rounded border p-3 text-sm" onSubmit={(e) => { e.preventDefault(); onSave(transfer.items.map((i) => ({ lineNo: i.lineNo, receivedQuantity: Number(arrived[i.lineNo]) })), note.trim() || null) }}>
      <h3 className="font-semibold">{t('web.transfers.receiveTitle', { number: transfer.number })}</h3>
      {transfer.items.map((i) => (
        <label key={i.lineNo} className="flex flex-wrap items-center gap-2">
          <span className="min-w-60">{i.productCode} · {i.productName} {i.bucket === 'damaged' && `(${t('web.transfers.damaged')})`} — {t('web.transfers.sentQty', { quantity: qty(i.quantity) })}</span>
          <input type="number" min={0} max={i.quantity} step="any" dir="ltr" aria-label={t('web.transfers.arrivedOf', { name: i.productName })} className="w-28 rounded border px-2 py-1" value={arrived[i.lineNo]} onChange={(e) => setArrived({ ...arrived, [i.lineNo]: e.target.value })} />
        </label>
      ))}
      <input aria-label={t('web.transfers.note')} placeholder={t('web.transfers.note')} maxLength={500} className="w-full rounded border px-2 py-1" value={note} onChange={(e) => setNote(e.target.value)} />
      <div className="flex gap-2">
        <button type="submit" className="rounded border px-3 py-1" disabled={busy || !valid}>{t('web.transfers.confirmReceive')}</button>
        <button type="button" className="rounded border px-3 py-1" onClick={onCancel}>{t('web.transfers.close')}</button>
      </div>
    </form>
  )
}

function NewTransfer({ admin, onDone }: { admin: AdminApi; onDone: () => void }) {
  const warehouses = useLoad(useCallback(() => admin.transferWarehouses(), [admin]))
  const stock = useLoad(useCallback(() => admin.warehouseStock(), [admin]))
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [note, setNote] = useState('')
  const [sellable, setSellable] = useState<Record<string, string>>({})
  const [damaged, setDamaged] = useState<Record<string, string>>({})
  const action = useAction(async () => {})
  // The server lists only company warehouses (a wholesaler keeps no stock); the sending list is what the caller may send from (the stock endpoint already limits a worker to his own warehouses).
  const company = warehouses.data ?? []
  const mine = new Set((stock.data ?? []).map((r) => r.warehouseId))
  const rows: WarehouseStockRow[] = (stock.data ?? []).filter((r) => r.warehouseId === from && (r.quantity > 0 || (r.damaged ?? 0) > 0))
  const items = rows.flatMap((r) => [
    { productId: r.productId, quantity: Number(sellable[r.productId] ?? 0), bucket: 'sellable' as const },
    { productId: r.productId, quantity: Number(damaged[r.productId] ?? 0), bucket: 'damaged' as const },
  ]).filter((i) => i.quantity > 0)
  const tooMuch = rows.some((r) => Number(sellable[r.productId] ?? 0) > r.quantity || Number(damaged[r.productId] ?? 0) > (r.damaged ?? 0))

  function submit(e: FormEvent) {
    e.preventDefault()
    void action.run(async () => {
      await admin.createTransfer({ fromWarehouseId: from, toWarehouseId: to, note: note.trim() || null, items })
      onDone()
    })
  }

  return (
    <form onSubmit={submit} className="space-y-3 rounded border p-3 text-sm">
      {(warehouses.error ?? stock.error ?? action.error) && <p role="alert" className="text-red-700">{warehouses.error ?? stock.error ?? action.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.transfers.from')} className="rounded border px-2 py-1" value={from} onChange={(e) => { setFrom(e.target.value); setSellable({}); setDamaged({}) }}>
          <option value="">{t('web.transfers.from')}</option>
          {company.filter((w) => mine.has(w.id)).map((w) => <option key={w.id} value={w.id}>{w.code} · {w.name}</option>)}
        </select>
        <span aria-hidden>→</span>
        <select aria-label={t('web.transfers.to')} className="rounded border px-2 py-1" value={to} onChange={(e) => setTo(e.target.value)}>
          <option value="">{t('web.transfers.to')}</option>
          {company.filter((w) => w.id !== from).map((w) => <option key={w.id} value={w.id}>{w.code} · {w.name}</option>)}
        </select>
        <input aria-label={t('web.transfers.note')} placeholder={t('web.transfers.note')} maxLength={500} className="min-w-60 flex-1 rounded border px-2 py-1" value={note} onChange={(e) => setNote(e.target.value)} />
      </div>
      {from && rows.length === 0 && <p className="text-slate-500">{t('web.transfers.nothingToSend')}</p>}
      {rows.map((r) => (
        <div key={r.productId} className="flex flex-wrap items-center gap-2">
          <span className="min-w-60">{r.productCode} · {r.productName} ({qty(r.quantity)} {unitLabel(r.unit)}{(r.damaged ?? 0) > 0 ? ` + ${qty(r.damaged ?? 0)} ${t('web.transfers.damaged')}` : ''})</span>
          {r.quantity > 0 && <input type="number" min={0} max={r.quantity} step="any" dir="ltr" aria-label={t('web.transfers.sendQty', { name: r.productName })} placeholder="0" className="w-28 rounded border px-2 py-1" value={sellable[r.productId] ?? ''} onChange={(e) => setSellable({ ...sellable, [r.productId]: e.target.value })} />}
          {(r.damaged ?? 0) > 0 && <input type="number" min={0} max={r.damaged} step="any" dir="ltr" aria-label={t('web.transfers.sendDamagedQty', { name: r.productName })} placeholder="0" className="w-28 rounded border px-2 py-1" value={damaged[r.productId] ?? ''} onChange={(e) => setDamaged({ ...damaged, [r.productId]: e.target.value })} />}
        </div>
      ))}
      <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !from || !to || items.length === 0 || tooMuch}>{t('web.transfers.send')}</button>
    </form>
  )
}
