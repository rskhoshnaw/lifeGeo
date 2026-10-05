import type { ImportRow, PlaceKind } from '../api/admin.ts'

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

const kinds: PlaceKind[] = ['city', 'neighbourhood', 'street', 'alley']

// Rows of an import or export file. Columns: kind, city, neighbourhood, street, name, name_ckb, name_ar, name_en and
// status (active|inactive) or active (yes|no); id and parent_id are ignored. A row needs a kind, a name and the levels
// above it (city: none, its city column is ignored; neighbourhood: city; street: city and neighbourhood; alley: city and neighbourhood, street
// optional); other rows are dropped. A leading apostrophe before = + - @ (added by the export to keep a spreadsheet
// from running text as a formula) is removed.
export function placesFromCsv(text: string): ImportRow[] {
  const [header, ...records] = parseCsv(text)
  if (!header) return []
  const index = (name: string) => header.findIndex((h) => h.trim().toLowerCase() === name)
  const columns = Object.fromEntries(['kind', 'city', 'neighbourhood', 'street', 'name', 'name_ckb', 'name_ar', 'name_en', 'status', 'active'].map((n) => [n, index(n)]))
  const cell = (record: string[], name: string) => {
    const at = columns[name]!
    const value = at < 0 ? '' : (record[at] ?? '').trim()
    return /^'[=+\-@]/.test(value) ? value.slice(1) : value
  }
  const rows: ImportRow[] = []
  for (const record of records) {
    const kind = cell(record, 'kind').toLowerCase() as PlaceKind
    const name = cell(record, 'name')
    const city = cell(record, 'city')
    const neighbourhood = cell(record, 'neighbourhood')
    const street = cell(record, 'street')
    if (!kinds.includes(kind) || !name) continue
    const valid =
      kind === 'city' ? !neighbourhood && !street
      : kind === 'neighbourhood' ? !!city && !neighbourhood && !street
      : kind === 'street' ? !!city && !!neighbourhood && !street
      : !!city && !!neighbourhood
    if (!valid) continue
    rows.push({
      kind,
      name,
      city: kind === 'city' ? null : city || null,
      neighbourhood: neighbourhood || null,
      street: street || null,
      nameCkb: cell(record, 'name_ckb') || null,
      nameAr: cell(record, 'name_ar') || null,
      nameEn: cell(record, 'name_en') || null,
      active: cell(record, 'status').toLowerCase() !== 'inactive' && cell(record, 'active').toLowerCase() !== 'no',
    })
  }
  return rows
}
