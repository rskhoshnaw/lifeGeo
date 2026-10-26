import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { TargetsPage } from './TargetsPage.tsx'
import { targetLines, targetsCsv } from '../components/targetReport.ts'
import type { AdminApi, Me, Product, TargetRow, UserSummary } from '../api/admin.ts'

const row = (user: string, email: string, code: string, target: number | null, actual: number, unit = 'box'): TargetRow => ({
  userId: user, userEmail: email, productId: `p-${code}`, productCode: code, productName: `Product ${code}`, unit, target, actual,
})
const rows = [row('u1', 'rep1@t1', 'A', 100, 80), row('u1', 'rep1@t1', 'B', 50, 60), row('u1', 'rep1@t1', 'C', null, 7), row('u2', 'rep2@t1', 'A', 100, 20)]

describe('target report (plan step 5.5)', () => {
  it('adds the products of a visitor, counting the sales without a target apart', () => {
    const [rep1, rep2] = targetLines(rows, 'visitor')
    expect(rep1).toMatchObject({ label: 'rep1@t1', target: 150, actual: 140, untargeted: 7, percent: 93, unit: 'box' })
    expect(rep2).toMatchObject({ label: 'rep2@t1', target: 100, actual: 20, percent: 20 })
  })

  it('adds the visitors of a product and gives no percent without a target', () => {
    const byProduct = targetLines(rows, 'product')
    expect(byProduct.find((l) => l.label.startsWith('A'))).toMatchObject({ target: 200, actual: 100, percent: 50 })
    expect(byProduct.find((l) => l.label.startsWith('C'))).toMatchObject({ target: 0, actual: 0, untargeted: 7, percent: null })
  })

  it('has no unit when the sums mix units, and exports every line', () => {
    const mixed = targetLines([row('u1', 'rep1@t1', 'A', 10, 5, 'box'), row('u1', 'rep1@t1', 'B', 10, 5, 'vial')], 'visitor')
    expect(mixed[0]!.unit).toBeNull()
    expect(targetsCsv(targetLines(rows, 'detail'), 'Visitor and product')).toContain('rep1@t1 · A · Product A,box,100,80,0,80')
  })
})

describe('targets page', () => {
  const me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['targets.read', 'targets.manage'] } as Me
  const users = [{ id: 'u1', email: 'rep1@t1', isActive: true }] as UserSummary[]
  const products = [{ id: 'p-A', code: 'A', name: 'Product A', unit: 'box' }] as Product[]

  it('shows the report and sets and clears a target of the chosen month', async () => {
    const setTarget = vi.fn().mockResolvedValue(undefined)
    const admin = { targets: vi.fn().mockResolvedValue(rows), users: vi.fn().mockResolvedValue(users), products: vi.fn().mockResolvedValue(products), setTarget } as unknown as AdminApi
    render(<TargetsPage admin={admin} me={me} />)
    expect(await screen.findByRole('cell', { name: 'rep1@t1' })).toBeInTheDocument()
    expect(screen.getByText('93%')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Visitor'), 'u1')
    await userEvent.selectOptions(screen.getByLabelText('Product'), 'p-A')
    await userEvent.type(screen.getByLabelText('Target (units)'), '120')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(setTarget).toHaveBeenCalledWith('u1', 'p-A', expect.stringMatching(/^\d{4}-\d{2}-01$/), 120))

    await userEvent.clear(screen.getByLabelText('Target (units)'))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(setTarget).toHaveBeenLastCalledWith('u1', 'p-A', expect.any(String), null))
  })

  it('hides the form from a reader without targets.manage', async () => {
    const admin = { targets: vi.fn().mockResolvedValue(rows) } as unknown as AdminApi
    render(<TargetsPage admin={admin} me={{ ...me, permissions: ['targets.read'] }} />)
    expect(await screen.findByRole('cell', { name: 'rep1@t1' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Visitor')).not.toBeInTheDocument()
  })
})
