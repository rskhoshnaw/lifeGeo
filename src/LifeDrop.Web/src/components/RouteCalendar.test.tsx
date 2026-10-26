import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { dayLevel, RouteCalendar } from './RouteCalendar.tsx'
import { CustomerHistoryPanel } from './CustomerHistoryPanel.tsx'
import type { AdminApi, Customer, CustomerHistory } from '../api/admin.ts'

describe('route calendar (plan step 5.4)', () => {
  it('colours the days with a route, darker with more points, and picks a day', async () => {
    const onPick = vi.fn()
    const onMonth = vi.fn()
    render(
      <RouteCalendar
        month="2026-10-01"
        today="2026-10-20"
        selected="2026-10-04"
        days={[{ date: '2026-10-01', shifts: 1, points: 0 }, { date: '2026-10-04', shifts: 2, points: 40 }, { date: '2026-10-10', shifts: 1, points: 100 }, { date: '2026-10-12', shifts: 1, points: 500 }]}
        onPick={onPick}
        onMonth={onMonth}
      />,
    )
    expect(screen.getByLabelText('2026-10-02')).toHaveAttribute('data-level', '0')
    expect(screen.getByLabelText('2026-10-01')).toHaveAttribute('data-level', '1')   // a shift whose points are gone
    expect(screen.getByLabelText('2026-10-04')).toHaveAttribute('data-level', '2')
    expect(screen.getByLabelText('2026-10-10')).toHaveAttribute('data-level', '3')
    expect(screen.getByLabelText('2026-10-12')).toHaveAttribute('data-level', '4')
    expect(screen.getByLabelText('2026-10-21')).toBeDisabled()   // the future
    await userEvent.click(screen.getByLabelText('2026-10-10'))
    expect(onPick).toHaveBeenCalledWith('2026-10-10')
    await userEvent.click(screen.getByRole('button', { name: 'Previous month' }))
    expect(onMonth).toHaveBeenCalledWith('2026-09-01')
    expect(screen.getByRole('button', { name: 'Next month' })).toBeDisabled()   // already the current month
  })

  it('starts the week on Saturday', () => {
    render(<RouteCalendar month="2026-10-01" today="2026-10-20" selected="" days={[]} onPick={() => {}} onMonth={() => {}} />)
    // 1 October 2026 is a Thursday: Saturday, Sunday, Monday, Tuesday, Wednesday come before it
    const buttons = screen.getAllByRole('button').filter((b) => /^2026-10-/.test(b.getAttribute('aria-label') ?? ''))
    expect(buttons[0]).toHaveAttribute('aria-label', '2026-10-01')
    expect(dayLevel(undefined)).toBe(0)
  })
})

describe('customer history panel (plan step 5.4)', () => {
  const customer = { id: 'c1', code: 'P-1', name: 'Shifa' } as Customer
  const history = (orders: CustomerHistory['orders']): CustomerHistory => ({
    visits: [
      { id: 'v1', date: '2026-10-05', status: 'Completed', visitType: 'pharmacy', userEmail: 'rep@t1', checkInAt: '2026-10-05T06:00:00Z', checkOutAt: '2026-10-05T06:20:00Z', outcome: 'order_intent', note: 'Wants more syrup', reason: null },
      { id: 'v2', date: '2026-10-06', status: 'Failed', visitType: 'pharmacy', userEmail: 'rep@t1', checkInAt: null, checkOutAt: null, outcome: null, note: null, reason: 'Closed for holiday' },
    ],
    orders,
  })

  it('shows the visits with their result and the orders with their products', async () => {
    const admin = { customerHistory: vi.fn().mockResolvedValue(history([{ id: 'o1', number: 'O-000001', createdAt: '2026-10-05T06:30:00Z', status: 'delivered', currency: 'IQD', total: 250000, pricesHidden: false, createdByEmail: 'rep@t1', lines: [{ productName: 'Cough syrup', quantity: 10, deliveredQuantity: 8 }] }])) } as unknown as AdminApi
    render(<CustomerHistoryPanel admin={admin} customer={customer} onClose={() => {}} />)
    expect(await screen.findByText('Customer intends to order')).toBeInTheDocument()
    expect(screen.getByText(/Wants more syrup/)).toBeInTheDocument()
    expect(screen.getByText(/Closed for holiday/)).toBeInTheDocument()
    expect(screen.getByText('Failed')).toBeInTheDocument()
    expect(screen.getByText('O-000001')).toBeInTheDocument()
    expect(screen.getByText(/Cough syrup × 10 \(8\)/)).toBeInTheDocument()
    expect(screen.getByText('250000 IQD')).toBeInTheDocument()
  })

  it('says so when the reader may not see orders, and hides the amount when prices are hidden', async () => {
    const hidden = { customerHistory: vi.fn().mockResolvedValue(history(null)) } as unknown as AdminApi
    const { unmount } = render(<CustomerHistoryPanel admin={hidden} customer={customer} onClose={() => {}} />)
    expect(await screen.findByText('Orders are not shown for your role.')).toBeInTheDocument()
    unmount()
    const noPrices = { customerHistory: vi.fn().mockResolvedValue(history([{ id: 'o2', number: 'O-000002', createdAt: '2026-10-05T06:30:00Z', status: 'placed', currency: 'IQD', total: 0, pricesHidden: true, createdByEmail: null, lines: [] }])) } as unknown as AdminApi
    render(<CustomerHistoryPanel admin={noPrices} customer={customer} onClose={() => {}} />)
    expect(await screen.findByText('O-000002')).toBeInTheDocument()
    expect(screen.queryByText(/IQD/)).not.toBeInTheDocument()
  })
})
