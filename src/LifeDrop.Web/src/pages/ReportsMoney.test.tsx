import { render, screen } from '@testing-library/react'
import type { AdminApi, KpiReport } from '../api/admin.ts'
import { ReportsPage } from './ReportsPage.tsx'

const base = { visitsPlanned: 2, visitsCompleted: 1, visitsFailed: 0, delayBase: 0, delayedVisits: 0, newCustomers: 0, approvedNewCustomers: 0, orders: 1, ordersDelivered: 0, ordersDeliveryFailed: 0, orderTotals: [{ currency: 'IQD', total: 1000 }], followUpBase: 0, followUpsOverdue: 0 }
const rates = { visitCompletion: 50, visitFailure: 0, delay: null, orderDelivery: null, followUpOverdue: null }

describe('the report shows samples, money received and approved returns (step 5.73)', () => {
  it('shows them in the cards and per user, and still opens a result saved before they existed', async () => {
    const withNew = { ...base, samples: 3, paymentsConfirmed: [{ currency: 'IQD', total: 700 }], paymentsWaiting: [{ currency: 'IQD', total: 300 }], returnsApproved: 2, returnTotals: [{ currency: 'USD', total: 5 }] }
    const report = { from: '2026-10-01', to: '2026-10-05', total: { userId: '00000000-0000-0000-0000-000000000000', email: null, teamName: null, counts: withNew, rates }, users: [{ userId: 'u1', email: 'rep@t1', teamName: 'A', counts: withNew, rates }, { userId: 'u2', email: 'old@t1', teamName: null, counts: base, rates }], alerts: [] } as KpiReport
    const admin = { kpis: async () => report, regions: async () => [], teams: async () => [], savedReports: async () => [], users: async () => [], kpiCompare: async () => null, salesReport: async () => ({ from: '2026-10-01', to: '2026-10-05', by: 'warehouse', total: { id: null, name: null, orders: 0, ordersDelivered: 0, orderTotals: [], returnsApproved: 0, returnTotals: [], netSales: [] }, rows: [] }) } as unknown as AdminApi
    render(<ReportsPage admin={admin} />)
    expect((await screen.findAllByText('Samples given')).length).toBeGreaterThan(0)
    expect(screen.getAllByText('700 IQD')).toHaveLength(3)                      // money received: the card, the row of the user and the total row
    expect(screen.getAllByText(/waiting for the finance manager: 300 IQD/).length).toBeGreaterThan(0)
    expect(screen.getAllByText('5 USD')).toHaveLength(3)                        // value of the approved returns: the same three places
    expect(screen.getByText('old@t1')).toBeInTheDocument()                      // a user whose result lacks the new counts is shown, with zeros
  })
})
