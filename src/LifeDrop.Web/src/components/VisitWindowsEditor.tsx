import { useCallback, useState } from 'react'
import { AdminApi, type Customer, type VisitWindow } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'
import { windowsAreValid } from './visitWindows.ts'

const hhmm = (time: string) => time.slice(0, 5)

// Days and hours in which a customer accepts visits. No rows means any time. Only users who may change the
// customer get the editing controls; the server checks the permission and the customer's area again.
export function VisitWindowsEditor({ admin, customer, canEdit, onClose }: { admin: AdminApi; customer: Customer; canEdit: boolean; onClose: () => void }) {
  const stored = useLoad(useCallback(() => admin.visitWindows(customer.id), [admin, customer.id]))
  const [edited, setEdited] = useState<VisitWindow[] | null>(null)
  const [invalid, setInvalid] = useState(false)
  const action = useAction(onClose)
  const rows = edited ?? stored.data ?? []

  const change = (index: number, patch: Partial<VisitWindow>) => {
    setInvalid(false)
    setEdited(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)))
  }

  function save() {
    if (!windowsAreValid(rows)) {
      setInvalid(true)
      return
    }
    void action.run(() => admin.setVisitWindows(customer.id, rows))
  }

  const error = action.error ?? stored.error
  return (
    <div className="my-3 rounded border p-3 text-sm">
      <h3 className="mb-1 font-semibold">{t('web.customers.visitHoursTitle', { customer: `${customer.code} · ${customer.name}` })}</h3>
      <p className="mb-2 text-slate-600">{t('web.customers.visitHoursHelp')}</p>
      {error && <p role="alert" className="mb-2 text-red-700">{error}</p>}
      {invalid && <p role="alert" className="mb-2 text-red-700">{t('web.customers.hoursInvalid')}</p>}
      {stored.data &&
        rows.map((row, index) => (
          <div key={index} className="mb-2 flex flex-wrap items-center gap-2">
            <select aria-label={t('web.customers.weekday')} className="rounded border px-1 py-1" disabled={!canEdit} value={row.weekday} onChange={(e) => change(index, { weekday: Number(e.target.value) })}>
              {[0, 1, 2, 3, 4, 5, 6].map((day) => (
                <option key={day} value={day}>
                  {t(`web.weekday.${day}` as TextKey)}
                </option>
              ))}
            </select>
            <input type="time" aria-label={t('web.customers.opensAt')} className="rounded border px-1 py-1" disabled={!canEdit} value={hhmm(row.opensAt)} onChange={(e) => change(index, { opensAt: `${e.target.value}:00` })} />
            <input type="time" aria-label={t('web.customers.closesAt')} className="rounded border px-1 py-1" disabled={!canEdit} value={hhmm(row.closesAt)} onChange={(e) => change(index, { closesAt: `${e.target.value}:00` })} />
            {canEdit && (
              <button className="rounded border px-2 py-1 text-red-700" onClick={() => setEdited(rows.filter((_, i) => i !== index))}>
                {t('web.customers.removeWindow')}
              </button>
            )}
          </div>
        ))}
      <div className="space-x-2">
        {canEdit && stored.data && (
          <>
            <button className="rounded border px-2 py-1" onClick={() => setEdited([...rows, { weekday: 0, opensAt: '09:00:00', closesAt: '13:00:00' }])}>
              {t('web.customers.addWindow')}
            </button>
            <button className="rounded border px-2 py-1" disabled={action.busy} onClick={save}>
              {t('web.customers.saveHours')}
            </button>
          </>
        )}
        <button className="rounded border px-2 py-1" onClick={onClose}>
          {t('web.customers.closeHours')}
        </button>
      </div>
    </div>
  )
}
