import type { KpiPeriod } from '../api/admin.ts'
import type { TextKey } from '../i18n.ts'

// The KPIs shown side by side for different months (plan step 5.29). A count changes by a percentage of the earlier month, a rate by percentage points; nothing to compare
// (no earlier value, or an earlier month of 0 for a count) is a dash. `better` follows what a good month is: more visits done, fewer failures and delays.
export interface KpiMetric {
  id: string
  label: TextKey
  kind: 'count' | 'rate'
  higherIsBetter: boolean
  value: (period: KpiPeriod) => number | null
}

export const kpiMetrics: KpiMetric[] = [
  { id: 'planned', label: 'web.kpiCompare.planned', kind: 'count', higherIsBetter: true, value: (p) => p.counts.visitsPlanned },
  { id: 'completed', label: 'web.kpiCompare.completed', kind: 'count', higherIsBetter: true, value: (p) => p.counts.visitsCompleted },
  { id: 'completion', label: 'web.reports.completion', kind: 'rate', higherIsBetter: true, value: (p) => p.rates.visitCompletion },
  { id: 'failure', label: 'web.reports.failure', kind: 'rate', higherIsBetter: false, value: (p) => p.rates.visitFailure },
  { id: 'delay', label: 'web.reports.delay', kind: 'rate', higherIsBetter: false, value: (p) => p.rates.delay },
  { id: 'newCustomers', label: 'web.reports.newCustomers', kind: 'count', higherIsBetter: true, value: (p) => p.counts.newCustomers },
  { id: 'orders', label: 'web.reports.orders', kind: 'count', higherIsBetter: true, value: (p) => p.counts.orders },
  { id: 'delivery', label: 'web.reports.delivery', kind: 'rate', higherIsBetter: true, value: (p) => p.rates.orderDelivery },
  { id: 'followups', label: 'web.reports.followups', kind: 'rate', higherIsBetter: false, value: (p) => p.rates.followUpOverdue },
]

export function formatKpi(metric: KpiMetric, value: number | null): string {
  if (value === null) return '—'
  return metric.kind === 'rate' ? `${value}%` : String(value)
}

const sign = (n: number) => (n > 0 ? '+' : '')

// The change from `base` to `current`: text such as "+12.5%" (a count) or "-3.2 pt" (a rate), and whether it is better (true), worse (false) or neither (null).
export function kpiChange(metric: KpiMetric, current: number | null, base: number | null): { text: string; better: boolean | null } {
  if (current === null || base === null) return { text: '—', better: null }
  if (metric.kind === 'count' && base === 0) return { text: '—', better: null }
  const diff = metric.kind === 'count' ? ((current - base) / base) * 100 : current - base
  const rounded = Math.round(diff * 10) / 10
  const text = `${sign(rounded)}${rounded}${metric.kind === 'count' ? '%' : ' pt'}`
  return { text, better: rounded === 0 ? null : rounded > 0 === metric.higherIsBetter }
}
