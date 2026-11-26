import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, Customer, Warehouse } from '../api/admin.ts'
import { Warehouses } from './OrdersPage.tsx'

describe('warehouses and wholesalers (step 5.59)', () => {
  it('makes a wholesaler warehouse from a customer of type Wholesaler and shows whose it is', async () => {
    const warehouses = vi.fn().mockResolvedValue([{ id: 'w1', code: 'WE', name: 'Karwan', status: 'active', kind: 'wholesaler', customerId: 'c1', customerName: 'Karwan Pharma' }] as Warehouse[])
    const customers = vi.fn().mockResolvedValue([{ id: 'c1', code: 'W1', name: 'Karwan Pharma', type: 'Wholesaler' }, { id: 'c2', code: 'W2', name: 'Azad Pharma', type: 'Wholesaler' }] as Customer[])
    const createWarehouse = vi.fn().mockResolvedValue({ id: 'w2' })
    const admin = { warehouses, customers, createWarehouse, places: async () => [] } as unknown as AdminApi
    const user = userEvent.setup()
    render(<Warehouses admin={admin} />)
    expect(await screen.findByText(/Karwan Pharma\]/)).toBeInTheDocument()   // the warehouse list names the customer
    expect(screen.queryByRole('combobox', { name: 'Wholesaler (customer)' })).toBeNull()   // only for the wholesaler kind
    await user.selectOptions(screen.getByRole('combobox', { name: 'Warehouse type' }), 'wholesaler')
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Wholesaler (customer)' }), 'c2')
    expect(screen.getByRole('textbox', { name: 'Name' })).toHaveValue('Azad Pharma')   // the name is taken from the customer
    await user.type(screen.getByRole('textbox', { name: 'Code' }), 'WA')
    await user.click(screen.getByRole('button', { name: 'Add warehouse' }))
    expect(createWarehouse).toHaveBeenCalledWith('WA', 'Azad Pharma', 'wholesaler', expect.objectContaining({ customerId: 'c2' }))
  })

  it('links an old wholesaler warehouse to its customer', async () => {
    const warehouses = vi.fn().mockResolvedValue([{ id: 'w9', code: 'OLD', name: 'Old wholesaler', status: 'active', kind: 'wholesaler', cityId: 'c1' }] as Warehouse[])
    const customers = vi.fn().mockResolvedValue([{ id: 'c2', code: 'W2', name: 'Azad Pharma', type: 'Wholesaler' }] as Customer[])
    const updateWarehouse = vi.fn().mockResolvedValue(undefined)
    render(<Warehouses admin={{ warehouses, customers, updateWarehouse, places: async () => [] } as unknown as AdminApi} />)
    await userEvent.selectOptions(await screen.findByRole('combobox', { name: 'Link Old wholesaler to a wholesaler customer' }), 'c2')
    expect(updateWarehouse).toHaveBeenCalledWith('w9', 'Old wholesaler', 'active', expect.objectContaining({ cityId: 'c1', customerId: 'c2' }))
  })
})
