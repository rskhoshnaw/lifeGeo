import { useState } from 'react'
import { AdminApi, customerTypes, doctorClasses, doctorSpecialties, type Customer } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction } from './useLoad.ts'

// Type, code and name of one customer (plan step 4.11); saving closes the editor and reloads the list.
export function CustomerDetailsEditor({ admin, customer, onClose, onSaved }: { admin: AdminApi; customer: Customer; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({ type: customer.type, code: customer.code, name: customer.name })
  const action = useAction(onSaved)
  const [doctor, setDoctor] = useState({
    clinicName: customer.clinicName ?? '',
    specialty: customer.specialty ?? '',
    cooperates: tri(customer.cooperates),
    dealer: tri(customer.dealer),
    customerClass: customer.customerClass ?? '',
  })
  const typeOptions: string[] = customerTypes.some((x) => x.toLowerCase() === form.type.toLowerCase()) ? [...customerTypes] : [...customerTypes, form.type]
  const isDoctor = form.type.toLowerCase() === 'doctor'

  return (
    <div className="my-3 rounded border p-3 text-sm">
      <h3 className="mb-2 font-semibold">{t('web.customers.editTitle', { customer: `${customer.code} · ${customer.name}` })}</h3>
      {action.error && <p role="alert" className="mb-2 text-red-700">{action.error}</p>}
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <input aria-label={t('web.customers.code')} placeholder={t('web.customers.code')} className="rounded border px-2 py-1" maxLength={50} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />
        <input aria-label={t('web.customers.name')} placeholder={t('web.customers.name')} className="min-w-64 flex-1 rounded border px-2 py-1" maxLength={200} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <select aria-label={t('web.customers.type')} className="rounded border px-2 py-1" value={typeOptions.find((x) => x.toLowerCase() === form.type.toLowerCase()) ?? form.type} onChange={(e) => setForm({ ...form, type: e.target.value })}>
          {typeOptions.map((x) => (
            <option key={x} value={x}>{typeLabel(x)}</option>
          ))}
        </select>
      </div>
      {isDoctor && (
        <fieldset className="mb-2 rounded border p-2">
          <legend className="px-1 font-semibold">{t('web.customers.doctorTitle')}</legend>
          <div className="flex flex-wrap items-center gap-2">
            <input aria-label={t('web.customers.clinic')} placeholder={t('web.customers.clinic')} className="min-w-64 flex-1 rounded border px-2 py-1" maxLength={150} value={doctor.clinicName} onChange={(e) => setDoctor({ ...doctor, clinicName: e.target.value })} />
            <select aria-label={t('web.customers.specialty')} className="rounded border px-2 py-1" value={doctor.specialty} onChange={(e) => setDoctor({ ...doctor, specialty: e.target.value })}>
              <option value="">{t('web.customers.specialty')}: {t('web.customers.unset')}</option>
              {doctorSpecialties.map((x) => (
                <option key={x} value={x}>{t(`specialty.${x}` as TextKey)}</option>
              ))}
            </select>
            <select aria-label={t('web.customers.class')} className="rounded border px-2 py-1" value={doctor.customerClass} onChange={(e) => setDoctor({ ...doctor, customerClass: e.target.value })}>
              <option value="">{t('web.customers.class')}: {t('web.customers.unset')}</option>
              {doctorClasses.map((x) => (
                <option key={x} value={x}>{x}</option>
              ))}
            </select>
            <TriSelect label={t('web.customers.cooperates')} value={doctor.cooperates} onChange={(v) => setDoctor({ ...doctor, cooperates: v })} />
            <TriSelect label={t('web.customers.dealer')} value={doctor.dealer} onChange={(v) => setDoctor({ ...doctor, dealer: v })} />
          </div>
        </fieldset>
      )}
      <div className="space-x-2">
        <button
          className="rounded border px-2 py-1"
          disabled={action.busy || !form.code.trim() || !form.name.trim() || !form.type.trim()}
          onClick={() =>
            void action.run(async () => {
              await admin.editCustomer(customer.id, { type: form.type.trim(), code: form.code.trim(), name: form.name.trim() })
              if (isDoctor)
                await admin.setCustomerDoctor(customer.id, {
                  clinicName: doctor.clinicName.trim() || null,
                  specialty: doctor.specialty || null,
                  cooperates: fromTri(doctor.cooperates),
                  dealer: fromTri(doctor.dealer),
                  customerClass: doctor.customerClass || null,
                })
            })
          }
        >
          {t('web.places.save')}
        </button>
        <button className="rounded border px-2 py-1" onClick={onClose}>
          {t('web.customers.closeHours')}
        </button>
      </div>
    </div>
  )
}

type Tri = '' | 'yes' | 'no'
const tri = (value: boolean | null | undefined): Tri => (value === true ? 'yes' : value === false ? 'no' : '')
const fromTri = (value: Tri): boolean | null => (value === 'yes' ? true : value === 'no' ? false : null)

function typeLabel(type: string): string {
  const key = type === 'MedicalAssistant' ? 'medical_assistant' : type.toLowerCase()
  return ['pharmacy', 'medical_assistant', 'doctor', 'wholesaler', 'other'].includes(key) ? t(`app.type.${key}` as TextKey) : type
}

function TriSelect({ label, value, onChange }: { label: string; value: Tri; onChange: (value: Tri) => void }) {
  return (
    <select aria-label={label} className="rounded border px-2 py-1" value={value} onChange={(e) => onChange(e.target.value as Tri)}>
      <option value="">{label}: {t('web.customers.unset')}</option>
      <option value="yes">{label}: {t('web.customers.yes')}</option>
      <option value="no">{label}: {t('web.customers.no')}</option>
    </select>
  )
}
