import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RecordForm } from './PaymentsPage.tsx'
import { ReturnPhotos } from './ReturnsPage.tsx'
import type { AdminApi, CustomerReturn } from '../api/admin.ts'

const customer = { id: 'c1', code: 'C1', name: 'Shifa' }

function api(extra: Record<string, unknown> = {}) {
  return { customers: vi.fn().mockResolvedValue([]), orders: vi.fn().mockResolvedValue([{ id: 'o1', number: 'O-1', customerId: 'c1', status: 'delivered', currency: 'IQD', total: 5000 }]), ...extra } as unknown as AdminApi
}

describe('refund: money paid back to the customer (step 5.55)', () => {
  it('is offered only to those who may manage payments and is sent as an outgoing payment without an order', async () => {
    const recordPayment = vi.fn().mockResolvedValue({ id: 'p1', number: 'P-000009', status: 'confirmed', replayed: false })
    const user = userEvent.setup()
    const { rerender } = render(<RecordForm admin={api({ recordPayment })} fixedCustomer={customer} onSaved={() => {}} />)
    expect(screen.queryByRole('checkbox', { name: /Refund/ })).toBeNull()   // a visitor cannot pay money back
    rerender(<RecordForm admin={api({ recordPayment })} fixedCustomer={customer} canRefund onSaved={() => {}} />)
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Order (optional)' }), 'o1')
    await user.click(screen.getByRole('checkbox', { name: /Refund/ }))
    expect(screen.getByRole('combobox', { name: 'Order (optional)' })).toBeDisabled()   // never against an order
    await user.type(screen.getByRole('textbox', { name: 'Amount' }), '400')
    await user.click(screen.getByRole('button', { name: 'Save payment' }))
    expect(recordPayment).toHaveBeenCalledTimes(1)
    expect(recordPayment.mock.calls[0]![0]).toMatchObject({ customerId: 'c1', orderId: null, amount: 400, direction: 'out' })
  })

  it('sends an ordinary payment as it always was (no direction)', async () => {
    const recordPayment = vi.fn().mockResolvedValue({ id: 'p1', number: 'P-000010', status: 'confirmed', replayed: false })
    const user = userEvent.setup()
    render(<RecordForm admin={api({ recordPayment })} fixedCustomer={customer} canRefund onSaved={() => {}} />)
    await user.type(await screen.findByRole('textbox', { name: 'Amount' }), '1000')
    await user.click(screen.getByRole('button', { name: 'Save payment' }))
    expect(recordPayment.mock.calls[0]![0]).not.toHaveProperty('direction')
  })
})

describe('pictures of the goods on a return', () => {
  it('are fetched with the sign-in and shown', async () => {
    const returnPhotoUrl = vi.fn(async (_id: string, photoId: string) => `blob:${photoId}`)
    const r = { id: 'r1', photoIds: ['a', 'b'] } as unknown as CustomerReturn
    render(<ReturnPhotos admin={{ returnPhotoUrl } as unknown as AdminApi} r={r} />)
    expect(await screen.findAllByRole('img')).toHaveLength(2)
    expect(returnPhotoUrl).toHaveBeenCalledWith('r1', 'a')
  })

  it('show nothing when the return has no picture', () => {
    const { container } = render(<ReturnPhotos admin={{ returnPhotoUrl: vi.fn() } as unknown as AdminApi} r={{ id: 'r1', photoIds: [] } as unknown as CustomerReturn} />)
    expect(container).toBeEmptyDOMElement()
  })
})
