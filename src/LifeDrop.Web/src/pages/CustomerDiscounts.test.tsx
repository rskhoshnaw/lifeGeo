import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, Product } from '../api/admin.ts'
import { CustomerDiscounts } from './CatalogPage.tsx'

describe('discount amounts are money in the currency of the product (step 5.69)', () => {
  it('reads 10.440 as 10440 dinars, refuses a fraction, and keeps a percentage a plain number', async () => {
    const products = vi.fn().mockResolvedValue([{ id: 'p1', code: 'P1', name: 'Dinar product', currency: 'IQD', status: 'active' }] as Product[])
    const createDiscount = vi.fn().mockResolvedValue({})
    const admin = { discounts: async () => [], products, createDiscount } as unknown as AdminApi
    const user = userEvent.setup()
    render(<CustomerDiscounts admin={admin} customerId="c1" canManage onChanged={() => {}} />)
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Product' }), 'p1')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Kind' }), 'amount')
    const value = screen.getByRole('textbox', { name: 'Value' })
    await user.type(value, '100.5')
    expect(await screen.findByRole('alert')).toHaveTextContent('too many decimals')
    expect(screen.getByRole('button', { name: 'Add discount' })).toBeDisabled()
    await user.clear(value)
    await user.type(value, '10.440')
    await user.click(screen.getByRole('button', { name: 'Add discount' }))
    await vi.waitFor(() => expect(createDiscount).toHaveBeenCalledTimes(1))
    expect(createDiscount.mock.calls[0]![1]).toMatchObject({ productId: 'p1', kind: 'amount', value: 10440 })
    // a percentage is not money: 2.5 stays 2.5
    await user.selectOptions(screen.getByRole('combobox', { name: 'Kind' }), 'percent')
    await user.type(screen.getByRole('textbox', { name: 'Value' }), '2.5')
    await user.click(screen.getByRole('button', { name: 'Add discount' }))
    await vi.waitFor(() => expect(createDiscount).toHaveBeenCalledTimes(2))
    expect(createDiscount.mock.calls[1]![1]).toMatchObject({ kind: 'percent', value: 2.5 })
  })
})
