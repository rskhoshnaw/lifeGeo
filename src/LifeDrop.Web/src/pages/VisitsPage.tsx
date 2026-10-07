import { useCallback, useState, type FormEvent } from 'react'
import {
  AdminApi,
  can,
  iraqToday,
  Permissions,
  type Customer,
  type Me,
  type PlannedVisit,
  type UserSummary,
  type VisitPriority,
  type VisitType,
} from '../api/admin.ts'
import { Badge, type Tone } from '../components/Badge.tsx'
import { t, type TextKey } from '../i18n.ts'

const visitStatuses = ['Planned', 'EnRoute', 'Arrived', 'InVisit', 'Completed', 'Failed', 'Cancelled']
const statusText = (status: string) => (visitStatuses.includes(status) ? t(`web.visitStatus.${status}` as TextKey) : status)
import { PlanFromPlaces } from '../components/PlanFromPlaces.tsx'
import { useAction, useLoad } from '../components/useLoad.ts'

const visitTypes: VisitType[] = ['pharmacy', 'doctor', 'wholesaler', 'other']
const priorities: VisitPriority[] = ['high', 'normal', 'low']
const finished = ['Completed', 'Failed', 'Cancelled']

// Visit planning (plan step 1.18): one day's plan within the user's area, with create, reassign and cancel.
// The server checks scope and state on every action; the buttons only follow /me and the visit's status.
export function VisitsPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [date, setDate] = useState(iraqToday)
  const plan = useLoad(useCallback(() => admin.visitPlan(date), [admin, date]))
  const users = useLoad(useCallback(() => admin.users(), [admin]))
  const canCancel = can(me, ...Permissions.cancelVisits)
  const action = useAction(plan.reload)
  const [picked, setPicked] = useState<Record<string, string>>({})
  const [moveDay, setMoveDay] = useState<Record<string, string>>({})

  function cancel(visit: PlannedVisit) {
    const reason = window.prompt(t('web.visits.cancelPrompt', { customer: visit.customerName ?? visit.customerId }))?.trim()
    if (!reason) return
    void action.run(() => admin.cancelVisit(visit.id, reason))
  }

  // Swaps a visit with its neighbour inside the same assignee's list and saves the whole order of that assignee.
  function shift(visit: PlannedVisit, delta: -1 | 1) {
    const ids = (plan.data ?? []).filter((v) => v.userId === visit.userId).map((v) => v.id)
    const from = ids.indexOf(visit.id)
    const to = from + delta
    if (from < 0 || to < 0 || to >= ids.length) return
    ;[ids[from], ids[to]] = [ids[to]!, ids[from]!]
    void action.run(() => admin.reorderPlan(date, ids, visit.userId))
  }

  function postpone(visit: PlannedVisit) {
    const day = moveDay[visit.id]
    if (!day) return
    const reason = window.prompt(t('web.visits.postponePrompt', { customer: visit.customerName ?? visit.customerId }))
    if (reason === null) return
    void action.run(() => admin.postponeVisit(visit.id, day, reason.trim()))
  }

  const [formFor, setFormFor] = useState<PlannedVisit | null>(null)
  const error = action.error ?? plan.error ?? users.error
  return (
    <section className="space-y-8 p-6">
      <div>
        <div className="mb-4 flex items-center gap-4">
          <h2 className="text-xl font-semibold">{t('web.visits.title')}</h2>
          <input type="date" aria-label={t('web.visits.planDate')} className="rounded border px-2 py-1 text-sm" value={date} onChange={(e) => e.target.value && setDate(e.target.value)} />
        </div>
        {error && <p role="alert" className="mb-3 text-sm text-red-700">{error}</p>}
        {plan.data?.length === 0 && <p className="text-slate-600">{t('web.visits.none')}</p>}
        {plan.data && plan.data.length > 0 && (
          <table className="w-full text-start text-sm">
            <thead>
              <tr className="border-b">
                <th className="py-2">{t('web.visits.order')}</th>
                <th>{t('web.visits.time')}</th>
                <th>{t('web.visits.customer')}</th>
                <th>{t('web.visits.assignee')}</th>
                <th>{t('web.visits.type')}</th>
                <th>{t('web.visits.priority')}</th>
                <th>{t('web.visits.status')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {plan.data.map((visit) => {
                const label = visit.customerCode ?? visit.customerId
                const others = (users.data ?? []).filter((u) => u.id !== visit.userId)
                const mine = (plan.data ?? []).filter((v) => v.userId === visit.userId)
                const position = mine.findIndex((v) => v.id === visit.id)
                const movable = visit.status === 'Planned' || visit.status === 'EnRoute'
                return (
                  <tr key={visit.id} className="border-b align-top">
                    <td className="space-x-1 py-2 whitespace-nowrap">
                      <button className="rounded border px-1" aria-label={t('web.visits.moveUp', { customer: label })} disabled={action.busy || position <= 0} onClick={() => shift(visit, -1)}>
                        ↑
                      </button>
                      <button className="rounded border px-1" aria-label={t('web.visits.moveDown', { customer: label })} disabled={action.busy || position >= mine.length - 1} onClick={() => shift(visit, 1)}>
                        ↓
                      </button>
                    </td>
                    <td className="py-2">{visit.plannedTime?.slice(0, 5) ?? '—'}</td>
                    <td>
                      {label} · {visit.customerName ?? ''}
                      {visit.customerPriority !== 'normal' && <div className="text-xs text-blue-700">★ {t('web.visits.customerPriority', { priority: t(`web.priority.${visit.customerPriority}`) })}</div>}
                      {visit.windowStatus === 'outside' && <div className="text-xs text-amber-700">⚠ {t('web.visits.outsideHours')}</div>}
                      {visit.postponeCount > 0 && (
                        <div className="text-xs text-slate-600">
                          {t('web.visits.movedTimes', { count: visit.postponeCount })}
                          {visit.postponeReason ? ` · ${visit.postponeReason}` : ''}
                        </div>
                      )}
                    </td>
                    <td>{visit.assigneeEmail ?? visit.userId}</td>
                    <td>{t(`web.visitType.${visit.visitType}`)}</td>
                    <td>{t(`web.priority.${visit.priority}`)}</td>
                    <td><Badge tone={({ Completed: 'good', Failed: 'bad', Cancelled: 'neutral', Planned: 'info' } as Record<string, Tone>)[visit.status] ?? 'warn'}>{statusText(visit.status)}</Badge></td>
                    <td className="space-x-2 py-2 text-end">
                      {(visit.status === 'InVisit' || visit.status === 'Completed') && (
                        <button className="rounded border px-2 py-1" aria-label={t('web.visits.formOf', { customer: label })} onClick={() => setFormFor(formFor?.id === visit.id ? null : visit)}>
                          {t('web.visits.form')}
                        </button>
                      )}
                      {movable && (
                        <>
                          <input type="date" aria-label={t('web.visits.moveToDay', { customer: label })} className="rounded border px-1 py-1" min={iraqToday()} value={moveDay[visit.id] ?? ''} onChange={(e) => setMoveDay({ ...moveDay, [visit.id]: e.target.value })} />
                          <button className="rounded border px-2 py-1" disabled={action.busy || !moveDay[visit.id]} onClick={() => postpone(visit)}>
                            {t('web.visits.move')}
                          </button>
                        </>
                      )}
                      {visit.status === 'Planned' && others.length > 0 && (
                        <>
                          <select aria-label={t('web.visits.newAssigneeFor', { customer: label })} className="rounded border px-1 py-1" value={picked[visit.id] ?? ''} onChange={(e) => setPicked({ ...picked, [visit.id]: e.target.value })}>
                            <option value="">{t('web.visits.reassignTo')}</option>
                            {others.map((u) => (
                              <option key={u.id} value={u.id}>
                                {u.email ?? u.id}
                              </option>
                            ))}
                          </select>
                          <button className="rounded border px-2 py-1" disabled={action.busy || !picked[visit.id]} onClick={() => void action.run(() => admin.reassignVisit(visit.id, picked[visit.id]!))}>
                            {t('web.visits.reassign')}
                          </button>
                        </>
                      )}
                      {canCancel && !finished.includes(visit.status) && (
                        <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} onClick={() => cancel(visit)}>
                          {t('web.visits.cancel')}
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
        {formFor && <VisitFormView key={formFor.id} admin={admin} visit={formFor} />}
      </div>
      {can(me, ...Permissions.planVisits) && users.data && <PlanFromPlaces admin={admin} me={me} date={date} users={users.data} onCreated={plan.reload} />}
      {can(me, ...Permissions.customers) && users.data && <NewVisitForm admin={admin} date={date} users={users.data} onCreated={plan.reload} />}
    </section>
  )
}

function NewVisitForm({ admin, date, users, onCreated }: { admin: AdminApi; date: string; users: UserSummary[]; onCreated: () => void }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const action = useAction(onCreated)
  const [assignee, setAssignee] = useState('')
  const [customer, setCustomer] = useState('')
  const [visitType, setVisitType] = useState<VisitType>('pharmacy')
  const [priority, setPriority] = useState<VisitPriority>('normal')
  const [time, setTime] = useState('')
  // Only approved customers can get a visit (decision D11); the server checks this too.
  const approved = (customers.data ?? []).filter((c: Customer) => c.approvalStatus === 'approved')

  function submit(e: FormEvent) {
    e.preventDefault()
    if (!assignee || !customer) return
    void action.run(() =>
      admin.createVisit({ assignedUserId: assignee, customerId: customer, scheduleDate: date, visitType, priority, plannedTime: time ? `${time}:00` : null }),
    )
  }

  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2 text-sm">
      <h3 className="w-full text-lg font-semibold">{t('web.visits.newVisit', { date })}</h3>
      {(action.error ?? customers.error) && <p role="alert" className="w-full text-red-700">{action.error ?? customers.error}</p>}
      <select aria-label={t('web.visits.assignee')} className="rounded border px-2 py-1" value={assignee} onChange={(e) => setAssignee(e.target.value)}>
        <option value="">{t('web.visits.chooseAssignee')}</option>
        {users.map((u) => (
          <option key={u.id} value={u.id}>
            {u.email ?? u.id}
          </option>
        ))}
      </select>
      <select aria-label={t('web.visits.customer')} className="rounded border px-2 py-1" value={customer} onChange={(e) => setCustomer(e.target.value)}>
        <option value="">{t('web.visits.chooseCustomer')}</option>
        {approved.map((c) => (
          <option key={c.id} value={c.id}>
            {c.code} · {c.name}
          </option>
        ))}
      </select>
      <select aria-label={t('web.visits.visitType')} className="rounded border px-2 py-1" value={visitType} onChange={(e) => setVisitType(e.target.value as VisitType)}>
        {visitTypes.map((type) => (
          <option key={type} value={type}>
            {t(`web.visitType.${type}`)}
          </option>
        ))}
      </select>
      <select aria-label={t('web.visits.priority')} className="rounded border px-2 py-1" value={priority} onChange={(e) => setPriority(e.target.value as VisitPriority)}>
        {priorities.map((p) => (
          <option key={p} value={p}>
            {t(`web.priority.${p}`)}
          </option>
        ))}
      </select>
      <input type="time" aria-label={t('web.visits.plannedTime')} className="rounded border px-2 py-1" value={time} onChange={(e) => setTime(e.target.value)} />
      <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !assignee || !customer}>
        {t('web.visits.create')}
      </button>
    </form>
  )
}

// The specialised form of one visit (plan step 2.7): what the rep wrote beside the base report.
function VisitFormView({ admin, visit }: { admin: AdminApi; visit: PlannedVisit }) {
  const form = useLoad(useCallback(() => admin.visitDetails(visit.id), [admin, visit.id]))
  const d = form.data
  const row = (label: TextKey, value: string | number | null) => (value === null || value === '' ? null : <li key={label}>{t(label)}: {value}</li>)
  return (
    <div className="mt-4 space-y-1 rounded border p-3 text-sm">
      <h3 className="font-semibold">{visit.customerCode} · {visit.customerName}</h3>
      {form.error && <p className="text-slate-600">{t('web.visits.formNone')}</p>}
      {d && (
        <ul>
          {row('web.visits.form.purpose', t(`web.form.purpose.${d.purpose}` as TextKey))}
          {row('web.visits.form.personMet', d.personMet)}
          {row('web.visits.form.personRole', d.personRole)}
          {row('web.visits.form.products', d.products.map((p) => `${p.code} · ${p.name}`).join('، '))}
          {row('web.visits.form.needs', d.needs)}
          {row('web.visits.form.specialty', d.specialty)}
          {row('web.visits.form.dailyPatients', d.dailyPatients)}
          {row('web.visits.form.monthlySales', d.monthlySales === null ? null : `${d.monthlySales} ${d.currency ?? ''}`)}
          {row('web.visits.form.competitors', d.competitors)}
          {row('web.visits.form.monthlyVolume', d.monthlyVolume === null ? null : `${d.monthlyVolume} ${d.currency ?? ''}`)}
          {row('web.visits.form.paymentTerms', d.paymentTerms)}
        </ul>
      )}
    </div>
  )
}
