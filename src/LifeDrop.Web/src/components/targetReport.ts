import type { TargetRow } from '../api/admin.ts'
import { csvCell } from './reportCsv.ts'

// The target report (plan step 5.5): the same rows seen by visitor, by product, or one line for every visitor and product.
export interface TargetLine {
  key: string
  label: string
  unit: string | null   // null when the sums mix products with different units
  target: number
  actual: number         // what was delivered of the products that have a target
  untargeted: number     // delivered of products that have no target
  percent: number | null // actual / target, null when there is no target
}

export type TargetView = 'visitor' | 'product' | 'detail'

const round = (value: number) => Math.round(value * 1000) / 1000

function group(rows: TargetRow[], keyOf: (r: TargetRow) => string, labelOf: (r: TargetRow) => string): TargetLine[] {
  const map = new Map<string, { label: string; units: Set<string>; target: number; actual: number; untargeted: number }>()
  for (const r of rows) {
    const key = keyOf(r)
    const entry = map.get(key) ?? { label: labelOf(r), units: new Set<string>(), target: 0, actual: 0, untargeted: 0 }
    entry.units.add(r.unit)
    if (r.target !== null) {
      entry.target += r.target
      entry.actual += r.actual
    } else entry.untargeted += r.actual
    map.set(key, entry)
  }
  return [...map.entries()].map(([key, e]) => ({
    key,
    label: e.label,
    unit: e.units.size === 1 ? [...e.units][0]! : null,
    target: round(e.target),
    actual: round(e.actual),
    untargeted: round(e.untargeted),
    percent: e.target > 0 ? Math.round((e.actual / e.target) * 100) : null,
  }))
}

export function targetLines(rows: TargetRow[], view: TargetView): TargetLine[] {
  if (view === 'visitor') return group(rows, (r) => r.userId, (r) => r.userEmail ?? r.userId)
  if (view === 'product') return group(rows, (r) => r.productId, (r) => `${r.productCode} · ${r.productName}`)
  return rows.map((r) => ({
    key: `${r.userId}:${r.productId}`,
    label: `${r.userEmail ?? r.userId} · ${r.productCode} · ${r.productName}`,
    unit: r.unit,
    target: r.target ?? 0,
    actual: r.target !== null ? r.actual : 0,
    untargeted: r.target === null ? r.actual : 0,
    percent: r.target !== null && r.target > 0 ? Math.round((r.actual / r.target) * 100) : null,
  }))
}

export function targetsCsv(lines: TargetLine[], firstColumn: string): string {
  const header = [firstColumn, 'unit', 'target', 'delivered', 'delivered without a target', 'achieved %']
  return [header.map(csvCell).join(','), ...lines.map((l) => [l.label, l.unit, l.target, l.actual, l.untargeted, l.percent].map(csvCell).join(','))].join('\r\n') + '\r\n'
}
