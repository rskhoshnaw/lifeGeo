import { useCallback, useState } from 'react'
import { AdminApi, can, Permissions, type Me, type PlanCandidate, type PlanOrder, type PlanPreview, type PlanRequest, type UserSummary, type VisitPriority, type VisitType } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { PlaceCascade } from './PlaceCascade.tsx'
import { useAction, useLoad } from './useLoad.ts'

const orders: PlanOrder[] = ['least_recent', 'name', 'code']
const visitTypes: VisitType[] = ['pharmacy', 'doctor', 'wholesaler', 'other']
const priorities: VisitPriority[] = ['high', 'normal', 'low']

// Plan from places (plan step 1.24): a field user, the day, where the customers are (a place and everything below it and/or a
// region), how many and in which order. The list is shown first; the visits are created only for the rows that stay ticked.
// The server applies scope, approval, hours and "already planned that day" again when it creates them.
export function PlanFromPlaces({ admin, me, date, users, onCreated }: { admin: AdminApi; me: Me; date: string; users: UserSummary[]; onCreated: () => void }) {
  const [assignee, setAssignee] = useState('')
  const [path, setPath] = useState<string[]>([])
  const [regionId, setRegionId] = useState('')
  const [count, setCount] = useState('')
  const [order, setOrder] = useState<PlanOrder>('least_recent')
  const [respect, setRespect] = useState(true)
  const [visitType, setVisitType] = useState<VisitType | ''>('')
  const [priority, setPriority] = useState<VisitPriority>('normal')
  const [preview, setPreview] = useState<PlanPreview | null>(null)
  const [ticked, setTicked] = useState<Record<string, boolean>>({})
  const [message, setMessage] = useState<string | null>(null)
  const regions = useLoad(useCallback(() => (can(me, ...Permissions.regions) ? admin.regions() : Promise.resolve([])), [admin, me]))
  const action = useAction()

  const placeId = path[path.length - 1] ?? null
  const request = (): PlanRequest => ({
    assignedUserId: assignee,
    scheduleDate: date,
    placeId,
    regionId: regionId || null,
    count: count.trim() ? Number(count) : null,
    order,
    respectWindows: respect,
    visitType: visitType || null,
    priority,
  })
  const ready = assignee !== '' && (placeId !== null || regionId !== '') && (count.trim() === '' || Number(count) >= 1)
  // Any change of the filters makes the shown list stale.
  const change = <T,>(set: (value: T) => void) => (value: T) => {
    set(value)
    setPreview(null)
    setMessage(null)
  }

  async function showPreview() {
    const result = await action.run(() => admin.previewPlan(request()))
    if (!result) return
    setPreview(result)
    setTicked(Object.fromEntries(result.candidates.filter((c) => c.included).map((c) => [c.customerId, true])))
  }

  const chosen = (preview?.candidates ?? []).filter((c) => ticked[c.customerId])
  async function create() {
    const customerIds = chosen.map((c) => c.customerId)
    const result = await action.run(() => admin.generatePlan({ ...request(), customerIds }))
    if (!result) return
    setMessage(t('web.planGen.created', { created: result.created, skipped: result.skipped }))
    setPreview(null)
    onCreated()
  }

  return (
    <div className="space-y-3 text-sm">
      <h3 className="text-lg font-semibold">{t('web.planGen.title')}</h3>
      <p className="text-slate-600">{t('web.planGen.hint', { date })}</p>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {message && <p role="status" className="text-green-700">{message}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.planGen.assignee')} className="rounded border px-2 py-1" value={assignee} onChange={(e) => change(setAssignee)(e.target.value)}>
          <option value="">{t('web.visits.chooseAssignee')}</option>
          {users.map((u) => (
            <option key={u.id} value={u.id}>
              {u.email ?? u.id}
            </option>
          ))}
        </select>
        <PlaceCascade admin={admin} path={path} onChange={change(setPath)} />
        {(regions.data?.length ?? 0) > 0 && (
          <select aria-label={t('web.planGen.region')} className="rounded border px-2 py-1" value={regionId} onChange={(e) => change(setRegionId)(e.target.value)}>
            <option value="">{t('web.planGen.anyRegion')}</option>
            {regions.data!.map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
              </option>
            ))}
          </select>
        )}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <input type="number" min={1} max={200} aria-label={t('web.planGen.count')} placeholder={t('web.planGen.count')} className="w-44 rounded border px-2 py-1" value={count} onChange={(e) => change(setCount)(e.target.value)} />
        <select aria-label={t('web.planGen.order')} className="rounded border px-2 py-1" value={order} onChange={(e) => change(setOrder)(e.target.value as PlanOrder)}>
          {orders.map((o) => (
            <option key={o} value={o}>
              {t(`web.planGen.order.${o}` as TextKey)}
            </option>
          ))}
        </select>
        <select aria-label={t('web.planGen.visitType')} className="rounded border px-2 py-1" value={visitType} onChange={(e) => change(setVisitType)(e.target.value as VisitType | '')}>
          <option value="">{t('web.visits.visitType')}</option>
          {visitTypes.map((v) => (
            <option key={v} value={v}>
              {t(`web.visitType.${v}`)}
            </option>
          ))}
        </select>
        <select aria-label={t('web.planGen.priority')} className="rounded border px-2 py-1" value={priority} onChange={(e) => change(setPriority)(e.target.value as VisitPriority)}>
          {priorities.map((p) => (
            <option key={p} value={p}>
              {t(`web.priority.${p}`)}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={respect} onChange={(e) => change(setRespect)(e.target.checked)} />
          {t('web.planGen.respectHours')}
        </label>
        <button className="rounded border px-3 py-1" disabled={action.busy || !ready} onClick={() => void showPreview()}>
          {t('web.planGen.preview')}
        </button>
      </div>
      {!ready && assignee !== '' && <p className="text-slate-600">{t('web.planGen.needArea')}</p>}
      {preview && (
        <div className="space-y-2">
          {preview.candidates.length === 0 ? (
            <p className="text-slate-600">{t('web.planGen.empty')}</p>
          ) : (
            <>
              <p>{t('web.planGen.summary', { included: preview.included, matching: preview.matching })}</p>
              <table className="w-full text-start">
                <thead>
                  <tr>
                    <th />
                    <th>{t('web.customers.code')}</th>
                    <th>{t('web.customers.name')}</th>
                    <th>{t('web.planGen.place')}</th>
                    <th>{t('web.planGen.lastVisit')}</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {preview.candidates.map((c: PlanCandidate) => (
                    <tr key={c.customerId} className={c.included ? '' : 'text-slate-400'}>
                      <td>
                        <input type="checkbox" aria-label={`${c.code} ${c.name}`} disabled={!c.included} checked={!!ticked[c.customerId]} onChange={(e) => setTicked({ ...ticked, [c.customerId]: e.target.checked })} />
                      </td>
                      <td>{c.code}</td>
                      <td>{c.name}</td>
                      <td>{c.place ?? ''}</td>
                      <td>{c.lastVisitDate ?? t('web.planGen.never')}</td>
                      <td>{c.skipReason ? t(`web.planGen.skip.${c.skipReason}` as TextKey) : ''}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <button className="rounded border px-3 py-1" disabled={action.busy || chosen.length === 0} onClick={() => void create()}>
                {t('web.planGen.create', { count: chosen.length })}
              </button>
            </>
          )}
        </div>
      )}
    </div>
  )
}
