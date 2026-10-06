import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, errorMessage, Permissions, type Me, type Payment } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })
const filters = ['', 'collected', 'confirmed', 'voided', 'superseded']

// Payments (plan step 2.5): the list, the record form, and for the finance manager confirm / void / correct. A visitor's payment stays
// "collected" until the finance manager confirms that the money arrived. Nothing is deleted: voided and corrected payments stay with their history.
export function PaymentsPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [status, setStatus] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [more, setMore] = useState<Payment[]>([])
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const first = useLoad(useCallback(() => admin.payments(status), [admin, status]))
  const items = [...(first.data ?? []), ...more]

  const refresh = () => {
    setMore([])
    setDone(false)
    first.reload()
  }
  async function loadMore() {
    try {
      const page = await admin.payments(status, items[items.length - 1])
      setMore([...more, ...page])
      if (page.length < 50) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.payments.title')}</h2>
        <select aria-label={t('web.payments.filter')} className="rounded border px-2 py-1 text-sm" value={status} onChange={(e) => { setStatus(e.target.value); setMore([]); setDone(false); setOpen(null) }}>
          {filters.map((f) => (
            <option key={f} value={f}>
              {f === '' ? t('web.payments.all') : t(`web.payments.status.${f}` as TextKey)}
            </option>
          ))}
        </select>
      </div>
      {(first.error || error) && <p role="alert" className="text-sm text-red-700">{first.error ?? error}</p>}
      {saved && <p role="status" className="text-sm text-green-800">{saved}</p>}
      {can(me, ...Permissions.recordPayments) && <RecordForm admin={admin} onSaved={(message) => { setSaved(message); refresh() }} />}
      {first.data?.length === 0 && <p className="text-slate-600">{t('web.payments.none')}</p>}
      {items.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.payments.number')}</th>
              <th>{t('web.payments.customer')}</th>
              <th>{t('web.payments.method')}</th>
              <th>{t('web.payments.amount')}</th>
              <th>{t('web.payments.status')}</th>
              <th>{t('web.payments.date')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((p) => (
              <tr key={p.id} className="border-b">
                <td className="py-2">{p.number}</td>
                <td>
                  {p.customerCode} · {p.customerName}
                </td>
                <td>{t(`web.payments.method.${p.method}` as TextKey)}</td>
                <td>
                  {money(p.amount)} {p.currency}
                </td>
                <td>{t(`web.payments.status.${p.status}` as TextKey)}</td>
                <td>{p.createdAt.slice(0, 10)}</td>
                <td className="text-end">
                  <button className="rounded border px-2 py-1" aria-label={t('web.payments.details', { number: p.number })} onClick={() => setOpen(open === p.id ? null : p.id)}>
                    {t('web.payments.view')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {items.length >= 50 && !done && (
        <button className="rounded border px-3 py-1 text-sm" onClick={() => void loadMore()}>
          …
        </button>
      )}
      {open && <PaymentDetails key={open} admin={admin} me={me} paymentId={open} onChanged={refresh} />}
    </section>
  )
}

function PaymentDetails({ admin, me, paymentId, onChanged }: { admin: AdminApi; me: Me; paymentId: string; onChanged: () => void }) {
  const payment = useLoad(useCallback(() => admin.payment(paymentId), [admin, paymentId]))
  const p = payment.data
  const balance = useLoad(useCallback(() => (p ? admin.customerBalance(p.customerId).catch(() => null) : Promise.resolve(null)), [admin, p]))
  const action = useAction(() => {
    payment.reload()
    onChanged()
  })
  const manage = can(me, ...Permissions.managePayments)
  const open = p?.status === 'collected' || p?.status === 'confirmed'

  function close(kind: 'void' | 'correct') {
    if (!p) return
    const reason = window.prompt(t('web.payments.reasonPrompt'))?.trim()
    if (!reason) return
    if (kind === 'void') return void action.run(() => admin.voidPayment(p.id, reason))
    const amount = Number(window.prompt(t('web.payments.amountPrompt'), String(p.amount)))
    if (!(amount > 0)) return
    void action.run(() => admin.correctPayment(p.id, { orderId: p.orderId, method: p.method, currency: p.currency, amount, reference: p.reference, note: p.note, reason }))
  }

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      {(payment.error || action.error) && <p role="alert" className="text-red-700">{payment.error ?? action.error}</p>}
      {p && (
        <>
          <h3 className="font-semibold">
            {p.number} · {t(`web.payments.status.${p.status}` as TextKey)}
          </h3>
          <p>
            {p.customerCode} · {p.customerName} — {money(p.amount)} {p.currency} ({t(`web.payments.method.${p.method}` as TextKey)})
          </p>
          {p.orderNumber && <p>{t('web.payments.order')}: {p.orderNumber}</p>}
          {p.reference && <p>{t('web.payments.reference')}: {p.reference}</p>}
          {p.note && <p>{t('web.payments.note')}: {p.note}</p>}
          {p.receivedByEmail && <p>{t('web.payments.receivedBy')}: {p.receivedByEmail}</p>}
          {p.closedReason && <p>{p.closedReason}</p>}
          {manage && (
            <div className="flex flex-wrap gap-2">
              {p.status === 'collected' && (
                <button className="rounded border px-3 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.confirmPayment(p.id))}>
                  {t('web.payments.confirm')}
                </button>
              )}
              {open && (
                <>
                  <button className="rounded border px-3 py-1" disabled={action.busy} onClick={() => close('correct')}>
                    {t('web.payments.correct')}
                  </button>
                  <button className="rounded border px-3 py-1" disabled={action.busy} onClick={() => close('void')}>
                    {t('web.payments.void')}
                  </button>
                </>
              )}
            </div>
          )}
          <h4 className="font-medium">{t('web.payments.history')}</h4>
          <ul>
            {(p.history ?? []).map((h, i) => (
              <li key={i}>
                {h.at.slice(0, 16).replace('T', ' ')} · {t(`web.payments.status.${h.status}` as TextKey)}
                {h.actorEmail ? ` · ${h.actorEmail}` : ''}
                {h.note ? ` · ${h.note}` : ''}
              </li>
            ))}
          </ul>
          {balance.data && balance.data.items.length > 0 && (
            <div>
              <h4 className="font-medium">{t('web.payments.balance')}</h4>
              <ul>
                {balance.data.items.map((b) => (
                  <li key={b.currency}>
                    {b.currency}: {t('web.payments.delivered')} {money(b.delivered)} · {t('web.payments.confirmedTotal')} {money(b.confirmed)} · {t('web.payments.collectedTotal')} {money(b.collected)} ·{' '}
                    <strong>{t('web.payments.owes')} {money(b.balance)}</strong>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function RecordForm({ admin, onSaved }: { admin: AdminApi; onSaved: (message: string) => void }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const orders = useLoad(useCallback(() => admin.orders(), [admin]))
  const [customer, setCustomer] = useState('')
  const [order, setOrder] = useState('')
  const [method, setMethod] = useState('cash')
  const [currency, setCurrency] = useState('IQD')
  const [amount, setAmount] = useState('')
  const [reference, setReference] = useState('')
  const [note, setNote] = useState('')
  // One reference per form: a retry of a lost answer returns the same payment instead of a second one.
  const [clientRef, setClientRef] = useState(() => crypto.randomUUID())
  const action = useAction()
  const customerOrders = (orders.data ?? []).filter((o) => o.customerId === customer && o.status !== 'cancelled' && o.currency === currency)
  const needsReference = method === 'fastpay' || method === 'fib'
  const ready = customer !== '' && Number(amount) > 0 && (!needsReference || reference.trim() !== '')

  async function save(e: FormEvent) {
    e.preventDefault()
    const result = await action.run(() =>
      admin.recordPayment({ customerId: customer, orderId: order || null, method, currency, amount: Number(amount), reference: reference.trim() || null, note: note.trim() || null, clientRef }),
    )
    if (!result) return
    onSaved(t('web.payments.saved', { number: result.number }))
    setCustomer('')
    setOrder('')
    setAmount('')
    setReference('')
    setNote('')
    setClientRef(crypto.randomUUID())
  }

  return (
    <form onSubmit={(e) => void save(e)} className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.payments.record')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.payments.customer')} className="rounded border px-2 py-1" value={customer} onChange={(e) => { setCustomer(e.target.value); setOrder('') }}>
          <option value="">{t('web.payments.chooseCustomer')}</option>
          {(customers.data ?? []).map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {c.name}
            </option>
          ))}
        </select>
        <select aria-label={t('web.payments.order')} className="rounded border px-2 py-1" value={order} onChange={(e) => setOrder(e.target.value)}>
          <option value="">{t('web.payments.noOrder')}</option>
          {customerOrders.map((o) => (
            <option key={o.id} value={o.id}>
              {o.number} ({money(o.total)} {o.currency})
            </option>
          ))}
        </select>
        <select aria-label={t('web.payments.method')} className="rounded border px-2 py-1" value={method} onChange={(e) => setMethod(e.target.value)}>
          {['cash', 'fastpay', 'fib', 'other'].map((m) => (
            <option key={m} value={m}>
              {t(`web.payments.method.${m}` as TextKey)}
            </option>
          ))}
        </select>
        <select aria-label={t('web.payments.currency')} className="rounded border px-2 py-1" value={currency} onChange={(e) => { setCurrency(e.target.value); setOrder('') }}>
          <option value="IQD">IQD</option>
          <option value="USD">USD</option>
        </select>
        <input aria-label={t('web.payments.amount')} className="w-32 rounded border px-2 py-1" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <input aria-label={t('web.payments.reference')} placeholder={t('web.payments.reference')} className="w-64 rounded border px-2 py-1" maxLength={100} value={reference} onChange={(e) => setReference(e.target.value)} />
        <input aria-label={t('web.payments.note')} placeholder={t('web.payments.note')} className="w-64 rounded border px-2 py-1" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        <button type="submit" className="rounded border px-3 py-1" disabled={!ready || action.busy}>
          {t('web.payments.save')}
        </button>
      </div>
    </form>
  )
}
