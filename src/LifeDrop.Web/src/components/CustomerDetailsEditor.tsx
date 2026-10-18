import { useState } from 'react'
import { AdminApi, type Customer } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction } from './useLoad.ts'

// Type, code and name of one customer (plan step 4.11); saving closes the editor and reloads the list.
export function CustomerDetailsEditor({ admin, customer, onClose, onSaved }: { admin: AdminApi; customer: Customer; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({ type: customer.type, code: customer.code, name: customer.name })
  const action = useAction(onSaved)

  return (
    <div className="my-3 rounded border p-3 text-sm">
      <h3 className="mb-2 font-semibold">{t('web.customers.editTitle', { customer: `${customer.code} · ${customer.name}` })}</h3>
      {action.error && <p role="alert" className="mb-2 text-red-700">{action.error}</p>}
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <input aria-label={t('web.customers.code')} placeholder={t('web.customers.code')} className="rounded border px-2 py-1" maxLength={50} value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} />
        <input aria-label={t('web.customers.name')} placeholder={t('web.customers.name')} className="min-w-64 flex-1 rounded border px-2 py-1" maxLength={200} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        <input aria-label={t('web.customers.type')} placeholder={t('web.customers.type')} className="rounded border px-2 py-1" maxLength={40} value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value })} />
      </div>
      <div className="space-x-2">
        <button
          className="rounded border px-2 py-1"
          disabled={action.busy || !form.code.trim() || !form.name.trim() || !form.type.trim()}
          onClick={() => void action.run(() => admin.editCustomer(customer.id, { type: form.type.trim(), code: form.code.trim(), name: form.name.trim() }))}
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
