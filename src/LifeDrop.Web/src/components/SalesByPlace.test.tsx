import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, SalesReport } from '../api/admin.ts'
import { SalesByPlace } from './SalesByPlace.tsx'

const row = (id: string | null, name: string | null, orders: number, delivered: number, orderIqd: number, returns: number, returnIqd: number) => ({
  id, name, orders, ordersDelivered: delivered, orderTotals: orderIqd ? [{ currency: 'IQD', total: orderIqd }] : [], returnsApproved: returns, returnTotals: returnIqd ? [{ currency: 'IQD', total: returnIqd }] : [],
  netSales: [{ currency: 'IQD', total: orderIqd - returnIqd }],
})

describe('sales per warehouse and per city (step 5.74)', () => {
  it('shows orders, approved returns and net sales per row and in total, and asks again for the other grouping', async () => {
    const report = (by: 'warehouse' | 'city'): SalesReport => ({
      from: '2026-10-01', to: '2026-10-05', by,
      rows: by === 'warehouse' ? [row('w1', 'Erbil warehouse', 2, 1, 1500, 1, 250)] : [row('p1', 'Erbil', 2, 1, 1500, 1, 250), row(null, null, 1, 0, 2000, 0, 0)],
      total: row(null, null, 3, 1, 3500, 1, 250),
    })
    const salesReport = vi.fn().mockImplementation(async (_from: string, _to: string, by: 'warehouse' | 'city') => report(by))
    const admin = { salesReport } as unknown as AdminApi
    const user = userEvent.setup()
    render(<SalesByPlace admin={admin} filter={{ from: '2026-10-01', to: '2026-10-05', regionId: '', teamId: '', customerType: 'Pharmacy' }} />)
    expect(await screen.findByText('Erbil warehouse')).toBeInTheDocument()
    expect(salesReport).toHaveBeenCalledWith('2026-10-01', '2026-10-05', 'warehouse', '', '', 'Pharmacy')   // the filters of the report above go along
    expect(screen.getByText('1,250 IQD')).toBeInTheDocument()                                              // net sales: 1500 - 250
    expect(screen.getByText('3,250 IQD')).toBeInTheDocument()                                              // the total row: 3500 - 250
    await user.selectOptions(screen.getByRole('combobox', { name: 'Sales by' }), 'city')
    expect(await screen.findByText('(none)')).toBeInTheDocument()                                          // a customer without a city
    expect(salesReport).toHaveBeenLastCalledWith('2026-10-01', '2026-10-05', 'city', '', '', 'Pharmacy')
  })
})
