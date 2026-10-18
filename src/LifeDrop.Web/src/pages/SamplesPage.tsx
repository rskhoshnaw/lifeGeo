import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, errorMessage, iraqToday, Permissions, type Me, type Sample } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

// Pharma samples (plan step 2.6): what reps gave to customers. Reps record them in the app; here managers read the list and void a record with a reason.
export function SamplesPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const first = useLoad(useCallback(() => admin.samples(), [admin]))
  const [more, setMore] = useState<Sample[]>([])
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const reload = () => {
    setMore([])
    setDone(false)
    first.reload()
  }
  const action = useAction(reload)
  const items = [...(first.data ?? []), ...more]
  const manage = can(me, ...Permissions.manageSamples)

  async function loadMore() {
    try {
      const page = await admin.samples(items[items.length - 1])
      setMore([...more, ...page])
      if (page.length < 50) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  function voidSample(sample: Sample) {
    const reason = window.prompt(t('web.samples.reasonPrompt'))?.trim()
    if (reason) void action.run(() => admin.voidSample(sample.id, reason))
  }

  return (
    <section className="space-y-6 p-6">
      <h2 className="text-xl font-semibold">{t('web.samples.title')}</h2>
      {(first.error || error || action.error) && <p role="alert" className="text-sm text-red-700">{first.error ?? error ?? action.error}</p>}
      {can(me, ...Permissions.recordSamples) && <NewSample admin={admin} onRecorded={reload} />}
      {first.data?.length === 0 && <p className="text-slate-600">{t('web.samples.none')}</p>}
      {items.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.samples.number')}</th>
              <th>{t('web.samples.customer')}</th>
              <th>{t('web.samples.product')}</th>
              <th>{t('web.samples.quantity')}</th>
              <th>{t('web.samples.recipient')}</th>
              <th>{t('web.samples.batch')}</th>
              <th>{t('web.samples.expiry')}</th>
              <th>{t('web.samples.by')}</th>
              <th>{t('web.samples.date')}</th>
              <th>{t('web.samples.status')}</th>
              {manage && <th />}
            </tr>
          </thead>
          <tbody>
            {items.map((s) => (
              <tr key={s.id} className="border-b">
                <td className="py-2">{s.number}</td>
                <td>
                  {s.customerCode} · {s.customerName}
                </td>
                <td>
                  {s.productCode} · {s.productName}
                </td>
                <td>
                  {s.quantity} {s.unit}
                </td>
                <td>{s.recipientName ?? ''}</td>
                <td>{s.batchNumber ?? ''}</td>
                <td>{s.expiryDate ?? ''}</td>
                <td>{s.distributedByEmail ?? ''}</td>
                <td>{s.createdAt.slice(0, 10)}</td>
                <td>
                  {t(`web.samples.status.${s.status}` as TextKey)}
                  {s.voidReason ? ` · ${s.voidReason}` : ''}
                </td>
                {manage && (
                  <td className="text-end">
                    {s.status === 'given' && (
                      <button className="rounded border px-2 py-1" aria-label={t('web.samples.voidAction', { number: s.number })} disabled={action.busy} onClick={() => voidSample(s)}>
                        {t('web.samples.void')}
                      </button>
                    )}
                  </td>
                )}
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
    </section>
  )
}

// A sample recorded from the panel (plan step 4.13), for example by a manager who gives one during a visit with a rep. The same route as the phone app's; without a position.
function NewSample({ admin, onRecorded }: { admin: AdminApi; onRecorded: () => void }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const products = useLoad(useCallback(() => admin.products(false), [admin]))
  const empty = { customerId: '', productId: '', quantity: '', recipientName: '', batchNumber: '', expiryDate: '', note: '' }
  const [form, setForm] = useState(empty)
  // One reference per form: a retry of a lost answer does not record a second sample.
  const [clientRef, setClientRef] = useState(() => crypto.randomUUID())
  const [recorded, setRecorded] = useState(false)
  const action = useAction()
  const quantity = Number(form.quantity)
  const ready = form.customerId !== '' && form.productId !== '' && quantity > 0 && (form.expiryDate === '' || form.expiryDate >= iraqToday())

  async function save(e: FormEvent) {
    e.preventDefault()
    setRecorded(false)
    const done = await action.run(async () => {
      await admin.recordSample({
        customerId: form.customerId,
        productId: form.productId,
        quantity,
        recipientName: form.recipientName.trim() || null,
        batchNumber: form.batchNumber.trim() || null,
        expiryDate: form.expiryDate || null,
        note: form.note.trim() || null,
        clientRef,
      })
      return true
    })
    if (done) {
      setForm(empty)
      setClientRef(crypto.randomUUID())
      setRecorded(true)
      onRecorded()
    }
  }

  return (
    <form onSubmit={(e) => void save(e)} className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.samples.new')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {recorded && <p role="status" className="text-green-700">{t('web.samples.recorded')}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.samples.customer')} className="rounded border px-2 py-1" value={form.customerId} onChange={(e) => setForm({ ...form, customerId: e.target.value })}>
          <option value="">{t('web.samples.chooseCustomer')}</option>
          {(customers.data ?? []).map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {c.name}
            </option>
          ))}
        </select>
        <select aria-label={t('web.samples.product')} className="rounded border px-2 py-1" value={form.productId} onChange={(e) => setForm({ ...form, productId: e.target.value })}>
          <option value="">{t('web.samples.chooseProduct')}</option>
          {(products.data ?? []).map((p) => (
            <option key={p.id} value={p.id}>
              {p.code} · {p.name}
            </option>
          ))}
        </select>
        <input type="number" min="0" step="0.001" aria-label={t('web.samples.quantity')} placeholder={t('web.samples.quantity')} className="w-28 rounded border px-2 py-1" value={form.quantity} onChange={(e) => setForm({ ...form, quantity: e.target.value })} />
        <input aria-label={t('web.samples.recipient')} placeholder={t('web.samples.recipient')} className="rounded border px-2 py-1" maxLength={100} value={form.recipientName} onChange={(e) => setForm({ ...form, recipientName: e.target.value })} />
        <input aria-label={t('web.samples.batch')} placeholder={t('web.samples.batch')} className="w-32 rounded border px-2 py-1" maxLength={50} value={form.batchNumber} onChange={(e) => setForm({ ...form, batchNumber: e.target.value })} />
        <input type="date" aria-label={t('web.samples.expiry')} className="rounded border px-2 py-1" min={iraqToday()} value={form.expiryDate} onChange={(e) => setForm({ ...form, expiryDate: e.target.value })} />
        <input aria-label={t('web.samples.note')} placeholder={t('web.samples.note')} className="min-w-48 flex-1 rounded border px-2 py-1" maxLength={500} value={form.note} onChange={(e) => setForm({ ...form, note: e.target.value })} />
        <button type="submit" className="rounded border px-3 py-1" disabled={!ready || action.busy}>
          {t('web.samples.record')}
        </button>
      </div>
    </form>
  )
}
