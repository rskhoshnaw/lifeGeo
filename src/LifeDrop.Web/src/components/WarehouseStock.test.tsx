import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, WarehouseStockRow } from '../api/admin.ts'
import { WarehouseStock } from './WarehouseStock.tsx'

const row = (over: Partial<WarehouseStockRow>): WarehouseStockRow => ({
  warehouseId: 'w1', warehouseCode: 'W1', warehouseName: 'Erbil warehouse', cityId: 'c1', cityName: 'Erbil',
  productId: 'a', productCode: 'A', productName: 'Cough syrup', unit: 'box', quantity: 0, counted: false, ...over,
})

function admin(rows: WarehouseStockRow[]) {
  const saved: [string, string, number][] = []
  const fake = {
    warehouseStock: async () => rows,
    setWarehouseStock: async (w: string, p: string, q: number) => {
      saved.push([w, p, q])
    },
  } as unknown as AdminApi
  return { fake, saved }
}

const rows = [
  row({ quantity: 10, counted: true }),
  row({ productId: 'b', productCode: 'B', productName: 'Vitamin C', quantity: 0, counted: true }),
  row({ warehouseId: 'w2', warehouseCode: 'W2', warehouseName: 'Basra warehouse', cityId: 'c2', cityName: 'Basra', quantity: 4, counted: true }),
  row({ warehouseId: 'w2', warehouseCode: 'W2', warehouseName: 'Basra warehouse', cityId: 'c2', cityName: 'Basra', productId: 'b', productCode: 'B', productName: 'Vitamin C' }),
]

describe('WarehouseStock', () => {
  it('shows the figures of every warehouse and the total of every product', async () => {
    render(<WarehouseStock admin={admin(rows).fake} canEdit={false} />)
    const erbil = (await screen.findByText('W1 · Erbil warehouse')).closest('tr')!
    expect(within(erbil).getByText('2 / 2')).toBeInTheDocument()   // both products counted
    expect(within(erbil).getAllByText('10')).toHaveLength(1)      // units in stock
    expect(within(erbil).getByText('1')).toBeInTheDocument()      // one product out of stock
    const basra = screen.getByText('W2 · Basra warehouse').closest('tr')!
    expect(within(basra).getByText('1 / 2')).toBeInTheDocument()
    const cough = screen.getByText('A · Cough syrup', { selector: 'td' }).closest('tr')!
    expect(within(cough).getByText('14 box')).toBeInTheDocument()   // 10 in Erbil + 4 in Basra
    expect(within(cough).getByText('Erbil: 10 · Basra: 4')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Save/ })).not.toBeInTheDocument()   // a reader cannot change anything
  })

  it('counts and saves the quantity of one product in one warehouse', async () => {
    const { fake, saved } = admin(rows)
    const user = userEvent.setup()
    render(<WarehouseStock admin={fake} canEdit />)
    await user.click(await screen.findByRole('button', { name: 'Stock of Erbil warehouse' }))
    const box = screen.getByRole('spinbutton', { name: 'Quantity of Vitamin C' })
    expect(screen.getByRole('button', { name: 'Save Vitamin C' })).toBeDisabled()   // nothing changed yet
    await user.clear(box)
    await user.type(box, '7.5')
    await user.click(screen.getByRole('button', { name: 'Save Vitamin C' }))
    expect(saved).toEqual([['w1', 'b', 7.5]])
  })

  it('warns about low stock and products that expire within 90 days', async () => {
    const soon = new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10)
    const later = new Date(Date.now() + 400 * 86_400_000).toISOString().slice(0, 10)
    const warn = [
      row({ quantity: 2, counted: true, minStock: 5, expiryDate: soon }),
      row({ productId: 'b', productCode: 'B', productName: 'Vitamin C', quantity: 2, counted: true, minStock: 5, expiryDate: later }),
      row({ productId: 'c', productCode: 'C', productName: 'Plenty', quantity: 50, counted: true, minStock: 5 }),
      row({ productId: 'd', productCode: 'D', productName: 'Never counted', quantity: 0, counted: false, minStock: 5 }),
    ]
    render(<WarehouseStock admin={admin(warn).fake} canEdit={false} />)
    const low = (await screen.findByText('Low stock')).closest('div')!
    expect(within(low).getByText(/Cough syrup — 2 box/)).toBeInTheDocument()
    expect(within(low).getByText(/Vitamin C — 2 box/)).toBeInTheDocument()
    expect(within(low).queryByText(/Plenty/)).not.toBeInTheDocument()          // above the limit
    expect(within(low).queryByText(/Never counted/)).not.toBeInTheDocument()   // not counted: nothing to warn about
    const expiring = screen.getByText('Expires within 90 days').closest('div')!
    expect(within(expiring).getByText(new RegExp(`Cough syrup — ${soon}`))).toBeInTheDocument()
    expect(within(expiring).queryByText(/Vitamin C/)).not.toBeInTheDocument()
  })
})
