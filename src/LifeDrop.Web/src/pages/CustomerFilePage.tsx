import { useCallback, useState, type ReactNode } from 'react'
import { AdminApi, can, Permissions, type Customer, type Me } from '../api/admin.ts'
import { Badge, type Tone } from '../components/Badge.tsx'
import { useLoad } from '../components/useLoad.ts'
import { CustomerDetailsEditor } from '../components/CustomerDetailsEditor.tsx'
import { CustomerProfileEditor } from '../components/CustomerProfileEditor.tsx'
import { VisitWindowsEditor } from '../components/VisitWindowsEditor.tsx'
import { NewFollowUp } from './FollowUpsPage.tsx'
import { NewOrderForm } from './OrdersPage.tsx'
import { RecordForm } from './PaymentsPage.tsx'
import { NewSample } from './SamplesPage.tsx'
import { NewReturnForm, ReturnLinesTable, ReturnPhotos } from './ReturnsPage.tsx'
import { t, type TextKey } from '../i18n.ts'
import { unitLabel } from './productView.ts'

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'short' })
const clock = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' })
const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })

// The address of a customer's file; it opens in its own tab (owner request 2026-11-26) and works after a reload because it lives in the address bar.
export const customerFileHash = (id: string) => `#/customer/${id}`
export const customerIdFromHash = (hash: string): string | null => /^#\/customer\/([0-9a-fA-F-]{36})$/.exec(hash)?.[1] ?? null

// A section whose data may be refused for this reader (403/404 of that list only): it says so instead of breaking the page.
function Section({ title, error, children }: { title: string; error: string | null; children: ReactNode }) {
  return (
    <section className="space-y-2 rounded-xl border bg-white p-4 shadow-sm">
      <h3 className="text-base font-semibold">{title}</h3>
      {error ? <p className="text-sm text-slate-500">{t('web.c360.noAccess')}</p> : children}
    </section>
  )
}

const none = () => <p className="text-sm text-slate-500">{t('web.history.none')}</p>

