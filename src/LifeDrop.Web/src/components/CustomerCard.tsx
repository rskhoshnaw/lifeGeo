import { useCallback, useState } from 'react'
import { AdminApi, errorMessage, type Customer } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { CustomerLocationMap } from './CustomerLocationMap.tsx'
import { WhatsAppButton } from './WhatsAppButton.tsx'
import { useLoad } from './useLoad.ts'

type Position = { latitude: number; longitude: number }

// One customer opened from the list (plan step 5.13): the details, the position on a map and the buttons that edit them. The position comes from the customer map of the user's area;
// a user who may move customers (customers.location.write) clicks the map to choose a new spot and saves it. Editing the other details uses the editors of the list (the buttons call back).
export function CustomerCard({ admin, customer, canEdit, canMove, onEdit, onProfile, onHours, onHistory, onClose }: {
  admin: AdminApi
  customer: Customer
  canEdit: boolean
  canMove: boolean
  onEdit: () => void
  onProfile: () => void
  onHours: () => void
  onHistory: () => void
  onClose: () => void
}) {
  const located = useLoad(useCallback(async () => {
    const all = await admin.mapCustomers()
    const found = all.find((c) => c.id === customer.id)
    return { position: found ? ({ latitude: found.latitude, longitude: found.longitude } as Position) : null }
  }, [admin, customer.id]))
  const [picked, setPicked] = useState<Position | null>(null)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const loadedOnce = located.data !== null
  const position = picked ?? located.data?.position ?? null

  async function savePosition() {
    if (!picked) return
    setBusy(true)
    setError(null)
    try {
      await admin.setCustomerLocation(customer.id, picked.latitude, picked.longitude)
      setSaved(true)
      setPicked(null)
      located.reload()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  const place = (customer.placePath ?? []).map((p) => p.name).join(' › ')
  const rows: { label: TextKey; value: string | null | undefined }[] = [
    { label: 'web.customers.code', value: customer.code },
    { label: 'web.customers.type', value: customer.type },
    { label: 'web.customers.phone', value: customer.phone },
    { label: 'web.customers.phone2', value: customer.phone2 },
    { label: 'web.customers.contactName', value: customer.contactName },
    { label: 'web.customers.address', value: customer.address },
    { label: 'web.customers.place', value: place || null },
    { label: 'web.customers.notes', value: customer.notes },
  ]

  return (
    <div className="my-3 rounded border p-3 text-sm" role="region" aria-label={t('web.customers.cardTitle', { customer: customer.name })}>
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <h3 className="text-base font-semibold">{customer.name}</h3>
        <button className="ms-auto rounded border px-2 py-1" onClick={onClose}>{t('web.customers.closeCard')}</button>
      </div>
      {(error ?? located.error) && <p role="alert" className="mb-2 text-red-700">{error ?? located.error}</p>}
      <div className="grid gap-4 md:grid-cols-2">
        <dl className="space-y-1">
          {rows.filter((r) => r.value).map((r) => (
            <div key={r.label} className="flex gap-2">
              <dt className="min-w-32 text-slate-600">{t(r.label)}</dt>
              <dd>
                {r.value}
                {(r.label === 'web.customers.phone' || r.label === 'web.customers.phone2') && <WhatsAppButton phone={r.value} who={customer.name} />}
              </dd>
            </div>
          ))}
        </dl>
        <div className="space-y-2">
          <CustomerLocationMap position={position} canMove={canMove} onPick={(latitude, longitude) => { setSaved(false); setPicked({ latitude, longitude }) }} />
          {loadedOnce && !position && <p className="text-slate-600">{t('web.customers.noLocation')}</p>}
          {canMove && <p className="text-xs text-slate-600">{t('web.customers.pickOnMap')}</p>}
          {saved && <p role="status" className="text-green-700">{t('web.customers.locationSaved')}</p>}
          {canMove && picked && (
            <button className="rounded border px-2 py-1" disabled={busy} onClick={() => void savePosition()}>{t('web.customers.saveLocation')}</button>
          )}
        </div>
      </div>
      <div className="mt-3 space-x-2">
        {canEdit && <button className="rounded border px-2 py-1" onClick={onEdit}>{t('web.customers.edit')}</button>}
        <button className="rounded border px-2 py-1" onClick={onProfile}>{t('web.customers.profile')}</button>
        <button className="rounded border px-2 py-1" onClick={onHours}>{t('web.customers.visitHours')}</button>
        <button className="rounded border px-2 py-1" onClick={onHistory}>{t('web.customers.history')}</button>
      </div>
    </div>
  )
}
