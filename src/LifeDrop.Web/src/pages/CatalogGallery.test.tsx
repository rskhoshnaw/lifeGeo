import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CatalogPage } from './CatalogPage.tsx'
import type { AdminApi, Me, Product } from '../api/admin.ts'

const product = (code: string, extra: Partial<Product> = {}): Product => ({
  id: `p-${code}`, code, name: `Name ${code}`, nameCkb: null, nameAr: null, nameEn: null, unit: 'box', currency: 'IQD', basePrice: 2500, status: 'active', ...extra,
})

describe('product gallery', () => {
  const me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['catalog.read'] } as Me
  const items = [
    product('A', { scientificName: 'Acetaminophen', expiryDate: '2020-01-01', specialty: 'pediatrics', manufacturer: 'Pioneer', stockQty: 5, inStock: true, notes: 'Keep cool' }),
    product('B', { specialty: 'ent', inStock: false, stockQty: 0 }),
  ]
  const admin = { products: vi.fn().mockResolvedValue(items), productPhotoUrl: (k: string) => `/p/${k}` } as unknown as AdminApi

  it('shows cards with name, scientific name, expiry and price, filters them and opens all details on a click', async () => {
    render(<CatalogPage admin={admin} me={me} />)
    const card = await screen.findByRole('button', { name: 'Name A' })
    expect(card).toHaveTextContent('Acetaminophen')
    expect(card).toHaveTextContent('2020-01-01')
    expect(card).toHaveTextContent('2,500 IQD')
    await userEvent.selectOptions(screen.getByLabelText('Specialty'), 'ent')
    expect(screen.queryByRole('button', { name: 'Name A' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Name B' })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('Specialty'), '')
    await userEvent.click(screen.getByRole('button', { name: 'Name A' }))
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveTextContent('Keep cool')
    expect(dialog).toHaveTextContent('Pioneer')
    expect(dialog).toHaveTextContent('Expired')
  })
})
