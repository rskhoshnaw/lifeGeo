import { useState } from 'react'
import { AdminApi, customerTypes, doctorClasses, doctorSpecialties, type CustomerFilters, type UserSummary } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { PlaceCascade } from './PlaceCascade.tsx'

const typeKey = (type: string) => (type === 'MedicalAssistant' ? 'medical_assistant' : type.toLowerCase())

// The filters of the customer list (plan step 5.28): type, place, owner, who added it, the days it was added, specialty, class, position, dealer and cooperates.
// Every change is sent up at once, so the list follows the filters; `users` may be empty (a caller who cannot list users has no owner filters).
export function CustomerFilterBar({ admin, users, filters, onChange }: { admin: AdminApi; users: UserSummary[]; filters: CustomerFilters; onChange: (filters: CustomerFilters) => void }) {
  const [path, setPath] = useState<string[]>([])
  const set = (patch: Partial<CustomerFilters>) => onChange({ ...filters, ...patch })
  const yesNo = (name: 'hasLocation' | 'dealer' | 'cooperates', any: TextKey) => (
    <select aria-label={t(any)} className="rounded border px-2 py-1" value={filters[name] ?? ''} onChange={(e) => set({ [name]: e.target.value as 'yes' | 'no' | '' })}>
      <option value="">{t(any)}</option>
      <option value="yes">{t('web.customers.yes')}</option>
      <option value="no">{t('web.customers.no')}</option>
    </select>
  )
  const userSelect = (name: 'ownerId' | 'createdBy', any: TextKey) => (
    <select aria-label={t(any)} className="rounded border px-2 py-1" value={filters[name] ?? ''} onChange={(e) => set({ [name]: e.target.value })}>
      <option value="">{t(any)}</option>
      {users.map((u) => (
        <option key={u.id} value={u.id}>{u.email ?? u.id}</option>
      ))}
    </select>
  )

  return (
    <div className="mb-4 space-y-2 rounded border p-3 text-sm">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold">{t('web.customers.filtersTitle')}</h3>
        <button
          className="rounded border px-2 py-1"
          onClick={() => {
            setPath([])
            onChange({})
          }}
        >
          {t('web.customers.clearFilters')}
        </button>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.customers.allTypes')} className="rounded border px-2 py-1" value={filters.type ?? ''} onChange={(e) => set({ type: e.target.value })}>
          <option value="">{t('web.customers.allTypes')}</option>
          {customerTypes.map((x) => (
            <option key={x} value={x}>{t(`app.type.${typeKey(x)}` as TextKey)}</option>
          ))}
        </select>
        {users.length > 0 && userSelect('ownerId', 'web.customers.allOwners')}
        {users.length > 0 && userSelect('createdBy', 'web.customers.allCreators')}
        <label className="flex items-center gap-1">
          {t('web.customers.addedFrom')}
          <input type="date" className="rounded border px-2 py-1" value={filters.createdFrom ?? ''} onChange={(e) => set({ createdFrom: e.target.value })} />
        </label>
        <label className="flex items-center gap-1">
          {t('web.customers.addedTo')}
          <input type="date" className="rounded border px-2 py-1" value={filters.createdTo ?? ''} onChange={(e) => set({ createdTo: e.target.value })} />
        </label>
        <select aria-label={t('web.customers.allSpecialties')} className="rounded border px-2 py-1" value={filters.specialty ?? ''} onChange={(e) => set({ specialty: e.target.value })}>
          <option value="">{t('web.customers.allSpecialties')}</option>
          {doctorSpecialties.map((x) => (
            <option key={x} value={x}>{t(`specialty.${x}` as TextKey)}</option>
          ))}
        </select>
        <select aria-label={t('web.customers.allClasses')} className="rounded border px-2 py-1" value={filters.customerClass ?? ''} onChange={(e) => set({ customerClass: e.target.value })}>
          <option value="">{t('web.customers.allClasses')}</option>
          {doctorClasses.map((x) => (
            <option key={x} value={x}>{x}</option>
          ))}
        </select>
        {yesNo('hasLocation', 'web.customers.locationAny')}
        {yesNo('dealer', 'web.customers.dealerAny')}
        {yesNo('cooperates', 'web.customers.cooperatesAny')}
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <PlaceCascade
          admin={admin}
          path={path}
          onChange={(next) => {
            setPath(next)
            set({ placeId: next[next.length - 1] ?? '' })
          }}
        />
      </div>
    </div>
  )
}
