import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import type { Customer, Device, Me, UserSummary } from '../api/admin.ts'

type Call = { method: string; path: string; body: unknown }
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

// Fake API: GET routes return the given data; POST routes return 204 unless overridden.
function server(me: Me, data: Record<string, unknown>, posts: Record<string, Response> = {}) {
  const calls: Call[] = []
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace('https://api.test', '')
    const method = init?.method ?? 'GET'
    calls.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined })
    if (path === '/auth/refresh') return json({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' })
    if (path === '/me') return json(me)
    if (method === 'POST') return posts[path] ?? new Response(null, { status: 204 })
    return path in data ? json(data[path]) : json({ code: 'NOT_FOUND' }, 404)
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return { api: new ApiClient('https://api.test', fetcher, sessionStorage), calls }
}

const manager: Me = { id: 'm', email: 'manager@t1', roles: ['SalesManager'], permissions: ['customers.approve', 'customers.read', 'device.approve', 'sessions.kill', 'user.manageroles'] }
const rep: UserSummary = { id: 'u1', email: 'rep@t1', roles: ['SalesRep'], teamId: 't', teamName: 'Team A', regionId: 'r', activeSessions: 2 }
const device = (id: string, status: Device['status']): Device => ({ id, userId: 'u1', userEmail: 'rep@t1', label: `Phone ${id}`, platform: 'android', appVersion: '1.0', status, registeredAt: '2026-10-04T08:00:00Z' })
const customer: Customer = { id: 'c1', type: 'pharmacy', code: 'P-1', name: 'Shifa', status: 'located', createdAt: '2026-10-04T08:00:00Z', regionId: 'r', approvalStatus: 'pending' }

const posts = (calls: Call[]) => calls.filter((c) => c.method === 'POST' && c.path !== '/auth/refresh')

afterEach(() => vi.restoreAllMocks())

describe('panel management screens', () => {
  it('shows only the tabs the user has permissions for', async () => {
    const { api } = server({ id: 'x', email: 'finance@t1', roles: ['FinanceManager'], permissions: ['audit.read', 'customers.read'] }, {})
    render(<App api={api} />)
    expect(await screen.findByRole('button', { name: 'Customers' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Users' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Devices' })).not.toBeInTheDocument()
  })

  it('approves and blocks devices and shows a server refusal', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const { api, calls } = server(manager, { '/devices?status=pending': [device('d1', 'pending')] }, { '/devices/d1/block': json({ code: 'FORBIDDEN' }, 403) })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Devices' }))
    await user.click(await screen.findByRole('button', { name: 'Approve' }))
    await user.click(screen.getByRole('button', { name: 'Block' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('You do not have permission for this action.')
    expect(posts(calls).map((c) => c.path)).toEqual(['/devices/d1/approve', '/devices/d1/block'])
  })

  it('ends sessions only after confirmation and assigns a role', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    const { api, calls } = server(manager, { '/users': [rep], '/roles': [{ id: 'r1', name: 'SalesRep', dataScope: 'own' }, { id: 'r2', name: 'Supervisor', dataScope: 'region' }] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Users' }))
    const row = (await screen.findByText('rep@t1')).closest('tr')!
    await user.click(within(row).getByRole('button', { name: 'End sessions' }))
    expect(posts(calls)).toEqual([])
    await user.click(within(row).getByRole('button', { name: 'End sessions' }))
    expect(confirm).toHaveBeenCalledTimes(2)
    // The rep already has SalesRep, so only Supervisor is offered
    const select = within(row).getByRole('combobox', { name: 'Role for rep@t1' })
    expect(within(select).queryByRole('option', { name: 'SalesRep' })).not.toBeInTheDocument()
    await user.selectOptions(select, 'r2')
    await user.click(within(row).getByRole('button', { name: 'Assign' }))
    expect(posts(calls).map((c) => c.path)).toEqual(['/users/u1/sessions/revoke', '/users/u1/roles/r2'])
  })

  it('hides actions the user lacks', async () => {
    const { api, calls } = server({ ...manager, permissions: ['sessions.kill'] }, { '/users': [rep] })
    render(<App api={api} />)
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Users' }))
    await screen.findByText('rep@t1')
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    expect(calls.some((c) => c.path === '/roles')).toBe(false)
  })

  it('needs a note to reject a pending customer and approves with the note', async () => {
    const { api, calls } = server(manager, { '/customer-approvals': [customer], '/customers?limit=100': [{ ...customer, id: 'c2', code: 'P-2', approvalStatus: 'approved' }] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    expect(await screen.findByText('P-2')).toBeInTheDocument()
    await user.click(await screen.findByRole('button', { name: 'Reject' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Write a reason before rejecting.')
    expect(posts(calls)).toEqual([])
    await user.type(screen.getByLabelText('Note for P-1'), 'Checked')
    await user.click(screen.getByRole('button', { name: 'Approve' }))
    expect(posts(calls)).toEqual([{ method: 'POST', path: '/customers/c1/approval', body: { decision: 'approve', note: 'Checked' } }])
  })
})
