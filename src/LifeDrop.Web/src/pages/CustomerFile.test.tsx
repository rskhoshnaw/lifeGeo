import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CustomerFilePage, customerFileHash, customerIdFromHash } from './CustomerFilePage.tsx'
import type { AdminApi, Customer, Me } from '../api/admin.ts'

const id = '11111111-1111-4111-8111-111111111111'
const me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['customers.read'] } as Me
const customer = {
  id, type: 'Pharmacy', code: 'P1', name: 'Shifa Pharmacy', status: 'located', createdAt: '2026-11-01T08:00:00Z', regionId: null, approvalStatus: 'approved', phone: '0750 111 2233', address: 'Salim street',
  placeId: null, placePath: [{ id: 'c', kind: 'city', name: 'Erbil' }], priority: 'normal', ownerEmail: 'rep@t1', notes: 'Pays on Sundays',
} as unknown as Customer

describe('customer file', () => {
  it('reads and writes the address of the file', () => {
    expect(customerFileHash(id)).toBe(`#/customer/${id}`)
    expect(customerIdFromHash(`#/customer/${id}`)).toBe(id)
    expect(customerIdFromHash('#/customer/not-an-id')).toBeNull()
    expect(customerIdFromHash('')).toBeNull()
  })

  it('shows the details, visits, orders with what was not delivered, payments, samples and follow-ups, and says so when one part is refused', async () => {
    const admin = {
      customer: vi.fn().mockResolvedValue(customer),
      customerHistory: vi.fn().mockResolvedValue({
        visits: [{ id: 'v1', date: '2026-11-10', status: 'Completed', visitType: 'pharmacy', userEmail: 'rep@t1', checkInAt: null, checkOutAt: null, outcome: null, note: 'Visit note', reason: null }],
        orders: [{ id: 'o1', number: 'O-1', createdAt: '2026-11-11T08:00:00Z', status: 'delivered', currency: 'IQD', total: 5000, pricesHidden: false, createdByEmail: null, lines: [{ productName: 'Paracetamol', quantity: 10, deliveredQuantity: 7 }] }],
      }),
      customerPayments: vi.fn().mockResolvedValue([{ id: 'p1', number: 'PAY-1', createdAt: '2026-11-12T08:00:00Z', method: 'cash', currency: 'IQD', amount: 3000, status: 'confirmed', receivedByEmail: 'rep@t1' }]),
      customerBalance: vi.fn().mockResolvedValue({ customerId: id, items: [{ currency: 'IQD', delivered: 5000, confirmed: 3000, collected: 0, balance: 2000, returned: 0 }] }),
      customerSamples: vi.fn().mockResolvedValue([]),
      customerFollowUps: vi.fn().mockRejectedValue(new Error('403')),
      returns: vi.fn().mockResolvedValue([{ id: 'r1', number: 'R-000001', customerId: id, createdAt: '2026-11-13T08:00:00Z', status: 'approved', currency: 'IQD', total: 2500, items: [{ lineNo: 1, productId: 'p', productName: 'Paracetamol', unit: 'box', quantity: 2, unitPrice: 1000, lineTotal: 2000, reason: 'damaged', destination: 'damaged', stockApplied: null }] }]),
    } as unknown as AdminApi
    render(<CustomerFilePage admin={admin} customerId={id} me={me} />)
    expect(await screen.findByRole('heading', { name: /Shifa Pharmacy \(P1\)/ })).toBeInTheDocument()
    expect(await screen.findByText('Pays on Sundays')).toBeInTheDocument()
    expect(screen.getByText('Erbil')).toBeInTheDocument()
    expect(await screen.findByText(/Visit note/)).toBeInTheDocument()
    expect(await screen.findByText('Paracetamol × 3')).toBeInTheDocument()   // ordered 10, delivered 7: 3 not delivered
    expect(await screen.findByText('PAY-1')).toBeInTheDocument()
    expect(screen.getByText(/Customer balance 2,000/)).toBeInTheDocument()
    expect(await screen.findByText('You cannot see this part.')).toBeInTheDocument()   // the follow-ups were refused
    expect(await screen.findByText('R-000001')).toBeInTheDocument()   // the returns of the customer, with their goods
    expect(screen.getByText('Damaged / expired (apart)')).toBeInTheDocument()
  })

  it('says when the customer is not found', async () => {
    const admin = { customer: vi.fn().mockRejectedValue(new Error('404')), customerHistory: vi.fn().mockResolvedValue({ visits: [], orders: [] }), customerPayments: vi.fn().mockResolvedValue([]), customerBalance: vi.fn().mockResolvedValue({ customerId: id, items: [] }), customerSamples: vi.fn().mockResolvedValue([]), customerFollowUps: vi.fn().mockResolvedValue([]), returns: vi.fn().mockResolvedValue([]) } as unknown as AdminApi
    render(<CustomerFilePage admin={admin} customerId={id} me={me} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('not found')
  })

  it('has the actions of the customer, only those the reader may do, and the form is for this customer only', async () => {
    const user = userEvent.setup()
    const createFollowUp = vi.fn().mockResolvedValue(undefined)
    const admin = {
      customer: vi.fn().mockResolvedValue(customer),
      customerHistory: vi.fn().mockResolvedValue({ visits: [], orders: [] }),
      customerPayments: vi.fn().mockResolvedValue([]),
      customerBalance: vi.fn().mockResolvedValue({ customerId: id, items: [] }),
      customerSamples: vi.fn().mockResolvedValue([]),
      customerFollowUps: vi.fn().mockResolvedValue([]),
      returns: vi.fn().mockResolvedValue([]),
      customers: vi.fn().mockResolvedValue([]),
      users: vi.fn().mockResolvedValue([]),
      createFollowUp,
    } as unknown as AdminApi
    const reader = { ...me, permissions: ['customers.read', 'followups.create'] } as Me
    render(<CustomerFilePage admin={admin} customerId={id} me={reader} />)
    expect(await screen.findByRole('button', { name: 'New follow-up' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Record payment' })).toBeNull()   // not allowed for this reader
    expect(screen.queryByRole('button', { name: 'New order' })).toBeNull()
    await user.click(screen.getByRole('button', { name: 'New follow-up' }))
    const chooser = screen.getByRole('combobox', { name: 'Customer' })
    expect(chooser).toBeDisabled()
    expect(chooser).toHaveValue(id)
    await user.type(screen.getByRole('textbox', { name: 'Subject' }), 'Call back')
    await user.click(screen.getByRole('button', { name: 'Save follow-up' }))
    await vi.waitFor(() => expect(createFollowUp).toHaveBeenCalledTimes(1))
    expect(createFollowUp.mock.calls[0]![0]).toMatchObject({ customerId: id, subject: 'Call back' })
    expect(await screen.findByRole('status')).toHaveTextContent('Saved.')
  })
})
