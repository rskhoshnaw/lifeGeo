import { useCallback, useState, type ChangeEvent, type FormEvent } from 'react'
import { AdminApi, errorMessage, type ImportSummary, type Place, type PlaceKind, type PlaceNames } from '../api/admin.ts'
import { placesFromCsv } from '../components/csv.ts'
import { downloadText } from '../components/download.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { t, type TextKey } from '../i18n.ts'

// The four name fields as typed (not trimmed while typing: a space between two words must stay), and as sent to the server.
interface NameFields {
  name: string
  nameCkb: string
  nameAr: string
  nameEn: string
}
const emptyFields: NameFields = { name: '', nameCkb: '', nameAr: '', nameEn: '' }
const toFields = (n: PlaceNames): NameFields => ({ name: n.name, nameCkb: n.nameCkb ?? '', nameAr: n.nameAr ?? '', nameEn: n.nameEn ?? '' })
const toNames = (f: NameFields): PlaceNames => ({ name: f.name.trim(), nameCkb: f.nameCkb.trim() || null, nameAr: f.nameAr.trim() || null, nameEn: f.nameEn.trim() || null })
const chunk = 1000
const kindText = (kind: PlaceKind) => t(`web.places.kind.${kind}` as TextKey)
// What can be added directly below a place of this kind (below nothing: cities).
const childKinds: Record<PlaceKind | 'root', PlaceKind[]> = { root: ['city'], city: ['neighbourhood'], neighbourhood: ['street', 'alley'], street: ['alley'], alley: [] }

