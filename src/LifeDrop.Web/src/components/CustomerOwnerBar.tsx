import { useState } from 'react'
import { AdminApi, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction } from './useLoad.ts'

// A manager gives customers to another user (plan step 5.28): the ticked ones, or everything one user owns (for example when somebody leaves). The server limits the
// change to the customers in the manager's area and to a new owner of the own region for a supervisor, audits every change, and says how many moved and how many did not.
export function CustomerOwnerBar({ admin, users, ticked, onDone }: { admin: AdminApi; users: UserSummary[]; ticked: string[]; onDone: () => void }) {
  const [owner, setOwner] = useState('')
  const [from, setFrom] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const action = useAction()
  const name = (id: string) => users.find((u) => u.id === id)?.email ?? id

  async function give(request: { customerIds: string[] } | { fromUserId: string }) {
    setMessage(null)
    const result = await action.run(() => admin.setCustomerOwner({ ownerUserId: owner, ...request }))
    if (!result) return
    setMessage(t('web.ownerBar.done', { updated: result.updated, skipped: result.skipped, to: name(owner) }))
    onDone()
  }

  function giveAll() {
    if (!window.confirm(t('web.ownerBar.confirmAll', { from: name(from), to: name(owner) }))) return
    void give({ fromUserId: from })
  }

  return (
    <div className="mb-4 space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.ownerBar.title')}</h3>
      <p className="text-slate-600">{t('web.ownerBar.hint')}</p>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {message && <p role="status" className="text-green-700">{message}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.ownerBar.newOwner')} className="rounded border px-2 py-1" value={owner} onChange={(e) => setOwner(e.target.value)}>
          <option value="">{t('web.ownerBar.chooseOwner')}</option>
          {users.map((u) => (
            <option key={u.id} value={u.id}>{u.email ?? u.id}</option>
          ))}
        </select>
        <button className="rounded border px-2 py-1" disabled={action.busy || !owner || ticked.length === 0} onClick={() => void give({ customerIds: ticked })}>
          {t('web.ownerBar.giveTicked', { count: ticked.length })}
        </button>
        <select aria-label={t('web.ownerBar.fromUser')} className="rounded border px-2 py-1" value={from} onChange={(e) => setFrom(e.target.value)}>
          <option value="">{t('web.ownerBar.fromUser')}</option>
          {users.filter((u) => u.id !== owner).map((u) => (
            <option key={u.id} value={u.id}>{u.email ?? u.id}</option>
          ))}
        </select>
        <button className="rounded border px-2 py-1" disabled={action.busy || !owner || !from || from === owner} onClick={giveAll}>
          {t('web.ownerBar.giveAll')}
        </button>
      </div>
    </div>
  )
}
