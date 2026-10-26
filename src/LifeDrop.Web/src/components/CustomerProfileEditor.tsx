import { useState } from 'react'
import { AdminApi, type Customer } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { PlaceCascade } from './PlaceCascade.tsx'
import { whatsappUrl } from './whatsapp.ts'
import { useAction } from './useLoad.ts'

// Phone, address note and the place of a customer (plan step 1.23-b): city, then neighbourhood, street and alley, each
// list below the one chosen above it. The most precise choice is saved. Only users who may change the customer get the
// save button; the server checks the permission and the customer's area again.
export function CustomerProfileEditor({ admin, customer, canEdit, onClose }: { admin: AdminApi; customer: Customer; canEdit: boolean; onClose: () => void }) {
  const [phone, setPhone] = useState(customer.phone ?? '')
  const [address, setAddress] = useState(customer.address ?? '')
  const [contactName, setContactName] = useState(customer.contactName ?? '')
  const [phone2, setPhone2] = useState(customer.phone2 ?? '')
  const [notes, setNotes] = useState(customer.notes ?? '')
  const [path, setPath] = useState<string[]>((customer.placePath ?? []).map((p) => p.id))
  const action = useAction(onClose)

  const placeId = path[path.length - 1] ?? null

  // The contact details have their own route, so it is only called when one of them changed.
  async function save() {
    await admin.setCustomerProfile(customer.id, { phone: phone.trim() || null, address: address.trim() || null, placeId })
    const contact = { contactName: contactName.trim() || null, phone2: phone2.trim() || null, notes: notes.trim() || null }
    if (contact.contactName !== (customer.contactName ?? null) || contact.phone2 !== (customer.phone2 ?? null) || contact.notes !== (customer.notes ?? null)) {
      await admin.setCustomerContact(customer.id, contact)
    }
  }

  return (
    <div className="my-3 rounded border p-3 text-sm">
      <h3 className="mb-2 font-semibold">{t('web.customers.profileTitle', { customer: `${customer.code} · ${customer.name}` })}</h3>
      {action.error && <p role="alert" className="mb-2 text-red-700">{action.error}</p>}
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <input aria-label={t('web.customers.phone')} placeholder={t('web.customers.phone')} className="rounded border px-2 py-1" disabled={!canEdit} maxLength={40} value={phone} onChange={(e) => setPhone(e.target.value)} />
        <WhatsAppLink phone={phone} />
        <input aria-label={t('web.customers.address')} placeholder={t('web.customers.address')} className="min-w-64 flex-1 rounded border px-2 py-1" disabled={!canEdit} maxLength={300} value={address} onChange={(e) => setAddress(e.target.value)} />
      </div>
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <input aria-label={t('web.customers.contactName')} placeholder={t('web.customers.contactName')} className="rounded border px-2 py-1" disabled={!canEdit} maxLength={100} value={contactName} onChange={(e) => setContactName(e.target.value)} />
        <input aria-label={t('web.customers.phone2')} placeholder={t('web.customers.phone2')} className="rounded border px-2 py-1" disabled={!canEdit} maxLength={40} value={phone2} onChange={(e) => setPhone2(e.target.value)} />
        <WhatsAppLink phone={phone2} />
        <input aria-label={t('web.customers.notes')} placeholder={t('web.customers.notes')} className="min-w-64 flex-1 rounded border px-2 py-1" disabled={!canEdit} maxLength={500} value={notes} onChange={(e) => setNotes(e.target.value)} />
      </div>
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <PlaceCascade admin={admin} path={path} disabled={!canEdit} onChange={setPath} />
      </div>
      <div className="space-x-2">
        {canEdit && (
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => save())}>
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

// Opens the WhatsApp chat with the number typed in the box next to it; nothing shows while the text is not a usable number.
function WhatsAppLink({ phone }: { phone: string }) {
  const url = whatsappUrl(phone)
  if (!url) return null
  return (
    <a href={url} target="_blank" rel="noopener noreferrer" className="rounded border px-2 py-1 text-green-700">
      {t('web.customers.whatsapp')}
    </a>
  )
}
