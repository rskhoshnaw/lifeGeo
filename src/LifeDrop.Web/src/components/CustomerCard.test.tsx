import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CustomerCard } from './CustomerCard.tsx'
import type { AdminApi, Customer, MapCustomer } from '../api/admin.ts'

// WebGL is not available in jsdom; the map is replaced by its position and a button that "clicks" the map at a fixed spot.
vi.mock('./CustomerLocationMap.tsx', () => ({
  CustomerLocationMap: ({ position, canMove, onPick }: { position: { latitude: number; longitude: number } | null; canMove: boolean; onPick: (a: number, b: number) => void }) => (
    <div>
      <p aria-label="Marker">{position ? `${position.latitude},${position.longitude}` : 'no marker'}</p>
      {canMove && <button onClick={() => onPick(36.2, 44.1)}>click map</button>}
    </div>
  ),
}))

const customer = {
  id: 'c1', type: 'Pharmacy', code: 'C-1', name: 'Shifa Pharmacy', status: 'Active', createdAt: '2026-10-01T00:00:00Z', regionId: null, approvalStatus: 'approved',
  phone: '0750 000 0000', address: 'Near the bridge', placeId: null, placePath: [{ id: 'p1', kind: 'city', name: 'Erbil' }], priority: 'normal',
} as unknown as Customer

function fake(map: MapCustomer[]) {
  const setCustomerLocation = vi.fn(async () => {})
  const admin = { mapCustomers: vi.fn(async () => map), setCustomerLocation } as unknown as AdminApi
  return { admin, setCustomerLocation }
}
const located: MapCustomer = { id: 'c1', code: 'C-1', name: 'Shifa Pharmacy', type: 'Pharmacy', latitude: 36.19, longitude: 44.01, geofenceRadiusMeters: 20 }
const props = { customer, canEdit: true, onEdit: () => {}, onProfile: () => {}, onHours: () => {}, onHistory: () => {}, onClose: () => {} }

describe('customer card', () => {
  it('shows the details and the saved position', async () => {
    const { admin } = fake([located])
    render(<CustomerCard admin={admin} canMove={false} {...props} />)
    expect(screen.getByText('Near the bridge')).toBeInTheDocument()
    expect(screen.getByText('Erbil')).toBeInTheDocument()
    expect(await screen.findByText('36.19,44.01')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'click map' })).not.toBeInTheDocument()   // cannot move: no choosing on the map
  })

  it('says so when the customer has no position', async () => {
    const { admin } = fake([])
    render(<CustomerCard admin={admin} canMove={false} {...props} />)
    expect(await screen.findByText('This customer has no position yet.')).toBeInTheDocument()
  })

  it('saves a position chosen on the map, only after Save position', async () => {
    const { admin, setCustomerLocation } = fake([located])
    render(<CustomerCard admin={admin} canMove {...props} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'click map' }))
    expect(screen.getByLabelText('Marker')).toHaveTextContent('36.2,44.1')
    expect(setCustomerLocation).not.toHaveBeenCalled()
    await user.click(screen.getByRole('button', { name: 'Save position' }))
    await waitFor(() => expect(setCustomerLocation).toHaveBeenCalledWith('c1', 36.2, 44.1))
    expect(await screen.findByText('Position saved.')).toBeInTheDocument()
  })

  it('the buttons open the editors', async () => {
    const onEdit = vi.fn()
    const { admin } = fake([located])
    render(<CustomerCard admin={admin} canMove={false} {...props} onEdit={onEdit} />)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Edit' }))
    expect(onEdit).toHaveBeenCalled()
  })
})
