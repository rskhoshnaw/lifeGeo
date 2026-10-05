import { useCallback, useState } from 'react'
import { AdminApi, type Customer, type Place } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

// Phone, address note and the place of a customer (plan step 1.23-b): city, then neighbourhood, street and alley, each
// list below the one chosen above it. The most precise choice is saved. Only users who may change the customer get the
// save button; the server checks the permission and the customer's area again.
export function CustomerProfileEditor({ admin, customer, canEdit, onClose }: { admin: AdminApi; customer: Customer; canEdit: boolean; onClose: () => void }) {
  const [phone, setPhone] = useState(customer.phone ?? '')
  const [address, setAddress] = useState(customer.address ?? '')
  const [path, setPath] = useState<string[]>((customer.placePath ?? []).map((p) => p.id))
  const action = useAction(onClose)

  const choose = (level: number, id: string) => setPath([...path.slice(0, level), ...(id ? [id] : [])])
  const placeId = path[path.length - 1] ?? null

  return (
    <div className="my-3 rounded border p-3 text-sm">
      <h3 className="mb-2 font-semibold">{t('web.customers.profileTitle', { customer: `${customer.code} · ${customer.name}` })}</h3>
      {action.error && <p role="alert" className="mb-2 text-red-700">{action.error}</p>}
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <input aria-label={t('web.customers.phone')} placeholder={t('web.customers.phone')} className="rounded border px-2 py-1" disabled={!canEdit} maxLength={40} value={phone} onChange={(e) => setPhone(e.target.value)} />
        <input aria-label={t('web.customers.address')} placeholder={t('web.customers.address')} className="min-w-64 flex-1 rounded border px-2 py-1" disabled={!canEdit} maxLength={300} value={address} onChange={(e) => setAddress(e.target.value)} />
      </div>
      <div className="mb-2 flex flex-wrap items-center gap-2">
        {/* One list per level: the cities, then what is below the chosen city, and so on while something is chosen. */}
        {Array.from({ length: Math.min(path.length + 1, 4) }, (_, level) => (
          <LevelSelect key={`${level}-${path[level - 1] ?? 'root'}`} admin={admin} parentId={level === 0 ? null : path[level - 1]!} value={path[level] ?? ''} disabled={!canEdit} onChange={(id) => choose(level, id)} />
        ))}
      </div>
      <div className="space-x-2">
        {canEdit && (
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.setCustomerProfile(customer.id, { phone: phone.trim() || null, address: address.trim() || null, placeId }))}>
            {t('web.places.save')}
          </button>
        )}
        <button className="rounded border px-2 py-1" onClick={onClose}>
          {t('web.customers.closeHours')}
        </button>
      </div>
    </div>
  )
}

// The places directly below one place (the cities for null). Hidden when there is nothing to choose and nothing chosen.
function LevelSelect({ admin, parentId, value, disabled, onChange }: { admin: AdminApi; parentId: string | null; value: string; disabled: boolean; onChange: (id: string) => void }) {
  const items = useLoad(useCallback(() => admin.places(parentId), [admin, parentId]))
  const list: Place[] = items.data ?? []
  if (items.data && list.length === 0 && !value) return null
  const label = list[0] ? t(`web.places.kind.${list[0].kind}` as TextKey) : ''
  return (
    <select aria-label={label || t('web.places.kind')} className="rounded border px-1 py-1" disabled={disabled} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">{t('web.customers.noPlace')}</option>
      {list.map((place) => (
        <option key={place.id} value={place.id}>
          {place.name}
        </option>
      ))}
    </select>
  )
}
