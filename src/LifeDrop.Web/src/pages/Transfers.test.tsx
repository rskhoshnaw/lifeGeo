import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, Transfer, WarehouseStockRow } from '../api/admin.ts'
import { TransfersPage } from './TransfersPage.tsx'

const sent: Transfer = {
  id: 't1', number: 'T-000001', fromWarehouseId: 'w2', fromWarehouseCode: 'WB', fromWarehouseName: 'Basra', toWarehouseId: 'w1', toWarehouseCode: 'WE', toWarehouseName: 'Erbil central',
  status: 'sent', note: null, createdByEmail: 'b@t1', createdAt: '2026-11-27T08:00:00Z', closedByEmail: null, closedAt: null, closeNote: null,
  items: [{ lineNo: 1, productId: 'p1', productCode: 'P1', productName: 'Cough syrup', unit: 'box', bucket: 'damaged', quantity: 2, receivedQuantity: null }],
}
const stockRow = (over: Partial<WarehouseStockRow>): WarehouseStockRow => ({
  warehouseId: 'w1', warehouseCode: 'WE', warehouseName: 'Erbil central', cityId: null, cityName: null, productId: 'p1', productCode: 'P1', productName: 'Cough syrup', unit: 'box', quantity: 10, counted: true, damaged: 1.5, ...over,
})

describe('transfers between warehouses (step 5.60)', () => {
  it('confirms what arrived, line by line, and shows a shortage', async () => {
    const transfers = vi.fn().mockResolvedValue([sent, { ...sent, id: 't2', number: 'T-000002', status: 'received', items: [{ ...sent.items[0]!, receivedQuantity: 1.5 }] }])
    const receiveTransfer = vi.fn().mockResolvedValue(undefined)
    const user = userEvent.setup()
    render(<TransfersPage admin={{ transfers, receiveTransfer } as unknown as AdminApi} />)
    expect(await screen.findByText('T-000002')).toBeInTheDocument()
    expect(screen.getByText(/arrived 1.5/)).toBeInTheDocument()                        // the shortage of a received transfer is shown
    await user.click(screen.getByRole('button', { name: 'Receive T-000001' }))
    const box = screen.getByRole('spinbutton', { name: 'Arrived quantity of Cough syrup' })
    expect(box).toHaveValue(2)                                                           // everything arrived unless said otherwise
    await user.clear(box)
    await user.type(box, '3')
    expect(screen.getByRole('button', { name: 'Confirm receipt' })).toBeDisabled()     // more than was sent
    await user.clear(box)
    await user.type(box, '1.5')
    await user.click(screen.getByRole('button', { name: 'Confirm receipt' }))
    expect(receiveTransfer).toHaveBeenCalledWith('t1', [{ lineNo: 1, receivedQuantity: 1.5 }], null)
  })

  it('sends sellable and damaged goods from a warehouse to another one, never more than it holds', async () => {
    const transferWarehouses = vi.fn().mockResolvedValue([{ id: 'w1', code: 'WE', name: 'Erbil central', cityName: null }, { id: 'w2', code: 'WB', name: 'Basra', cityName: null }])
    const warehouseStock = vi.fn().mockResolvedValue([stockRow({}), stockRow({ warehouseId: 'w2', warehouseCode: 'WB', warehouseName: 'Basra', quantity: 0, damaged: 3 })])
    const createTransfer = vi.fn().mockResolvedValue({ id: 't9' })
    const user = userEvent.setup()
    render(<TransfersPage admin={{ transfers: async () => [], transferWarehouses, warehouseStock, createTransfer } as unknown as AdminApi} />)
    await user.click(await screen.findByRole('button', { name: 'New transfer' }))
    const from = await screen.findByRole('combobox', { name: 'From warehouse' })
    await user.selectOptions(from, 'w1')
    const to = screen.getByRole('combobox', { name: 'To warehouse' })
    expect(within(to).queryByRole('option', { name: /Erbil central/ })).toBeNull()     // not to itself
    await user.selectOptions(to, 'w2')
    const sellable = screen.getByRole('spinbutton', { name: 'Quantity of Cough syrup to send' })
    await user.type(sellable, '11')
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()                // 10 are in stock
    await user.clear(sellable)
    await user.type(sellable, '4')
    await user.type(screen.getByRole('spinbutton', { name: 'Damaged quantity of Cough syrup to send' }), '1')
    await user.click(screen.getByRole('button', { name: 'Send' }))
    expect(createTransfer).toHaveBeenCalledWith({
      fromWarehouseId: 'w1', toWarehouseId: 'w2', note: null,
      items: [{ productId: 'p1', quantity: 4, bucket: 'sellable' }, { productId: 'p1', quantity: 1, bucket: 'damaged' }],
    })
  })
})
