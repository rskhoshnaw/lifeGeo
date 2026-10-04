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

  function cancel(visit: PlannedVisit) {
    const reason = window.prompt(`Reason for cancelling the visit to ${visit.customerName ?? visit.customerId}?`)?.trim()
    if (!reason) return
    void action.run(() => admin.cancelVisit(visit.id, reason))
  }

  const error = action.error ?? plan.error ?? users.error
  return (
    <section className="space-y-8 p-6">
      <div>
        <div className="mb-4 flex items-center gap-4">
          <h2 className="text-xl font-semibold">Visit plan</h2>
          <input type="date" aria-label="Plan date" className="rounded border px-2 py-1 text-sm" value={date} onChange={(e) => e.target.value && setDate(e.target.value)} />
        </div>
        {error && <p role="alert" className="mb-3 text-sm text-red-700">{error}</p>}
        {plan.data?.length === 0 && <p className="text-slate-600">No visits on this day.</p>}
        {plan.data && plan.data.length > 0 && (
          <table className="w-full text-left text-sm">
            <thead>
              <tr className="border-b">
                <th className="py-2">Time</th>
                <th>Customer</th>
                <th>Assignee</th>
                <th>Type</th>
                <th>Priority</th>
                <th>Status</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {plan.data.map((visit) => {
                const label = visit.customerCode ?? visit.customerId
                const others = (users.data ?? []).filter((u) => u.id !== visit.userId)
                return (
                  <tr key={visit.id} className="border-b align-top">
                    <td className="py-2">{visit.plannedTime?.slice(0, 5) ?? '—'}</td>
                    <td>
                      {label} · {visit.customerName ?? ''}
                    </td>
                    <td>{visit.assigneeEmail ?? visit.userId}</td>
                    <td>{visit.visitType}</td>
                    <td>{visit.priority}</td>
                    <td>{visit.status}</td>
                    <td className="space-x-2 py-2 text-right">
                      {visit.status === 'Planned' && others.length > 0 && (
                        <>
                          <select aria-label={`New assignee for ${label}`} className="rounded border px-1 py-1" value={picked[visit.id] ?? ''} onChange={(e) => setPicked({ ...picked, [visit.id]: e.target.value })}>
                            <option value="">Reassign to…</option>
                            {others.map((u) => (
                              <option key={u.id} value={u.id}>
                                {u.email ?? u.id}
                              </option>
                            ))}
                          </select>
                          <button className="rounded border px-2 py-1" disabled={action.busy || !picked[visit.id]} onClick={() => void action.run(() => admin.reassignVisit(visit.id, picked[visit.id]!))}>
                            Reassign
                          </button>
                        </>
                      )}
                      {canCancel && !finished.includes(visit.status) && (
                        <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} onClick={() => cancel(visit)}>
                          Cancel
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </div>
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
      <h3 className="w-full text-lg font-semibold">New visit on {date}</h3>
      {(action.error ?? customers.error) && <p role="alert" className="w-full text-red-700">{action.error ?? customers.error}</p>}
      <select aria-label="Assignee" className="rounded border px-2 py-1" value={assignee} onChange={(e) => setAssignee(e.target.value)}>
        <option value="">Assignee…</option>
        {users.map((u) => (
          <option key={u.id} value={u.id}>
            {u.email ?? u.id}
          </option>
        ))}
      </select>
      <select aria-label="Customer" className="rounded border px-2 py-1" value={customer} onChange={(e) => setCustomer(e.target.value)}>
        <option value="">Customer…</option>
        {approved.map((c) => (
          <option key={c.id} value={c.id}>
            {c.code} · {c.name}
          </option>
        ))}
      </select>
      <select aria-label="Visit type" className="rounded border px-2 py-1" value={visitType} onChange={(e) => setVisitType(e.target.value as VisitType)}>
        {visitTypes.map((t) => (
          <option key={t}>{t}</option>
        ))}
      </select>
      <select aria-label="Priority" className="rounded border px-2 py-1" value={priority} onChange={(e) => setPriority(e.target.value as VisitPriority)}>
        {priorities.map((p) => (
          <option key={p}>{p}</option>
        ))}
      </select>
      <input type="time" aria-label="Planned time" className="rounded border px-2 py-1" value={time} onChange={(e) => setTime(e.target.value)} />
      <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !assignee || !customer}>
        Create visit
      </button>
    </form>
  )
}
