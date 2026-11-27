import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, Customer, Warehouse } from '../api/admin.ts'
import { NewOrderForm } from './OrdersPage.tsx'

describe('the order form offers the warehouse of the customer\'s city (step 5.67)', () => {
  it('chooses it when a customer is picked, and asks the server again for another customer', async () => {
    const customers = vi.fn().mockResolvedValue([
      { id: 'c1', code: 'C1', name: 'In the north', approvalStatus: 'approved' },
      { id: 'c2', code: 'C2', name: 'In the south', approvalStatus: 'approved' },
    ] as Customer[])
    const north = { id: 'w1', code: 'W1', name: 'North', status: 'active', kind: 'company', isDefault: true } as Warehouse
    const south = { id: 'w2', code: 'W2', name: 'South', status: 'active', kind: 'company', isDefault: true } as Warehouse
    const warehousesForOrder = vi.fn().mockImplementation(async (customer?: string) => (customer === 'c1' ? [north] : customer === 'c2' ? [south] : []))
    const admin = { customers, warehousesForOrder, customerPrices: async () => [] } as unknown as AdminApi
    const user = userEvent.setup()
    render(<NewOrderForm admin={admin} onPlaced={() => {}} />)
    const warehouse = await screen.findByRole('combobox', { name: 'Warehouse' })
    expect(warehouse).toHaveValue('')   // no customer yet: nothing to choose
    await user.selectOptions(screen.getByRole('combobox', { name: 'Customer' }), 'c1')
    await vi.waitFor(() => expect(warehouse).toHaveValue('w1'))
    expect(warehousesForOrder).toHaveBeenCalledWith('c1')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Customer' }), 'c2')
    await vi.waitFor(() => expect(warehouse).toHaveValue('w2'))
  })
})
