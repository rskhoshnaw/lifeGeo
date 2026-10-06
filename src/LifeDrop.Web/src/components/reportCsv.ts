import type { KpiRow } from '../api/admin.ts'

// A cell of the CSV file: quoted when needed, and a text that a spreadsheet could run as a formula gets a leading apostrophe (names and emails come from users).
export function csvCell(value: string | number | null): string {
  if (value === null) return ''
  let text = String(value)
  if (typeof value === 'string' && /^[=+\-@\t\r]/.test(text)) text = `'${text}`
  return /[",\n\r]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text
}

export const kpiColumns = [
  'user', 'team', 'visits planned', 'visits completed', 'visits failed', 'completion %', 'failed %', 'visits with a planned time', 'delayed visits', 'delay %',
  'new customers', 'approved new customers', 'orders', 'orders delivered', 'orders not delivered', 'delivery %', 'order total IQD', 'order total USD', 'follow-ups', 'overdue follow-ups', 'overdue %',
]

// One row per user and the total, with the numerators and denominators next to the rates; rates stay empty when there is nothing to measure.
export function kpiCsv(rows: KpiRow[], total: KpiRow): string {
  const line = (r: KpiRow, name: string, team: string) => {
    const c = r.counts
    const money = (currency: string) => c.orderTotals.find((t) => t.currency === currency)?.total ?? 0
    return [
      name, team, c.visitsPlanned, c.visitsCompleted, c.visitsFailed, r.rates.visitCompletion, r.rates.visitFailure, c.delayBase, c.delayedVisits, r.rates.delay,
      c.newCustomers, c.approvedNewCustomers, c.orders, c.ordersDelivered, c.ordersDeliveryFailed, r.rates.orderDelivery, money('IQD'), money('USD'), c.followUpBase, c.followUpsOverdue, r.rates.followUpOverdue,
    ].map(csvCell).join(',')
  }
  return ['\ufeff' + kpiColumns.map(csvCell).join(','), ...rows.map((r) => line(r, r.email ?? r.userId, r.teamName ?? '')), line(total, 'TOTAL', '')].join('\r\n') + '\r\n'
}
