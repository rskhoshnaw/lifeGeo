import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, KpiCompare, KpiPeriod } from '../api/admin.ts'
import { KpiCompareView } from './KpiCompareView.tsx'
import { kpiChange, kpiMetrics } from './kpiCompare.ts'

const metric = (id: string) => kpiMetrics.find((m) => m.id === id)!
const period = (month: string, planned: number, completed: number, completion: number | null): KpiPeriod => ({
  month,
  counts: { visitsPlanned: planned, visitsCompleted: completed, visitsFailed: 0, delayBase: 0, delayedVisits: 0, newCustomers: 1, approvedNewCustomers: 1, orders: 2, ordersDelivered: 1, ordersDeliveryFailed: 0, orderTotals: [], followUpBase: 0, followUpsOverdue: 0 },
  rates: { visitCompletion: completion, visitFailure: null, delay: null, orderDelivery: null, followUpOverdue: null },
})

describe('KPI comparison of months (plan step 5.29)', () => {
  it('works out the change of a count in percent and of a rate in points, and says whether it is good', () => {
    expect(kpiChange(metric('completed'), 10, 8)).toEqual({ text: '+25%', better: true })
    expect(kpiChange(metric('completion'), 83.5, 80)).toEqual({ text: '+3.5 pt', better: true })
    expect(kpiChange(metric('failure'), 15, 10)).toEqual({ text: '+5 pt', better: false })   // fewer failures are better
    expect(kpiChange(metric('completion'), 80, 80)).toEqual({ text: '0 pt', better: null })
    expect(kpiChange(metric('planned'), 5, 0)).toEqual({ text: '—', better: null })          // no percentage of nothing
    expect(kpiChange(metric('completion'), null, 50)).toEqual({ text: '—', better: null })
  })

  it('shows the month next to the month before and last year, and asks for another user when one is chosen', async () => {
    const data: KpiCompare = {
      userId: 'u1', email: 'ann@x.com', month: '2026-11',
      current: period('2026-11', 10, 8, 80), previous: period('2026-10', 20, 10, 50), lastYear: period('2025-11', 4, 1, 25),
      trend: [period('2026-10', 20, 10, 50), period('2026-11', 10, 8, 80)],
    }
    const kpiCompare = vi.fn().mockResolvedValue(data)
    const users = [{ id: 'u2', email: 'bob@x.com' }]
    render(<KpiCompareView admin={{ kpiCompare, users: vi.fn().mockResolvedValue(users) } as unknown as AdminApi} />)

    expect(await screen.findByText('ann@x.com')).toBeInTheDocument()
    expect(screen.getByText('2025-11 (last year)')).toBeInTheDocument()
    expect(screen.getAllByText('+30 pt').length).toBeGreaterThan(0)        // completion 80 % against 50 % the month before
    expect(kpiCompare).toHaveBeenLastCalledWith(expect.stringMatching(/^\d{4}-\d{2}$/), undefined)
    await userEvent.selectOptions(await screen.findByLabelText('User'), 'u2')
    expect(kpiCompare).toHaveBeenLastCalledWith(expect.stringMatching(/^\d{4}-\d{2}$/), 'u2')
  })
})