// Places tree (plan step 1.23-b): browse city -> neighbourhood -> street -> alley, add, fix spelling, move, deactivate and
// merge duplicates without any approval step, import a CSV and export the tree for other projects. The server checks
// places.manage on every change.
export function PlacesPage({ admin }: { admin: AdminApi }) {
  const [trail, setTrail] = useState<Place[]>([])
  const current = trail[trail.length - 1] ?? null
  const [showInactive, setShowInactive] = useState(false)
  const [search, setSearch] = useState('')
  const places = useLoad(useCallback(() => admin.places(current?.id ?? null, showInactive, search.trim()), [admin, current?.id, showInactive, search]))
  // Places on the level of the open place: the places below can be moved to any of them.
  const parentOfCurrent = trail.length >= 2 ? trail[trail.length - 2]!.id : null
  const siblings = useLoad(useCallback(() => (current ? admin.places(parentOfCurrent) : Promise.resolve([] as Place[])), [admin, current, parentOfCurrent]))
  const action = useAction(places.reload)
  const [editing, setEditing] = useState<{ id: string; fields: NameFields } | null>(null)
  const [mergeInto, setMergeInto] = useState<Record<string, string>>({})
  const [moveTo, setMoveTo] = useState<Record<string, string>>({})
  const [summary, setSummary] = useState<ImportSummary | null>(null)
  const [fileError, setFileError] = useState<string | null>(null)
  const [busyFile, setBusyFile] = useState(false)

  const items = places.data ?? []
  const open = (place: Place) => {
    setTrail([...trail, place])
    setEditing(null)
    setSearch('')
  }
  const goTo = (depth: number) => {
    setTrail(trail.slice(0, depth))
    setEditing(null)
    setSearch('')
  }

  function merge(place: Place) {
    const target = items.find((p) => p.id === mergeInto[place.id])
    if (!target || !window.confirm(t('web.places.mergeConfirm', { name: place.name, target: target.name }))) return
    void action.run(() => admin.mergePlace(place.id, target.id))
  }

  async function onFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setSummary(null)
    setFileError(null)
    const rows = placesFromCsv(await file.text())
    if (rows.length === 0) return setFileError(t('web.places.importBadFile'))
    setBusyFile(true)
    try {
      const total: ImportSummary = { created: 0, skipped: 0 }
      for (let i = 0; i < rows.length; i += chunk) {
        const part = await admin.importPlaces(rows.slice(i, i + chunk))
        total.created += part.created
        total.skipped += part.skipped
      }
      setSummary(total)
      places.reload()
    } catch (error) {
      setFileError(errorMessage(error))
    } finally {
      setBusyFile(false)
    }
  }

  async function exportAs(format: 'csv' | 'json') {
    setFileError(null)
    try {
      const text = await admin.exportPlaces(format, showInactive)
      if (format === 'csv') downloadText('places.csv', '﻿' + text, 'text/csv;charset=utf-8')
      else downloadText('places.json', text, 'application/json')
    } catch (error) {
      setFileError(errorMessage(error))
    }
  }

  const error = action.error ?? places.error ?? fileError
  return (
    <section className="space-y-6 p-6 text-sm">
      <h2 className="text-xl font-semibold">{t('web.places.title')}</h2>
      {error && <p role="alert" className="text-red-700">{error}</p>}

      <div className="flex flex-wrap items-end gap-4">
        <label className="flex flex-col gap-1">
          <span className="text-slate-600">{t('web.places.import')}</span>
          <input type="file" accept=".csv,text/csv" aria-label={t('web.places.import')} disabled={busyFile} onChange={(e) => void onFile(e)} />
        </label>
        <div className="flex items-center gap-2">
          <button className="rounded border px-2 py-1" onClick={() => void exportAs('csv')}>
            {t('web.places.export')}
          </button>
          <button className="rounded border px-2 py-1" onClick={() => void exportAs('json')}>
            {t('web.places.exportJson')}
          </button>
        </div>
      </div>
      <p className="text-slate-600">{busyFile ? t('web.places.importing') : t('web.places.importHelp')}</p>
      <p className="text-slate-600">{t('web.places.exportHelp')}</p>
      {summary && <p role="status">{t('web.places.importResult', { created: summary.created, skipped: summary.skipped })}</p>}

      <nav aria-label={t('web.places.title')} className="flex flex-wrap items-center gap-2">
        <button className={trail.length === 0 ? 'font-semibold' : 'underline'} onClick={() => goTo(0)}>
          {t('web.places.allCities')}
        </button>
        {trail.map((place, i) => (
          <span key={place.id} className="flex items-center gap-2">
            <span aria-hidden="true">›</span>
            <button className={i === trail.length - 1 ? 'font-semibold' : 'underline'} onClick={() => goTo(i + 1)}>
              {place.name}
            </button>
          </span>
        ))}
      </nav>

      <div className="flex flex-wrap items-center gap-3">
        <input type="search" aria-label={t('web.places.search')} placeholder={t('web.places.search')} className="rounded border px-2 py-1" value={search} onChange={(e) => setSearch(e.target.value)} />
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          {t('web.places.showInactive')}
        </label>
      </div>

      {places.data && items.length === 0 && <p className="text-slate-600">{t('web.places.none')}</p>}
      {items.length > 0 && (
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
            {items.map((place) => {
              const merged = place.mergedInto !== null
              const isEditing = editing?.id === place.id
              const names: PlaceNames = { name: place.name, nameCkb: place.nameCkb, nameAr: place.nameAr, nameEn: place.nameEn }
              const next: 'active' | 'inactive' = place.status === 'inactive' ? 'active' : 'inactive'
              const mergeTargets = items.filter((p) => p.id !== place.id && p.kind === place.kind && p.status === 'active')
              const moveTargets = (siblings.data ?? []).filter((p) => p.id !== current?.id)
              return (
                <tr key={place.id} className="border-b align-top">
                  {isEditing ? (
                    <EditCells fields={editing.fields} onChange={(f) => setEditing({ id: place.id, fields: f })} />
                  ) : (
                    <>
                      <td className="py-2">
                        {place.kind === 'alley' || merged ? (
                          place.name
                        ) : (
                          <button className="underline" aria-label={`${t('web.places.open')} ${place.name}`} onClick={() => open(place)}>
                            {place.name}
                          </button>
                        )}
                        {place.childCount > 0 && <span className="ms-2 text-slate-500">{t('web.places.below', { count: place.childCount })}</span>}
                      </td>
                      <td>{kindText(place.kind)}</td>
                      <td>{place.nameCkb}</td>
                      <td>{place.nameAr}</td>
                      <td>{place.nameEn}</td>
                    </>
                  )}
                  <td>{merged ? t('web.places.merged') : t(`web.places.status.${place.status}` as TextKey)}</td>
                  <td className="space-x-2 py-2 text-end whitespace-nowrap">
                    {!merged && isEditing && (
                      <>
                        <button
                          className="rounded border px-2 py-1"
                          disabled={action.busy || !editing.fields.name.trim()}
                          onClick={() =>
                            void action.run(async () => {
                              await admin.updatePlace(place.id, toNames(editing.fields), place.status)
                              setEditing(null)
                            })
                          }
                        >
                          {t('web.places.save')}
                        </button>
                        <button className="rounded border px-2 py-1" onClick={() => setEditing(null)}>
                          {t('web.places.cancelEdit')}
                        </button>
                      </>
                    )}
                    {!merged && !isEditing && (
                      <>
                        <button className="rounded border px-2 py-1" aria-label={`${t('web.places.edit')} ${place.name}`} onClick={() => setEditing({ id: place.id, fields: toFields(names) })}>
                          {t('web.places.edit')}
                        </button>
                        <button
                          className="rounded border px-2 py-1"
                          aria-label={`${t(next === 'active' ? 'web.places.activate' : 'web.places.deactivate')} ${place.name}`}
                          disabled={action.busy}
                          onClick={() => void action.run(() => admin.updatePlace(place.id, names, next))}
                        >
                          {t(next === 'active' ? 'web.places.activate' : 'web.places.deactivate')}
                        </button>
                        {place.kind === 'city' && (
                          <label className="inline-flex items-center gap-1">
                            <input type="checkbox" checked={!!place.hidePrices} disabled={action.busy} onChange={(e) => void action.run(() => admin.setCityHidePrices(place.id, e.target.checked))} />
                            {t('web.places.hidePrices')}
                          </label>
                        )}
                        {place.status === 'active' && mergeTargets.length > 0 && (
                          <>
                            <select aria-label={t('web.places.mergeTo', { name: place.name })} className="rounded border px-1 py-1" value={mergeInto[place.id] ?? ''} onChange={(e) => setMergeInto({ ...mergeInto, [place.id]: e.target.value })}>
                              <option value="" />
                              {mergeTargets.map((p) => (
                                <option key={p.id} value={p.id}>
                                  {p.name}
                                </option>
                              ))}
                            </select>
                            <button className="rounded border px-2 py-1" aria-label={`${t('web.places.merge')} ${place.name}`} disabled={action.busy || !mergeInto[place.id]} onClick={() => merge(place)}>
                              {t('web.places.merge')}
                            </button>
                          </>
                        )}
                        {current && place.kind !== 'city' && moveTargets.length > 0 && (
                          <>
                            <select aria-label={t('web.places.moveTo', { name: place.name })} className="rounded border px-1 py-1" value={moveTo[place.id] ?? ''} onChange={(e) => setMoveTo({ ...moveTo, [place.id]: e.target.value })}>
                              <option value="" />
                              {moveTargets.map((p) => (
                                <option key={p.id} value={p.id}>
                                  {p.name}
                                </option>
                              ))}
                            </select>
                            <button className="rounded border px-2 py-1" aria-label={`${t('web.places.moveBtn')} ${place.name}`} disabled={action.busy || !moveTo[place.id]} onClick={() => void action.run(() => admin.updatePlace(place.id, names, place.status, moveTo[place.id]!))}>
                              {t('web.places.moveBtn')}
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
      <NewPlaceForm admin={admin} parent={current} onCreated={places.reload} />
    </section>
  )
}

function EditCells({ fields, onChange }: { fields: NameFields; onChange: (fields: NameFields) => void }) {
  const input = (aria: TextKey, key: keyof NameFields) => (
    <input aria-label={t(aria)} className="w-full rounded border px-1 py-1" value={fields[key]} onChange={(e) => onChange({ ...fields, [key]: e.target.value })} />
  )
  return (
    <>
      <td className="py-2">{input('web.places.name', 'name')}</td>
      <td />
      <td>{input('web.places.nameCkb', 'nameCkb')}</td>
      <td>{input('web.places.nameAr', 'nameAr')}</td>
      <td>{input('web.places.nameEn', 'nameEn')}</td>
    </>
  )
}

function NewPlaceForm({ admin, parent, onCreated }: { admin: AdminApi; parent: Place | null; onCreated: () => void }) {
  const action = useAction(onCreated)
  const kinds = childKinds[parent?.kind ?? 'root']
  const [kind, setKind] = useState<PlaceKind | null>(null)
  const [fields, setFields] = useState<NameFields>(emptyFields)
  const chosen = kind && kinds.includes(kind) ? kind : kinds[0]
  if (!chosen) return null

  function submit(e: FormEvent) {
    e.preventDefault()
    if (!fields.name.trim()) return
    void action.run(async () => {
      await admin.createPlace(parent?.id ?? null, chosen!, toNames(fields))
      setFields(emptyFields)
    })
  }

  const input = (aria: TextKey, key: keyof NameFields) => (
    <input aria-label={t(aria)} placeholder={t(aria)} className="rounded border px-2 py-1" value={fields[key]} onChange={(e) => setFields({ ...fields, [key]: e.target.value })} />
  )
  return (
    <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
      <h3 className="w-full text-lg font-semibold">{parent ? t('web.places.addUnder', { name: parent.name }) : t('web.places.addCity')}</h3>
      {action.error && <p role="alert" className="w-full text-red-700">{action.error}</p>}
      {kinds.length > 1 && (
        <select aria-label={t('web.places.kind')} className="rounded border px-2 py-1" value={chosen} onChange={(e) => setKind(e.target.value as PlaceKind)}>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {kindText(k)}
            </option>
          ))}
        </select>
      )}
      {input('web.places.name', 'name')}
      {input('web.places.nameCkb', 'nameCkb')}
      {input('web.places.nameAr', 'nameAr')}
      {input('web.places.nameEn', 'nameEn')}
      <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !fields.name.trim()}>
        {t('web.places.add')}
      </button>
    </form>
  )
}
