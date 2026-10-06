import { useState } from 'react'
import { AdminApi, type Customer } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { PlaceCascade } from './PlaceCascade.tsx'
import { useAction } from './useLoad.ts'

// Phone, address note and the place of a customer (plan step 1.23-b): city, then neighbourhood, street and alley, each
// list below the one chosen above it. The most precise choice is saved. Only users who may change the customer get the
// save button; the server checks the permission and the customer's area again.
export function CustomerProfileEditor({ admin, customer, canEdit, onClose }: { admin: AdminApi; customer: Customer; canEdit: boolean; onClose: () => void }) {
  const [phone, setPhone] = useState(customer.phone ?? '')
  const [address, setAddress] = useState(customer.address ?? '')
  const [path, setPath] = useState<string[]>((customer.placePath ?? []).map((p) => p.id))
  const action = useAction(onClose)

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
        <PlaceCascade admin={admin} path={path} disabled={!canEdit} onChange={setPath} />
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
