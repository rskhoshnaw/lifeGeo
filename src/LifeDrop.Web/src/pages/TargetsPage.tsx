import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, iraqToday, Permissions, type Me, type Product, type UserSummary } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { downloadText } from '../components/download.ts'
import { targetLines, targetsCsv, type TargetView } from '../components/targetReport.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const views: { value: TargetView; label: TextKey }[] = [
  { value: 'visitor', label: 'web.targets.byVisitor' },
  { value: 'product', label: 'web.targets.byProduct' },
  { value: 'detail', label: 'web.targets.detail' },
]

// Sales targets (plan step 5.5): the target of every visitor and product for a month (in the product's unit, boxes) and what was really delivered of the orders the visitor placed.
// Managers with targets.manage set and clear targets; the report is seen by visitor, by product or in detail and can be downloaded.
export function TargetsPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [month, setMonth] = useState(iraqToday().slice(0, 7))
  const [view, setView] = useState<TargetView>('visitor')
  const rows = useLoad(useCallback(() => admin.targets(`${month}-01`), [admin, month]))
  const manage = can(me, ...Permissions.manageTargets)
  const lines = targetLines(rows.data ?? [], view)

  return (
    <section className="space-y-4 p-6">
      <h2 className="text-xl font-semibold">{t('web.targets.title')}</h2>
      <p className="text-xs text-slate-600">{t('web.targets.note')}</p>
      {rows.error && <p role="alert" className="text-sm text-red-700">{rows.error}</p>}
      <div className="flex flex-wrap items-center gap-3 text-sm">
        <input type="month" aria-label={t('web.targets.month')} className="rounded border px-2 py-1" max={iraqToday().slice(0, 7)} value={month} onChange={(e) => e.target.value && setMonth(e.target.value)} />
        {views.map((v) => (
          <label key={v.value} className="flex items-center gap-1">
            <input type="radio" name="targetView" checked={view === v.value} onChange={() => setView(v.value)} />
            {t(v.label)}
          </label>
        ))}
        <button className="rounded border px-3 py-1" disabled={lines.length === 0} onClick={() => downloadText(`targets-${month}-${view}.csv`, targetsCsv(lines, t(views.find((v) => v.value === view)!.label)), 'text/csv;charset=utf-8')}>
          {t('web.reports.download')}
        </button>
      </div>
      {rows.data?.length === 0 && <p className="text-slate-600">{t('web.targets.none')}</p>}
      {lines.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t(views.find((v) => v.value === view)!.label)}</th>
              <th>{t('web.targets.target')}</th>
              <th>{t('web.targets.delivered')}</th>
              <th>{t('web.targets.percent')}</th>
              <th>{t('web.targets.untargeted')}</th>
            </tr>
          </thead>
          <tbody>
            {lines.map((l) => (
              <tr key={l.key} className="border-b">
                <td className="py-2">{l.label}</td>
                <td>{l.target > 0 ? `${l.target}${l.unit ? ` ${l.unit}` : ''}` : '—'}</td>
                <td>{`${l.actual}${l.unit ? ` ${l.unit}` : ''}`}</td>
                <td>{l.percent === null ? '—' : <strong className={l.percent >= 100 ? 'text-green-700' : l.percent < 50 ? 'text-red-700' : ''}>{l.percent}%</strong>}</td>
                <td>{l.untargeted > 0 ? l.untargeted : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {manage && <TargetForm admin={admin} month={month} onChanged={rows.reload} />}
    </section>
  )
}

// Set (or clear, with 0 or an empty box) the target of one visitor for one product in the chosen month.
export function TargetForm({ admin, month, onChanged }: { admin: AdminApi; month: string; onChanged: () => void }) {
  const users = useLoad(useCallback(() => admin.users(), [admin]))
  const products = useLoad(useCallback(() => admin.products(false), [admin]))
  const [userId, setUserId] = useState('')
  const [productId, setProductId] = useState('')
  const [quantity, setQuantity] = useState('')
  const [saved, setSaved] = useState(false)
  const action = useAction(onChanged)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSaved(false)
    const amount = quantity.trim() === '' ? null : Number(quantity)
    if (amount !== null && (!Number.isFinite(amount) || amount < 0)) return
    const done = await action.run(async () => (await admin.setTarget(userId, productId, `${month}-01`, amount), true))
    if (done) setSaved(true)
  }

  return (
    <form className="grid max-w-3xl grid-cols-1 gap-3 rounded border p-4 text-sm sm:grid-cols-4" onSubmit={(e) => void submit(e)}>
      <h3 className="font-medium sm:col-span-4">{t('web.targets.set', { month })}</h3>
      {(action.error ?? users.error ?? products.error) && <p role="alert" className="text-red-700 sm:col-span-4">{action.error ?? users.error ?? products.error}</p>}
      <select aria-label={t('web.targets.visitor')} className="rounded border px-2 py-1" required value={userId} onChange={(e) => setUserId(e.target.value)}>
        <option value="">{t('web.targets.chooseVisitor')}</option>
        {(users.data ?? []).filter((u: UserSummary) => u.isActive).map((u: UserSummary) => (
          <option key={u.id} value={u.id}>{u.email ?? u.id}</option>
        ))}
      </select>
      <select aria-label={t('web.targets.product')} className="rounded border px-2 py-1" required value={productId} onChange={(e) => setProductId(e.target.value)}>
        <option value="">{t('web.targets.chooseProduct')}</option>
        {(products.data ?? []).map((p: Product) => (
          <option key={p.id} value={p.id}>{`${p.code} · ${p.name} (${p.unit})`}</option>
        ))}
      </select>
      <input aria-label={t('web.targets.quantity')} placeholder={t('web.targets.quantity')} className="rounded border px-2 py-1" inputMode="decimal" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
      <button className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || !userId || !productId}>{t('web.places.save')}</button>
      <p className="text-xs text-slate-600 sm:col-span-4">{t('web.targets.clearHint')}</p>
      {saved && <p role="status" className="text-green-700 sm:col-span-4">{t('web.users.updated')}</p>}
    </form>
  )
}
