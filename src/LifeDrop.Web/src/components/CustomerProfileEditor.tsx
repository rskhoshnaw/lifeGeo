import { useCallback, useState } from 'react'
import { AdminApi, type Customer, type Place } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

// Phone, address note and the place of a customer in the reference list (plan step 1.23-b). Only users who may
// change the customer get the save button; the server checks the permission and the customer's area again.
export function CustomerProfileEditor({ admin, customer, canEdit, onClose }: { admin: AdminApi; customer: Customer; canEdit: boolean; onClose: () => void }) {
  const cities = useLoad(useCallback(() => admin.cities(), [admin]))
  const [phone, setPhone] = useState(customer.phone ?? '')
  const [address, setAddress] = useState(customer.address ?? '')
  const [cityId, setCityId] = useState(customer.cityId ?? '')
  const [neighbourhoodId, setNeighbourhoodId] = useState(customer.neighbourhoodId ?? '')
  const [streetId, setStreetId] = useState(customer.streetId ?? '')
  const places = useLoad(useCallback(() => (cityId ? admin.places(cityId) : Promise.resolve([] as Place[])), [admin, cityId]))
  const action = useAction(onClose)

  const options = (kind: Place['kind']) => (places.data ?? []).filter((p) => p.kind === kind)
  const select = (label: string, value: string, set: (v: string) => void, items: { id: string; name: string }[], disabled = false) => (
    <select aria-label={label} className="rounded border px-1 py-1" disabled={!canEdit || disabled} value={value} onChange={(e) => set(e.target.value)}>
      <option value="">{t('web.customers.noPlace')}</option>
      {items.map((item) => (
        <option key={item.id} value={item.id}>
          {item.name}
        </option>
      ))}
    </select>
  )

  function changeCity(id: string) {
    setCityId(id)
    setNeighbourhoodId('')
    setStreetId('')
  }

  const error = action.error ?? cities.error ?? places.error
  return (
    <div className="my-3 rounded border p-3 text-sm">
      <h3 className="mb-2 font-semibold">{t('web.customers.profileTitle', { customer: `${customer.code} · ${customer.name}` })}</h3>
      {error && <p role="alert" className="mb-2 text-red-700">{error}</p>}
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <input aria-label={t('web.customers.phone')} placeholder={t('web.customers.phone')} className="rounded border px-2 py-1" disabled={!canEdit} maxLength={40} value={phone} onChange={(e) => setPhone(e.target.value)} />
        <input aria-label={t('web.customers.address')} placeholder={t('web.customers.address')} className="min-w-64 flex-1 rounded border px-2 py-1" disabled={!canEdit} maxLength={300} value={address} onChange={(e) => setAddress(e.target.value)} />
      </div>
      <div className="mb-2 flex flex-wrap items-center gap-2">
        {select(t('web.places.city'), cityId, changeCity, cities.data ?? [])}
        {select(t('web.places.kind.neighbourhood'), neighbourhoodId, setNeighbourhoodId, options('neighbourhood'), !cityId)}
        {select(t('web.places.kind.street'), streetId, setStreetId, options('street'), !cityId)}
      </div>
      <div className="space-x-2">
        {canEdit && (
          <button
            className="rounded border px-2 py-1"
            disabled={action.busy}
            onClick={() =>
              void action.run(() =>
                admin.setCustomerProfile(customer.id, {
                  phone: phone.trim() || null,
                  address: address.trim() || null,
                  cityId: cityId || null,
                  neighbourhoodId: neighbourhoodId || null,
                  streetId: streetId || null,
                }),
              )
            }
          >
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
