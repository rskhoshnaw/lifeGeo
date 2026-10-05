import { useCallback, useState, type ChangeEvent, type FormEvent } from 'react'
import { AdminApi, errorMessage, type ImportSummary, type Place, type PlaceKind, type PlaceNames } from '../api/admin.ts'
import { placesFromCsv } from '../components/csv.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { t, type TextKey } from '../i18n.ts'

type StatusFilter = 'open' | 'suggested' | 'inactive'
const kinds: PlaceKind[] = ['neighbourhood', 'street']
const emptyNames: PlaceNames = { name: '', nameCkb: null, nameAr: null, nameEn: null }
const orNull = (value: string) => value.trim() || null
const chunk = 1000

// Reference list of cities, neighbourhoods and streets (plan step 1.23-b): import, add, rename, approve a field
// user's suggestion, deactivate and merge duplicates. The server checks places.manage on every action.
export function PlacesPage({ admin }: { admin: AdminApi }) {
  const cities = useLoad(useCallback(() => admin.cities(true), [admin]))
  const [cityId, setCityId] = useState('')
  const [kind, setKind] = useState<PlaceKind | ''>('')
  const [filter, setFilter] = useState<StatusFilter>('open')
  const [search, setSearch] = useState('')
  const places = useLoad(useCallback(() => (cityId ? admin.places(cityId, kind, search.trim(), true) : Promise.resolve([] as Place[])), [admin, cityId, kind, search]))
  const refresh = () => {
    cities.reload()
    places.reload()
  }
  const action = useAction(refresh)
  const [newCity, setNewCity] = useState('')
  const [editing, setEditing] = useState<{ id: string; names: PlaceNames } | null>(null)
  const [mergeInto, setMergeInto] = useState<Record<string, string>>({})
  const [summary, setSummary] = useState<ImportSummary | null>(null)
  const [importError, setImportError] = useState<string | null>(null)
  const [importing, setImporting] = useState(false)

  const all = places.data ?? []
  const shown = all.filter((p) => (filter === 'open' ? p.status !== 'inactive' : p.status === filter))
  const label = (p: Place) => p.name

  function merge(place: Place) {
    const target = all.find((p) => p.id === mergeInto[place.id])
    if (!target || !window.confirm(t('web.places.mergeConfirm', { name: label(place), target: label(target) }))) return
    void action.run(() => admin.mergePlace(place.id, target.id))
  }

  async function onFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setSummary(null)
    setImportError(null)
    const rows = placesFromCsv(await file.text())
    if (rows.length === 0) return setImportError(t('web.places.importBadFile'))
    setImporting(true)
    try {
      const total: ImportSummary = { citiesCreated: 0, placesCreated: 0, skipped: 0 }
      for (let i = 0; i < rows.length; i += chunk) {
        const part = await admin.importPlaces(rows.slice(i, i + chunk))
        total.citiesCreated += part.citiesCreated
        total.placesCreated += part.placesCreated
        total.skipped += part.skipped
      }
      setSummary(total)
      refresh()
    } catch (error) {
      setImportError(errorMessage(error))
    } finally {
      setImporting(false)
    }
  }

  const error = action.error ?? cities.error ?? places.error ?? importError
  return (
    <section className="space-y-6 p-6 text-sm">
      <h2 className="text-xl font-semibold">{t('web.places.title')}</h2>
      {error && <p role="alert" className="text-red-700">{error}</p>}

      <div className="flex flex-wrap items-end gap-2">
        <label className="flex flex-col gap-1">
          <span className="text-slate-600">{t('web.places.import')}</span>
          <input type="file" accept=".csv,text/csv" aria-label={t('web.places.import')} disabled={importing} onChange={(e) => void onFile(e)} />
        </label>
        <span className="text-slate-600">{importing ? t('web.places.importing') : t('web.places.importHelp')}</span>
      </div>
      {summary && <p role="status">{t('web.places.importResult', { places: summary.placesCreated, cities: summary.citiesCreated, skipped: summary.skipped })}</p>}

      <div className="flex flex-wrap items-end gap-2">
        <select aria-label={t('web.places.city')} className="rounded border px-2 py-1" value={cityId} onChange={(e) => setCityId(e.target.value)}>
          <option value="">{t('web.places.chooseCity')}</option>
          {(cities.data ?? []).map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
        <input aria-label={t('web.places.newCity')} placeholder={t('web.places.newCity')} className="rounded border px-2 py-1" value={newCity} onChange={(e) => setNewCity(e.target.value)} />
        <button
          className="rounded border px-2 py-1"
          disabled={action.busy || !newCity.trim()}
          onClick={() => void action.run(async () => {
            await admin.createCity(newCity.trim())
            setNewCity('')
          })}
        >
          {t('web.places.addCity')}
        </button>
      </div>

      {cityId && (
        <>
          <div className="flex flex-wrap items-end gap-2">
            <select aria-label={t('web.places.kind')} className="rounded border px-2 py-1" value={kind} onChange={(e) => setKind(e.target.value as PlaceKind | '')}>
              <option value="">{t('web.places.allKinds')}</option>
              {kinds.map((k) => (
                <option key={k} value={k}>
                  {t(`web.places.kind.${k}` as TextKey)}
                </option>
              ))}
            </select>
            <select aria-label={t('web.places.status')} className="rounded border px-2 py-1" value={filter} onChange={(e) => setFilter(e.target.value as StatusFilter)}>
              <option value="open">{t('web.places.filterOpen')}</option>
              <option value="suggested">{t('web.places.status.suggested')}</option>
              <option value="inactive">{t('web.places.status.inactive')}</option>
            </select>
            <input type="search" aria-label={t('web.places.search')} placeholder={t('web.places.search')} className="rounded border px-2 py-1" value={search} onChange={(e) => setSearch(e.target.value)} />
          </div>

          {places.data && shown.length === 0 && <p className="text-slate-600">{t('web.places.none')}</p>}
          {shown.length > 0 && (
            <table className="w-full text-start">
              <thead>
                <tr className="border-b">
                  <th className="py-2">{t('web.places.name')}</th>
                  <th>{t('web.places.kind')}</th>
                  <th>{t('web.places.nameCkb')}</th>
                  <th>{t('web.places.nameAr')}</th>
                  <th>{t('web.places.nameEn')}</th>
                  <th>{t('web.places.status')}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {shown.map((place) => {
                  const merged = place.mergedInto !== null
                  const targets = all.filter((p) => p.id !== place.id && p.kind === place.kind && p.status !== 'inactive')
                  const next: 'active' | 'inactive' = place.status === 'inactive' ? 'active' : 'inactive'
                  const names: PlaceNames = { name: place.name, nameCkb: place.nameCkb, nameAr: place.nameAr, nameEn: place.nameEn }
                  return (
                    <tr key={place.id} className="border-b align-top">
                      {editing?.id === place.id ? (
                        <EditRow names={editing.names} onChange={(n) => setEditing({ id: place.id, names: n })} />
                      ) : (
                        <>
                          <td className="py-2">{place.name}</td>
                          <td>{t(`web.places.kind.${place.kind}` as TextKey)}</td>
                          <td>{place.nameCkb}</td>
                          <td>{place.nameAr}</td>
                          <td>{place.nameEn}</td>
                        </>
                      )}
                      <td>{merged ? t('web.places.merged') : t(`web.places.status.${place.status}` as TextKey)}</td>
                      <td className="space-x-2 py-2 text-end whitespace-nowrap">
                        {!merged && editing?.id === place.id && (
                          <>
                            <button className="rounded border px-2 py-1" disabled={action.busy || !editing.names.name.trim()} onClick={() => void action.run(async () => {
                              await admin.updatePlace(place.id, { ...editing.names, name: editing.names.name.trim() }, place.status === 'inactive' ? 'inactive' : 'active')
                              setEditing(null)
                            })}>
                              {t('web.places.save')}
                            </button>
                            <button className="rounded border px-2 py-1" onClick={() => setEditing(null)}>
                              {t('web.places.cancelEdit')}
                            </button>
                          </>
                        )}
                        {!merged && editing?.id !== place.id && (
                          <>
                            <button className="rounded border px-2 py-1" aria-label={`${t('web.places.edit')} ${label(place)}`} onClick={() => setEditing({ id: place.id, names })}>
                              {t('web.places.edit')}
                            </button>
                            {place.status === 'suggested' && (
                              <button className="rounded border px-2 py-1" aria-label={`${t('web.action.approve')} ${label(place)}`} disabled={action.busy} onClick={() => void action.run(() => admin.updatePlace(place.id, names, 'active'))}>
                                {t('web.action.approve')}
                              </button>
                            )}
                            <button className="rounded border px-2 py-1" aria-label={`${t(next === 'active' ? 'web.places.activate' : 'web.places.deactivate')} ${label(place)}`} disabled={action.busy} onClick={() => void action.run(() => admin.updatePlace(place.id, names, next))}>
                              {t(next === 'active' ? 'web.places.activate' : 'web.places.deactivate')}
                            </button>
                            {place.status !== 'inactive' && targets.length > 0 && (
                              <>
                                <select aria-label={t('web.places.mergeTo', { name: label(place) })} className="rounded border px-1 py-1" value={mergeInto[place.id] ?? ''} onChange={(e) => setMergeInto({ ...mergeInto, [place.id]: e.target.value })}>
                                  <option value="" />
                                  {targets.map((p) => (
                                    <option key={p.id} value={p.id}>
                                      {label(p)}
                                    </option>
                                  ))}
                                </select>
                                <button className="rounded border px-2 py-1" aria-label={`${t('web.places.merge')} ${label(place)}`} disabled={action.busy || !mergeInto[place.id]} onClick={() => merge(place)}>
                                  {t('web.places.merge')}
                                </button>
                              </>
                            )}
                          </>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
          <NewPlaceForm admin={admin} cityId={cityId} onCreated={refresh} />
        </>
      )}
    </section>
  )
}

function EditRow({ names, onChange }: { names: PlaceNames; onChange: (names: PlaceNames) => void }) {
  const input = (aria: TextKey, value: string | null, set: (v: string) => PlaceNames) => (
    <input aria-label={t(aria)} className="w-full rounded border px-1 py-1" value={value ?? ''} onChange={(e) => onChange(set(e.target.value))} />
  )
  return (
    <>
      <td className="py-2">{input('web.places.name', names.name, (v) => ({ ...names, name: v }))}</td>
      <td />
      <td>{input('web.places.nameCkb', names.nameCkb, (v) => ({ ...names, nameCkb: orNull(v) }))}</td>
      <td>{input('web.places.nameAr', names.nameAr, (v) => ({ ...names, nameAr: orNull(v) }))}</td>
      <td>{input('web.places.nameEn', names.nameEn, (v) => ({ ...names, nameEn: orNull(v) }))}</td>
    </>
  )
}

function NewPlaceForm({ admin, cityId, onCreated }: { admin: AdminApi; cityId: string; onCreated: () => void }) {
  const action = useAction(onCreated)
  const [kind, setKind] = useState<PlaceKind>('street')
  const [names, setNames] = useState<PlaceNames>(emptyNames)

  function submit(e: FormEvent) {
    e.preventDefault()
    if (!names.name.trim()) return
    void action.run(async () => {
      await admin.createPlace(cityId, kind, { ...names, name: names.name.trim() })
      setNames(emptyNames)
    })
  }

  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
      <h3 className="w-full text-lg font-semibold">{t('web.places.add')}</h3>
      {action.error && <p role="alert" className="w-full text-red-700">{action.error}</p>}
      <select aria-label={t('web.places.kind')} className="rounded border px-2 py-1" value={kind} onChange={(e) => setKind(e.target.value as PlaceKind)}>
        {kinds.map((k) => (
          <option key={k} value={k}>
            {t(`web.places.kind.${k}` as TextKey)}
          </option>
        ))}
      </select>
      <input aria-label={t('web.places.name')} placeholder={t('web.places.name')} className="rounded border px-2 py-1" value={names.name} onChange={(e) => setNames({ ...names, name: e.target.value })} />
      <input aria-label={t('web.places.nameCkb')} placeholder={t('web.places.nameCkb')} className="rounded border px-2 py-1" value={names.nameCkb ?? ''} onChange={(e) => setNames({ ...names, nameCkb: orNull(e.target.value) })} />
      <input aria-label={t('web.places.nameAr')} placeholder={t('web.places.nameAr')} className="rounded border px-2 py-1" value={names.nameAr ?? ''} onChange={(e) => setNames({ ...names, nameAr: orNull(e.target.value) })} />
      <input aria-label={t('web.places.nameEn')} placeholder={t('web.places.nameEn')} className="rounded border px-2 py-1" value={names.nameEn ?? ''} onChange={(e) => setNames({ ...names, nameEn: orNull(e.target.value) })} />
      <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !names.name.trim()}>
        {t('web.places.add')}
      </button>
    </form>
  )
}
