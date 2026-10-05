import type { ImportRow } from '../api/admin.ts'

// Splits CSV text into records: comma separated, double quotes for fields with commas, quotes or line breaks
// ("" is a quote inside a quoted field), a leading byte order mark and CRLF line breaks are accepted.
export function parseCsv(text: string): string[][] {
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  const input = text.replace(/^﻿/, '')
  for (let i = 0; i < input.length; i++) {
    const c = input[i]!
    if (quoted) {
      if (c === '"' && input[i + 1] === '"') {
        field += '"'
        i++
      } else if (c === '"') quoted = false
      else field += c
    } else if (c === '"') quoted = true
    else if (c === ',') {
      row.push(field)
      field = ''
    } else if (c === '\n' || c === '\r') {
      if (c === '\r' && input[i + 1] === '\n') i++
      row.push(field)
      field = ''
      if (row.some((value) => value.trim() !== '')) rows.push(row)
      row = []
    } else field += c
  }
  row.push(field)
  if (row.some((value) => value.trim() !== '')) rows.push(row)
  return rows
}

// Rows of the Export-CityPlaces.ps1 file (columns city, kind, name_ckb, name_ar, name_en, name_local, osm, active).
// The main name is name_local, else the English, Kurdish or Arabic name. Rows without a city, a valid kind or any
// name are dropped; active = "no" keeps the row but hides it.
export function placesFromCsv(text: string): ImportRow[] {
  const [header, ...records] = parseCsv(text)
  if (!header) return []
  const column = (name: string) => header.findIndex((h) => h.trim().toLowerCase() === name)
  const index = Object.fromEntries(['city', 'kind', 'name_ckb', 'name_ar', 'name_en', 'name_local', 'osm', 'active'].map((n) => [n, column(n)]))
  const cell = (record: string[], name: string) => {
    const at = index[name]!
    return at < 0 ? '' : (record[at] ?? '').trim()
  }
  const rows: ImportRow[] = []
  for (const record of records) {
    const kind = cell(record, 'kind').toLowerCase()
    const names = { ckb: cell(record, 'name_ckb'), ar: cell(record, 'name_ar'), en: cell(record, 'name_en') }
    const name = cell(record, 'name_local') || names.en || names.ckb || names.ar
    const city = cell(record, 'city')
    if (!city || !name || (kind !== 'neighbourhood' && kind !== 'street')) continue
    rows.push({
      city,
      kind,
      name,
      nameCkb: names.ckb || null,
      nameAr: names.ar || null,
      nameEn: names.en || null,
      osm: cell(record, 'osm') || null,
      active: cell(record, 'active').toLowerCase() !== 'no',
    })
  }
  return rows
}