// One customer's whole file in one page: the details, the visits with their results, orders (and what was not delivered), payments with the balance, samples and follow-ups.
export function CustomerFilePage({ admin, customerId, me }: { admin: AdminApi; customerId: string; me: Me }) {
  const customer = useLoad(useCallback(() => admin.customer(customerId), [admin, customerId]))
  const history = useLoad(useCallback(() => admin.customerHistory(customerId), [admin, customerId]))
  const payments = useLoad(useCallback(() => admin.customerPayments(customerId), [admin, customerId]))
  const balance = useLoad(useCallback(() => admin.customerBalance(customerId), [admin, customerId]))
  const samples = useLoad(useCallback(() => admin.customerSamples(customerId), [admin, customerId]))
  const followUps = useLoad(useCallback(() => admin.customerFollowUps(customerId), [admin, customerId]))
  const returns = useLoad(useCallback(() => admin.returns('', customerId), [admin, customerId]))
  const [panel, setPanel] = useState<'order' | 'payment' | 'sample' | 'return' | 'followup' | 'edit' | 'profile' | 'hours' | null>(null)
  const [done, setDone] = useState<string | null>(null)
  const manageFollowUps = can(me, ...Permissions.manageFollowUps)
  const users = useLoad(useCallback(() => (manageFollowUps ? admin.users().catch(() => []) : Promise.resolve([])), [admin, manageFollowUps]))
  const editable = can(me, ...Permissions.editCustomers)
  const actions: { id: NonNullable<typeof panel>; label: string; allowed: boolean }[] = [
    { id: 'order', label: t('web.c360.newOrder'), allowed: can(me, 'orders.create') },
    { id: 'payment', label: t('web.c360.newPayment'), allowed: can(me, 'payments.record') },
    { id: 'sample', label: t('web.c360.newSample'), allowed: can(me, 'samples.record') },
    { id: 'return', label: t('web.c360.newReturn'), allowed: can(me, ...Permissions.createReturns) },
    { id: 'followup', label: t('web.c360.newFollowUp'), allowed: can(me, ...Permissions.createFollowUps) },
    { id: 'edit', label: t('web.customers.edit'), allowed: editable },
    { id: 'profile', label: t('web.customers.profile'), allowed: editable },
    { id: 'hours', label: t('web.customers.visitHours'), allowed: editable },
  ]
  // What a form changed shows in its list at once.
  const refresh = (message?: string) => {
    setDone(message ?? t('web.c360.saved'))
    setPanel(null)
    customer.reload()
    history.reload()
    payments.reload()
    balance.reload()
    samples.reload()
    followUps.reload()
    returns.reload()
  }

  if (customer.error) return <p role="alert" className="p-6 text-sm text-red-700">{t('web.c360.notFound')}</p>
  const c = customer.data
  if (!c) return <p className="p-6 text-sm text-slate-500">…</p>
  return (
    <div className="space-y-5 p-6">
      <h2 className="text-xl font-semibold">{t('web.c360.title', { customer: `${c.name} (${c.code})` })}</h2>
      <div className="flex flex-wrap items-center gap-2" role="toolbar" aria-label={t('web.c360.actions')}>
        {actions.filter((a) => a.allowed).map((a) => (
          <button key={a.id} type="button" aria-pressed={panel === a.id} className={`rounded border px-3 py-1 text-sm ${panel === a.id ? 'bg-slate-800 text-white' : 'bg-white'}`} onClick={() => { setDone(null); setPanel(panel === a.id ? null : a.id) }}>
            {a.label}
          </button>
        ))}
      </div>
      {done && <p role="status" className="text-sm text-green-700">{done}</p>}
      {panel === 'order' && <NewOrderForm admin={admin} fixedCustomer={c} onPlaced={(message) => refresh(message)} />}
      {panel === 'payment' && <RecordForm admin={admin} fixedCustomer={c} canRefund={can(me, ...Permissions.managePayments)} onSaved={(message) => refresh(message)} />}
      {panel === 'return' && <NewReturnForm admin={admin} fixedCustomer={c} onSent={(message) => refresh(message)} />}
      {panel === 'sample' && <NewSample admin={admin} fixedCustomer={c} onRecorded={() => refresh()} />}
      {panel === 'followup' && <NewFollowUp admin={admin} fixedCustomer={c} manage={manageFollowUps} users={users.data ?? []} onCreated={() => refresh()} />}
      {panel === 'edit' && <CustomerDetailsEditor admin={admin} customer={c} onClose={() => setPanel(null)} onSaved={() => refresh()} />}
      {panel === 'profile' && <CustomerProfileEditor admin={admin} customer={c} canEdit onClose={() => { setPanel(null); customer.reload() }} />}
      {panel === 'hours' && <VisitWindowsEditor admin={admin} customer={c} canEdit onClose={() => setPanel(null)} />}
      <Info customer={c} />

      <Section title={t('web.history.visits')} error={history.error}>
        {history.data && history.data.visits.length === 0 && none()}
        {history.data && history.data.visits.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-start text-sm">
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
                {history.data.visits.map((v) => (
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
          </div>
        )}
      </Section>

      <Section title={t('web.history.orders')} error={history.error}>
        {history.data?.orders === null && <p className="text-sm text-slate-500">{t('web.history.ordersHidden')}</p>}
        {history.data?.orders?.length === 0 && none()}
        {history.data?.orders && history.data.orders.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-start text-sm">
              <thead>
                <tr className="border-b">
                  <th className="py-1">{t('web.history.number')}</th>
                  <th>{t('web.history.date')}</th>
                  <th>{t('web.history.status')}</th>
                  <th>{t('web.history.products')}</th>
                  <th>{t('web.c360.notDelivered')}</th>
                  <th>{t('web.history.total')}</th>
                </tr>
              </thead>
              <tbody>
                {history.data.orders.map((o) => {
                  const short = o.lines.filter((l) => l.deliveredQuantity !== null && l.deliveredQuantity < l.quantity)
                  return (
                    <tr key={o.id} className="border-b align-top">
                      <td className="py-1">{o.number}</td>
                      <td>{when.format(new Date(o.createdAt))}</td>
                      <td>{t(`web.orders.status.${o.status}` as TextKey)}</td>
                      <td>{o.lines.map((l) => `${l.productName} × ${l.quantity}`).join('، ')}</td>
                      <td className={short.length ? 'text-red-700' : ''}>{short.length ? short.map((l) => `${l.productName} × ${money(l.quantity - (l.deliveredQuantity ?? 0))}`).join('، ') : '—'}</td>
                      <td>{o.pricesHidden ? '—' : `${money(o.total)} ${o.currency}`}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </Section>

      <Section title={t('web.payments.title')} error={payments.error}>
        {balance.data && balance.data.items.length > 0 && (
          <ul className="flex flex-wrap gap-4 text-sm">
            {balance.data.items.map((b) => (
              <li key={b.currency} className="rounded border bg-slate-50 px-3 py-2">
                <div className="font-semibold">{b.currency}: {t('web.payments.balance')} {money(b.balance)}</div>
                <div className="text-slate-600">{t('web.payments.delivered')} {money(b.delivered)} · {(b.returned ?? 0) > 0 ? `${t('web.c360.returned')} ${money(b.returned ?? 0)} · ` : ''}{(b.refunded ?? 0) > 0 ? `${t('web.payments.refundMark')} ${money(b.refunded ?? 0)} · ` : ''}{t('web.payments.confirmedTotal')} {money(b.confirmed)} · {t('web.payments.collectedTotal')} {money(b.collected)}</div>
              </li>
            ))}
          </ul>
        )}
        {payments.data && payments.data.length === 0 && none()}
        {payments.data && payments.data.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-start text-sm">
              <thead>
                <tr className="border-b">
                  <th className="py-1">{t('web.payments.number')}</th>
                  <th>{t('web.payments.date')}</th>
                  <th>{t('web.payments.method')}</th>
                  <th>{t('web.payments.amount')}</th>
                  <th>{t('web.payments.status')}</th>
                  <th>{t('web.payments.receivedBy')}</th>
                </tr>
              </thead>
              <tbody>
                {payments.data.map((p) => (
                  <tr key={p.id} className="border-b">
                    <td className="py-1">{p.number}</td>
                    <td>{when.format(new Date(p.createdAt))}</td>
                    <td>{t(`web.payments.method.${p.method}` as TextKey)}</td>
                    <td>{money(p.amount)} {p.currency}</td>
                    <td>{t(`web.payments.status.${p.status}` as TextKey)}</td>
                    <td>{p.receivedByEmail ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>

      <Section title={t('web.c360.returns')} error={returns.error}>
        {returns.data && returns.data.length === 0 && none()}
        {returns.data?.map((r) => (
          <div key={r.id} className="space-y-1 rounded border p-2 text-sm">
            <div className="flex flex-wrap items-center justify-center gap-3">
              <strong>{r.number}</strong>
              <span>{when.format(new Date(r.createdAt))}</span>
              <span>{t(`web.returns.status.${r.status}` as TextKey)}</span>
              <span>{r.pricesHidden ? '—' : `${money(r.total)} ${r.currency}`}</span>
            </div>
            <ReturnLinesTable r={r} />
            <ReturnPhotos admin={admin} r={r} />
          </div>
        ))}
      </Section>

      <Section title={t('web.samples.title')} error={samples.error}>
        {samples.data && samples.data.length === 0 && none()}
        {samples.data && samples.data.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-start text-sm">
              <thead>
                <tr className="border-b">
                  <th className="py-1">{t('web.samples.date')}</th>
                  <th>{t('web.samples.product')}</th>
                  <th>{t('web.samples.quantity')}</th>
                  <th>{t('web.samples.recipient')}</th>
                  <th>{t('web.samples.by')}</th>
                  <th>{t('web.samples.status')}</th>
                </tr>
              </thead>
              <tbody>
                {samples.data.map((s) => (
                  <tr key={s.id} className="border-b">
                    <td className="py-1">{when.format(new Date(s.createdAt))}</td>
                    <td>{s.productName}</td>
                    <td>{money(s.quantity)} {unitLabel(s.unit)}</td>
                    <td>{s.recipientName ?? '—'}</td>
                    <td>{s.distributedByEmail ?? '—'}</td>
                    <td>{t(`web.samples.status.${s.status}` as TextKey)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>

      <Section title={t('web.followups.title')} error={followUps.error}>
        {followUps.data && followUps.data.length === 0 && none()}
        {followUps.data && followUps.data.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-start text-sm">
              <thead>
                <tr className="border-b">
                  <th className="py-1">{t('web.followups.subject')}</th>
                  <th>{t('web.followups.due')}</th>
                  <th>{t('web.followups.assignee')}</th>
                  <th>{t('web.followups.status')}</th>
                </tr>
              </thead>
              <tbody>
                {followUps.data.map((f) => (
                  <tr key={f.id} className="border-b">
                    <td className="py-1">{f.subject}{f.resultNote ? ` — ${f.resultNote}` : ''}</td>
                    <td>{f.dueDate}{f.isOverdue ? ` · ${t('web.followups.overdue')}` : ''}</td>
                    <td>{f.assigneeEmail ?? '—'}</td>
                    <td>{t(`web.followups.status.${f.status === 'in_progress' ? 'pending' : f.status}` as TextKey)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Section>
    </div>
  )
}

// The details entered for the customer, in two columns.
function Info({ customer: c }: { customer: Customer }) {
  const place = (c.placePath ?? []).map((p) => p.name).join(' › ')
  const yesNo = (v: boolean | null | undefined) => (v === null || v === undefined ? null : t(v ? 'web.customers.yes' : 'web.customers.no'))
  const rows: [string, ReactNode][] = [
    [t('web.customers.code'), c.code],
    [t('web.customers.type'), c.type],
    [t('web.customers.approval'), <Badge key="a" tone={(({ approved: 'good', pending: 'warn', rejected: 'bad' }) as Record<string, Tone>)[c.approvalStatus] ?? 'neutral'}>{['approved', 'pending', 'rejected'].includes(c.approvalStatus) ? t(`web.approval.${c.approvalStatus}` as TextKey) : c.approvalStatus}</Badge>],
    [t('web.customers.phone'), c.phone],
    [t('web.customers.contactName'), c.contactName],
    [t('web.customers.address'), c.address],
    [t('web.customers.place'), place || null],
    [t('web.customers.clinic'), c.clinicName],
    [t('web.customers.specialty'), c.specialty ? t(`specialty.${c.specialty}` as TextKey) : null],
    [t('web.customers.class'), c.customerClass],
    [t('web.customers.dealer'), yesNo(c.dealer)],
    [t('web.customers.cooperates'), yesNo(c.cooperates)],
    [t('web.customers.owner'), c.ownerEmail],
    [t('web.customers.addedBy'), c.createdByEmail],
    [t('web.customers.addedOn'), when.format(new Date(c.createdAt))],
    [t('web.customers.priority'), t(`web.priority.${c.priority}` as TextKey)],
    [t('web.customers.notes'), c.notes],
  ]
  return (
    <section className="rounded-xl border bg-white p-4 shadow-sm">
      <h3 className="mb-2 text-base font-semibold">{t('web.c360.info')}</h3>
      <dl className="grid gap-x-8 gap-y-1 text-sm sm:grid-cols-2">
        {rows.filter(([, v]) => v !== null && v !== undefined && v !== '').map(([label, value]) => (
          <div key={label} className="flex gap-3 border-b py-1">
            <dt className="w-1/3 text-slate-500">{label}</dt>
            <dd className="w-2/3 whitespace-pre-wrap" dir="auto">{value}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}

export const canOpenCustomerFile = (me: Me | null) => can(me, ...Permissions.customers)
