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

  it('has Save and new product: the product is created and the form opens empty for the next one', async () => {
    const manager = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['catalog.read', 'catalog.manage'] } as Me
    const createProduct = vi.fn().mockResolvedValue({})
    const api = { products: vi.fn().mockResolvedValue([]), productPhotoUrl: (k: string) => k, createProduct } as unknown as AdminApi
    const user = userEvent.setup()
    render(<CatalogPage admin={api} me={manager} />)
    await user.click(await screen.findByRole('button', { name: 'New product' }))
    await user.type(screen.getByRole('textbox', { name: 'Code' }), 'P-1')
    await user.type(screen.getByRole('textbox', { name: 'Name' }), 'First product')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Unit' }), 'box')
    await user.type(screen.getByRole('spinbutton', { name: 'Base price' }), '1500')
    await user.click(screen.getByRole('button', { name: 'Save and new product' }))
    await vi.waitFor(() => expect(createProduct).toHaveBeenCalledTimes(1))
    expect(createProduct.mock.calls[0]![0]).toMatchObject({ code: 'P-1', name: 'First product', unit: 'box', basePrice: 1500 })
    expect(await screen.findByRole('textbox', { name: 'Code' })).toHaveValue('')   // an empty form is there again
    expect(screen.getByRole('textbox', { name: 'Name' })).toHaveValue('')
  })
})
