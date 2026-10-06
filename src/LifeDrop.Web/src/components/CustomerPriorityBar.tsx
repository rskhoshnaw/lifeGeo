import { useState } from 'react'
import { AdminApi, type VisitPriority } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { PlaceCascade } from './PlaceCascade.tsx'
import { useAction } from './useLoad.ts'

const priorities: VisitPriority[] = ['high', 'normal', 'low']

// A manager gives customers a priority (plan step 1.26): the ticked ones, or every customer of a place and below it. The
// server limits the change to the customers in the manager's area and says how many changed.
export function CustomerPriorityBar({ admin, ticked, onDone }: { admin: AdminApi; ticked: string[]; onDone: () => void }) {
  const [priority, setPriority] = useState<VisitPriority>('high')
  const [path, setPath] = useState<string[]>([])
  const [message, setMessage] = useState<string | null>(null)
  const action = useAction()
  const placeId = path[path.length - 1]

  async function apply(request: { customerIds: string[] } | { placeId: string }) {
    setMessage(null)
    const result = await action.run(() => admin.setCustomerPriority({ priority, ...request }))
    if (!result) return
    setMessage(t('web.priorityBar.done', { updated: result.updated }))
    onDone()
  }

  return (
    <div className="mb-4 space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.priorityBar.title')}</h3>
      <p className="text-slate-600">{t('web.priorityBar.hint')}</p>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {message && <p role="status" className="text-green-700">{message}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.priorityBar.value')} className="rounded border px-2 py-1" value={priority} onChange={(e) => setPriority(e.target.value as VisitPriority)}>
          {priorities.map((p) => (
            <option key={p} value={p}>
              {t(`web.priority.${p}`)}
            </option>
          ))}
        </select>
        <button className="rounded border px-2 py-1" disabled={action.busy || ticked.length === 0} onClick={() => void apply({ customerIds: ticked })}>
          {t('web.priorityBar.applyTicked', { count: ticked.length })}
        </button>
        <PlaceCascade admin={admin} path={path} onChange={setPath} />
        <button className="rounded border px-2 py-1" disabled={action.busy || !placeId} onClick={() => placeId && void apply({ placeId })}>
          {t('web.priorityBar.applyPlace')}
        </button>
      </div>
    </div>
  )
}
