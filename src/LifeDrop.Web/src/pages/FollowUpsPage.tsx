import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, iraqToday, Permissions, type FollowUp, type Me } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const filters = ['', 'pending', 'in_progress', 'completed', 'cancelled']

// Follow-ups (plan step 2.8): reminders for customers with a due day and one responsible user. Reps change the status in the app; here everybody sees the list
// they may read, makes a new one, and managers cancel or give one to somebody else in their area. "Overdue" is worked out by the server.
export function FollowUpsPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [status, setStatus] = useState('')
  const [overdueOnly, setOverdueOnly] = useState(false)
  const list = useLoad(useCallback(() => admin.followUps(status, overdueOnly), [admin, status, overdueOnly]))
  const manage = can(me, ...Permissions.manageFollowUps)
  const users = useLoad(useCallback(() => (manage ? admin.users().catch(() => []) : Promise.resolve([])), [admin, manage]))
  const [picked, setPicked] = useState<Record<string, string>>({})
  const [edit, setEdit] = useState<{ id: string; subject: string; due: string } | null>(null)
  const action = useAction(() => {
    setEdit(null)
    list.reload()
  })

  function cancel(f: FollowUp) {
    const reason = window.prompt(t('web.followups.reasonPrompt'))?.trim()
    if (reason) void action.run(() => admin.cancelFollowUp(f.id, reason))
  }

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.followups.title')}</h2>
        <select aria-label={t('web.followups.filter')} className="rounded border px-2 py-1 text-sm" value={status} onChange={(e) => setStatus(e.target.value)}>
          {filters.map((f) => (
            <option key={f} value={f}>
              {f === '' ? t('web.followups.all') : t(`web.followups.status.${f}` as TextKey)}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-1 text-sm">
          <input type="checkbox" checked={overdueOnly} onChange={(e) => setOverdueOnly(e.target.checked)} />
          {t('web.followups.overdueOnly')}
        </label>
      </div>
      {(list.error || action.error) && <p role="alert" className="text-sm text-red-700">{list.error ?? action.error}</p>}
      {can(me, ...Permissions.createFollowUps) && <NewFollowUp admin={admin} manage={manage} users={users.data ?? []} onCreated={list.reload} />}
      {list.data?.length === 0 && <p className="text-slate-600">{t('web.followups.none')}</p>}
      {list.data && list.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.followups.subject')}</th>
              <th>{t('web.followups.customer')}</th>
              <th>{t('web.followups.due')}</th>
              <th>{t('web.followups.assignee')}</th>
              <th>{t('web.followups.status')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {list.data.map((f) => {
              const open = f.status === 'pending' || f.status === 'in_progress'
              return (
                <tr key={f.id} className="border-b align-top">
                  <td className="py-2">
                    {edit?.id === f.id ? (
                      <input aria-label={t('web.followups.subject')} className="w-64 rounded border px-2 py-1" maxLength={200} value={edit.subject} onChange={(e) => setEdit({ ...edit, subject: e.target.value })} />
                    ) : (
                      f.subject
                    )}
                  </td>
                  <td>
                    {f.customerCode} · {f.customerName}
                  </td>
                  <td>
                    {edit?.id === f.id ? (
                      <input type="date" aria-label={t('web.followups.due')} className="rounded border px-2 py-1" min={f.dueDate < iraqToday() ? f.dueDate : iraqToday()} value={edit.due} onChange={(e) => e.target.value && setEdit({ ...edit, due: e.target.value })} />
                    ) : (
                      f.dueDate
                    )}
                    {edit?.id !== f.id && f.isOverdue && <strong className="text-red-700"> · {t('web.followups.overdue')}</strong>}
                  </td>
                  <td>{f.assigneeEmail ?? f.assigneeId}</td>
                  <td>
                    {t(`web.followups.status.${f.status}` as TextKey)}
                    {f.resultNote ? ` · ${f.resultNote}` : ''}
                    {f.cancelReason ? ` · ${f.cancelReason}` : ''}
                  </td>
                  <td className="space-x-2 py-2 text-end">
                    {open && (manage || f.createdBy === me.id) &&
                      (edit?.id === f.id ? (
                        <>
                          <button className="rounded border px-2 py-1" disabled={action.busy || edit.subject.trim() === ''} onClick={() => void action.run(() => admin.editFollowUp(f.id, edit.subject.trim(), edit.due))}>
                            {t('web.followups.saveEdit')}
                          </button>
                          <button className="rounded border px-2 py-1" onClick={() => setEdit(null)}>
                            {t('web.followups.cancelEdit')}
                          </button>
                        </>
                      ) : (
                        <button className="rounded border px-2 py-1" aria-label={t('web.followups.editAction', { subject: f.subject })} onClick={() => setEdit({ id: f.id, subject: f.subject, due: f.dueDate })}>
                          {t('web.followups.edit')}
                        </button>
                      ))}
                    {manage && open && (
                        <>
                          <select aria-label={t('web.followups.reassignAction', { subject: f.subject })} className="rounded border px-1 py-1" value={picked[f.id] ?? ''} onChange={(e) => setPicked({ ...picked, [f.id]: e.target.value })}>
                            <option value="">{t('web.followups.chooseAssignee')}</option>
                            {(users.data ?? []).filter((u) => u.id !== f.assigneeId).map((u) => (
                              <option key={u.id} value={u.id}>
                                {u.email ?? u.id}
                              </option>
                            ))}
                          </select>
                          <button className="rounded border px-2 py-1" disabled={action.busy || !picked[f.id]} onClick={() => void action.run(() => admin.reassignFollowUp(f.id, picked[f.id]!))}>
                            {t('web.followups.reassign')}
                          </button>
                          <button className="rounded border px-2 py-1 text-red-700" aria-label={t('web.followups.cancelAction', { subject: f.subject })} disabled={action.busy} onClick={() => cancel(f)}>
                            {t('web.followups.cancel')}
                          </button>
                        </>
                    )}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </section>
  )
}

function NewFollowUp({ admin, manage, users, onCreated }: { admin: AdminApi; manage: boolean; users: { id: string; email: string | null }[]; onCreated: () => void }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const [customer, setCustomer] = useState('')
  const [subject, setSubject] = useState('')
  const [due, setDue] = useState(iraqToday())
  const [assignee, setAssignee] = useState('')
  // One reference per form: a retry of a lost answer does not make a second follow-up.
  const [clientRef, setClientRef] = useState(() => crypto.randomUUID())
  const action = useAction()
  const ready = customer !== '' && subject.trim() !== '' && due >= iraqToday()

  async function save(e: FormEvent) {
    e.preventDefault()
    await action.run(async () => {
      await admin.createFollowUp({ customerId: customer, subject: subject.trim(), dueDate: due, assigneeId: assignee || null, clientRef })
      setSubject('')
      setCustomer('')
      setAssignee('')
      setClientRef(crypto.randomUUID())
      onCreated()
    })
  }

  return (
    <form onSubmit={(e) => void save(e)} className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.followups.new')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.followups.customer')} className="rounded border px-2 py-1" value={customer} onChange={(e) => setCustomer(e.target.value)}>
          <option value="">{t('web.followups.chooseCustomer')}</option>
          {(customers.data ?? []).map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {c.name}
            </option>
          ))}
        </select>
        <input aria-label={t('web.followups.subject')} placeholder={t('web.followups.subject')} className="w-64 rounded border px-2 py-1" maxLength={200} value={subject} onChange={(e) => setSubject(e.target.value)} />
        <input type="date" aria-label={t('web.followups.due')} className="rounded border px-2 py-1" min={iraqToday()} value={due} onChange={(e) => e.target.value && setDue(e.target.value)} />
        {manage && (
          <select aria-label={t('web.followups.chooseAssignee')} className="rounded border px-2 py-1" value={assignee} onChange={(e) => setAssignee(e.target.value)}>
            <option value="">{t('web.followups.chooseAssignee')}</option>
            {users.map((u) => (
              <option key={u.id} value={u.id}>
                {u.email ?? u.id}
              </option>
            ))}
          </select>
        )}
        <button type="submit" className="rounded border px-3 py-1" disabled={!ready || action.busy}>
          {t('web.followups.save')}
        </button>
      </div>
    </form>
  )
}
