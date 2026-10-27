import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

// Plan templates (plan step 5.8): plan one week, save it as a template for a field user, and apply it to the coming weeks. Applying uses the normal plan generation, so the
// accepted hours, the user's scope and "already planned that day" still apply; past days are left out.
export function PlanTemplates({ admin, date, users, onApplied }: { admin: AdminApi; date: string; users: UserSummary[]; onApplied: () => void }) {
  const [user, setUser] = useState('')
  const [name, setName] = useState('')
  const [weekStart, setWeekStart] = useState(date)
  const [startDate, setStartDate] = useState(date)
  const [weeks, setWeeks] = useState(4)
  const [message, setMessage] = useState<string | null>(null)
  const list = useLoad(useCallback(() => (user ? admin.planTemplates(user) : Promise.resolve([])), [admin, user]))
  const action = useAction(list.reload)

  function save(e: FormEvent) {
    e.preventDefault()
    void action.run(async () => {
      await admin.savePlanTemplate(name.trim(), weekStart, user)
      setName('')
      setMessage(null)
    })
  }

  async function apply(id: string) {
    const result = await action.run(() => admin.applyPlanTemplate(id, startDate, weeks))
    if (!result) return
    setMessage(t('web.planTemplates.applied', { created: String(result.created), skipped: String(result.skipped), days: String(result.days) }))
    onApplied()
  }

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.planTemplates.title')}</h3>
      <p className="text-slate-600">{t('web.planTemplates.hint')}</p>
      {(action.error ?? list.error) && <p role="alert" className="text-red-700">{action.error ?? list.error}</p>}
      <select aria-label={t('web.planTemplates.user')} className="rounded border px-2 py-1" value={user} onChange={(e) => { setUser(e.target.value); setMessage(null) }}>
        <option value="">{t('web.planTemplates.user')}</option>
        {users.map((u) => (
          <option key={u.id} value={u.id}>
            {u.email ?? u.id}
          </option>
        ))}
      </select>
      {user && (
        <>
          <form onSubmit={save} className="flex flex-wrap items-center gap-2">
            <input aria-label={t('web.planTemplates.name')} placeholder={t('web.planTemplates.name')} maxLength={100} className="rounded border px-2 py-1" value={name} onChange={(e) => setName(e.target.value)} />
            <label className="flex items-center gap-1">
              {t('web.planTemplates.weekStart')}
              <input type="date" className="rounded border px-2 py-1" value={weekStart} onChange={(e) => setWeekStart(e.target.value)} />
            </label>
            <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !name.trim() || !weekStart}>
              {t('web.planTemplates.save')}
            </button>
          </form>
          {list.data?.length === 0 && <p className="text-slate-600">{t('web.planTemplates.none')}</p>}
          {list.data && list.data.length > 0 && (
            <div className="flex flex-wrap items-center gap-2">
              <label className="flex items-center gap-1">
                {t('web.planTemplates.startDate')}
                <input type="date" className="rounded border px-2 py-1" value={startDate} onChange={(e) => setStartDate(e.target.value)} />
              </label>
              <label className="flex items-center gap-1">
                {t('web.planTemplates.weeks')}
                <input type="number" min={1} max={8} className="w-16 rounded border px-2 py-1" value={weeks} onChange={(e) => setWeeks(Math.min(8, Math.max(1, Number(e.target.value) || 1)))} />
              </label>
            </div>
          )}
          {list.data?.map((p) => (
            <div key={p.id} className="flex items-center gap-3 border-b py-1">
              <span className="flex-1">
                {p.name} · {t('web.planTemplates.counts', { customers: String(p.customers), days: String(p.days) })}
              </span>
              <button className="rounded border px-2 py-1" disabled={action.busy || !startDate} aria-label={t('web.planTemplates.applyOf', { name: p.name })} onClick={() => void apply(p.id)}>
                {t('web.planTemplates.apply')}
              </button>
              <button className="rounded border px-2 py-1" disabled={action.busy} aria-label={t('web.planTemplates.deleteOf', { name: p.name })} onClick={() => void action.run(() => admin.deletePlanTemplate(p.id))}>
                ×
              </button>
            </div>
          ))}
        </>
      )}
      {message && <p>{message}</p>}
    </div>
  )
}
