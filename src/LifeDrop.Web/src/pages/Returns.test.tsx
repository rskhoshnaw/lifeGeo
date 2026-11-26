import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ReturnsPage, defaultDestination } from './ReturnsPage.tsx'
import type { AdminApi, CustomerReturn, Me } from '../api/admin.ts'

const manager = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['returns.read', 'returns.create', 'returns.approve'] } as Me
const ret = (extra: Partial<CustomerReturn> = {}): CustomerReturn => ({
  id: 'r1', number: 'R-000001', customerId: 'c1', customerCode: 'C1', customerName: 'Shifa', orderId: null, orderNumber: null, warehouseId: 'w1', warehouseName: 'Main', status: 'requested', currency: 'IQD',
  total: 2500, note: null, createdBy: 'rep', createdByEmail: 'rep@t1', decisionNote: null, decidedByEmail: null, decidedAt: null, createdAt: '2026-11-13T08:00:00Z',
  items: [
    { lineNo: 1, productId: 'p1', productCode: 'P1', productName: 'Paracetamol', unit: 'box', quantity: 2, unitPrice: 1000, lineTotal: 2000, reason: 'damaged', destination: 'damaged', stockApplied: null },
    { lineNo: 2, productId: 'p2', productCode: 'P2', productName: 'Vitamin C', unit: 'box', quantity: 1, unitPrice: 500, lineTotal: 500, reason: 'customer_request', destination: 'warehouse', stockApplied: null },
  ], ...extra,
})

function admin(overrides: Record<string, unknown> = {}) {
  return {
    returns: vi.fn().mockResolvedValue([ret()]),
    damagedGoods: vi.fn().mockResolvedValue([{ productId: 'p1', productCode: 'P1', productName: 'Paracetamol', unit: 'box', reason: 'damaged', quantity: 2, value: 2000, currency: 'IQD' }]),
    customers: vi.fn().mockResolvedValue([{ id: 'c1', code: 'C1', name: 'Shifa', approvalStatus: 'approved' }]),
    orders: vi.fn().mockResolvedValue([]),
    warehouses: vi.fn().mockResolvedValue([{ id: 'w1', code: 'W1', name: 'Main', status: 'active', kind: 'company' }]),
    products: vi.fn().mockResolvedValue([{ id: 'p1', code: 'P1', name: 'Paracetamol' }, { id: 'p2', code: 'P2', name: 'Vitamin C' }]),
    ...overrides,
  } as unknown as AdminApi
}

describe('customer returns page', () => {
  it('sends damaged and expired goods apart by default and everything else back to the warehouse', () => {
    expect(defaultDestination('damaged')).toBe('damaged')
    expect(defaultDestination('expired')).toBe('damaged')
    expect(defaultDestination('wrong_item')).toBe('warehouse')
    expect(defaultDestination('customer_request')).toBe('warehouse')
    expect(defaultDestination('other')).toBe('warehouse')
  })

  it('lists the returns with their goods and lets the sales manager approve and reject', async () => {
    const approveReturn = vi.fn().mockResolvedValue(undefined)
    const rejectReturn = vi.fn().mockResolvedValue(undefined)
    const api = admin({ approveReturn, rejectReturn })
    const user = userEvent.setup()
    vi.spyOn(window, 'prompt').mockReturnValueOnce('fine').mockReturnValueOnce('not our goods')
    render(<ReturnsPage admin={api} me={manager} />)
    await user.click(await screen.findByRole('button', { name: /Goods R-000001/ }))
    expect(screen.getByText('Damaged / expired (apart)')).toBeInTheDocument()
    expect(screen.getByText('Back to the warehouse')).toBeInTheDocument()
    expect(screen.getByText('Paracetamol')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Approve R-000001' }))
    expect(approveReturn).toHaveBeenCalledWith('r1', 'fine')
    await user.click(screen.getByRole('button', { name: 'Reject R-000001' }))
    expect(rejectReturn).toHaveBeenCalledWith('r1', 'not our goods')
    expect(screen.getByText('Damaged and expired goods')).toBeInTheDocument()   // the period report is under the list
  })

  it('shows no decision buttons without returns.approve, and the author can cancel while it is requested', async () => {
    const reader = { ...manager, id: 'rep', permissions: ['returns.create', 'returns.read'] } as Me
    const cancelReturn = vi.fn().mockResolvedValue(undefined)
    vi.spyOn(window, 'prompt').mockReturnValueOnce('mistake')
    const user = userEvent.setup()
    render(<ReturnsPage admin={admin({ cancelReturn })} me={reader} />)
    await screen.findByRole('button', { name: /Goods R-000001/ })
    expect(screen.queryByRole('button', { name: 'Approve R-000001' })).toBeNull()
    await user.click(screen.getByRole('button', { name: 'Cancel return R-000001' }))
    expect(cancelReturn).toHaveBeenCalledWith('r1', 'mistake')
  })

  it('makes a return: lines take their destination from the reason, a warehouse is needed for goods that go back, and the server gets the checked lines', async () => {
    const createReturn = vi.fn().mockResolvedValue({ id: 'r9', number: 'R-000009', currency: 'IQD', total: 1500, replayed: false })
    const user = userEvent.setup()
    render(<ReturnsPage admin={admin({ createReturn })} me={manager} />)
    await user.click(await screen.findByRole('button', { name: 'New return' }))
    const form = screen.getByRole('heading', { name: 'New return' }).closest('div')!
    await user.selectOptions(within(form).getByRole('combobox', { name: 'Customer' }), 'c1')
    // damaged: destination follows the reason
    await user.selectOptions(within(form).getByRole('combobox', { name: 'Product' }), 'p1')
    await user.type(within(form).getByRole('textbox', { name: 'Quantity' }), '2')
    expect(within(form).getByRole('combobox', { name: 'Goes to' })).toHaveValue('damaged')
    await user.click(within(form).getByRole('button', { name: 'Add line' }))
    // a customer's request goes back to the warehouse
    await user.selectOptions(within(form).getByRole('combobox', { name: 'Product' }), 'p2')
    await user.type(within(form).getByRole('textbox', { name: 'Quantity' }), '1')
    await user.selectOptions(within(form).getByRole('combobox', { name: 'Reason' }), 'customer_request')
    expect(within(form).getByRole('combobox', { name: 'Goes to' })).toHaveValue('warehouse')
    await user.click(within(form).getByRole('button', { name: 'Add line' }))
    const send = within(form).getByRole('button', { name: 'Send for approval' })
    expect(send).toBeDisabled()   // a line goes back to a warehouse and none is chosen yet
    await user.selectOptions(within(form).getByRole('combobox', { name: 'Warehouse for goods that go back' }), 'w1')
    await user.click(send)
    expect(createReturn).toHaveBeenCalledTimes(1)
    expect(createReturn.mock.calls[0]![0]).toMatchObject({
      customerId: 'c1', orderId: null, warehouseId: 'w1',
      items: [{ productId: 'p1', quantity: 2, reason: 'damaged', destination: 'damaged' }, { productId: 'p2', quantity: 1, reason: 'customer_request', destination: 'warehouse' }],
    })
    expect(await screen.findByRole('status')).toHaveTextContent('Return R-000009 sent for approval.')
  })
})
