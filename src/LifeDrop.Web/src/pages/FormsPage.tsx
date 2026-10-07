import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, type FormField, type NewFormField } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const types: FormField['type'][] = ['text', 'number', 'choice', 'yes_no', 'date']
const appliesTo: FormField['appliesTo'][] = ['all', 'pharmacy', 'doctor', 'wholesaler', 'other']
const empty = { key: '', type: 'text' as FormField['type'], appliesTo: 'all' as FormField['appliesTo'], labelEn: '', labelCkb: '', labelAr: '', labelFa: '', options: '', isRequired: false, sortOrder: 0 }
const label = (f: FormField) => f.labelEn ?? f.labelCkb ?? f.labelAr ?? f.labelFa ?? f.key

// Extra fields of the visit report (plan step 3.2, part 2): the company manager (forms.manage) defines text, number, choice, yes/no and date fields for one
// visit type or all. A field is never deleted: it is made inactive, so the answers already given stay readable. Key, type and visit type cannot change.
export function FormsPage({ admin }: { admin: AdminApi }) {
  const [showInactive, setShowInactive] = useState(false)
  const list = useLoad(useCallback(() => admin.formFields(showInactive), [admin, showInactive]))
  const action = useAction(list.reload)
  const [form, setForm] = useState(empty)

  async function add(event: FormEvent) {
    event.preventDefault()
    const body: NewFormField = { ...form, key: form.key.trim(), options: form.type === 'choice' ? form.options.split(',').map((o) => o.trim()).filter(Boolean) : [] }
    const done = await action.run(async () => (await admin.createFormField(body), true))
    if (done) setForm(empty)
  }

  const input = (key: 'labelEn' | 'labelCkb' | 'labelAr' | 'labelFa', text: TextKey) => (
    <label className="text-sm">
      {t(text)}
      <input className="mt-1 block w-full rounded border px-2 py-1" value={form[key]} maxLength={100} onChange={(e) => setForm({ ...form, [key]: e.target.value })} />
    </label>
  )

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.forms.title')}</h2>
        <label className="flex items-center gap-1 text-sm">
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          {t('web.forms.showInactive')}
        </label>
      </div>
      {(list.error || action.error) && <p role="alert" className="text-sm text-red-700">{list.error ?? action.error}</p>}

      <form className="grid max-w-3xl grid-cols-2 gap-3 rounded border p-4" onSubmit={(e) => void add(e)}>
        <label className="text-sm">
          {t('web.forms.key')}
          <input className="mt-1 block w-full rounded border px-2 py-1" value={form.key} maxLength={40} required pattern="[a-z][a-z0-9_]{1,39}" onChange={(e) => setForm({ ...form, key: e.target.value })} />
        </label>
        <label className="text-sm">
          {t('web.forms.type')}
          <select className="mt-1 block w-full rounded border px-2 py-1" value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value as FormField['type'] })}>
            {types.map((x) => (
              <option key={x} value={x}>
                {t(`web.forms.type.${x}` as TextKey)}
              </option>
            ))}
          </select>
        </label>
        <label className="text-sm">
          {t('web.forms.appliesTo')}
          <select className="mt-1 block w-full rounded border px-2 py-1" value={form.appliesTo} onChange={(e) => setForm({ ...form, appliesTo: e.target.value as FormField['appliesTo'] })}>
            {appliesTo.map((x) => (
              <option key={x} value={x}>
                {t(`web.forms.applies.${x}` as TextKey)}
              </option>
            ))}
          </select>
        </label>
        <label className="text-sm">
          {t('web.forms.sort')}
          <input type="number" min={0} max={1000} className="mt-1 block w-full rounded border px-2 py-1" value={form.sortOrder} onChange={(e) => setForm({ ...form, sortOrder: Number(e.target.value) || 0 })} />
        </label>
        {input('labelEn', 'web.forms.labelEn')}
        {input('labelCkb', 'web.forms.labelCkb')}
        {input('labelAr', 'web.forms.labelAr')}
        {input('labelFa', 'web.forms.labelFa')}
        {form.type === 'choice' && (
          <label className="col-span-2 text-sm">
            {t('web.forms.options')}
            <input className="mt-1 block w-full rounded border px-2 py-1" value={form.options} onChange={(e) => setForm({ ...form, options: e.target.value })} />
          </label>
        )}
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={form.isRequired} onChange={(e) => setForm({ ...form, isRequired: e.target.checked })} />
          {t('web.forms.required')}
        </label>
        <div className="text-end">
          <button className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || !(form.labelEn || form.labelCkb || form.labelAr || form.labelFa)}>
            {t('web.forms.add')}
          </button>
        </div>
      </form>

      {list.data?.length === 0 && <p className="text-slate-600">{t('web.forms.none')}</p>}
      {list.data && list.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.forms.label')}</th>
              <th>{t('web.forms.key')}</th>
              <th>{t('web.forms.type')}</th>
              <th>{t('web.forms.appliesTo')}</th>
              <th>{t('web.forms.required')}</th>
              <th>{t('web.forms.status')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {list.data.map((f) => (
              <tr key={f.id} className="border-b align-top">
                <td className="py-2">
                  {label(f)}
                  {f.options.length > 0 && <span className="block text-xs text-slate-600">{f.options.join(' · ')}</span>}
                </td>
                <td>{f.key}</td>
                <td>{t(`web.forms.type.${f.type}` as TextKey)}</td>
                <td>{t(`web.forms.applies.${f.appliesTo}` as TextKey)}</td>
                <td>{f.isRequired ? '✓' : ''}</td>
                <td>{f.isActive ? t('web.forms.active') : t('web.forms.inactive')}</td>
                <td className="space-x-2 py-2 text-end">
                  <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.updateFormField(f.id, { isRequired: !f.isRequired }))}>
                    {f.isRequired ? t('web.forms.makeOptional') : t('web.forms.makeRequired')}
                  </button>
                  <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.updateFormField(f.id, { isActive: !f.isActive }))}>
                    {f.isActive ? t('web.forms.deactivate') : t('web.forms.activate')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
