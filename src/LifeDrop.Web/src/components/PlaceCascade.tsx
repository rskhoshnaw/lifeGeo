import { useCallback } from 'react'
import { AdminApi, type Place } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useLoad } from './useLoad.ts'

// One list per level: the cities, then what is below the chosen city, and so on while something is chosen.
// path = the chosen place ids from the city down; the most precise one is the last.
export function PlaceCascade({ admin, path, disabled = false, onChange }: { admin: AdminApi; path: string[]; disabled?: boolean; onChange: (path: string[]) => void }) {
  const choose = (level: number, id: string) => onChange([...path.slice(0, level), ...(id ? [id] : [])])
  return (
    <>
      {Array.from({ length: Math.min(path.length + 1, 4) }, (_, level) => (
        <LevelSelect key={`${level}-${path[level - 1] ?? 'root'}`} admin={admin} parentId={level === 0 ? null : path[level - 1]!} value={path[level] ?? ''} disabled={disabled} onChange={(id) => choose(level, id)} />
      ))}
    </>
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
