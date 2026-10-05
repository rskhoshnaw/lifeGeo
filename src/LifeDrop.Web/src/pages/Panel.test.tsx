import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { iraqToday, type Customer, type Place, type Device, type Me, type PlannedVisit, type UserSummary } from '../api/admin.ts'

type Call = { method: string; path: string; body: unknown }
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

// Fake API: GET routes return the given data; other methods return 204 unless overridden.
function server(me: Me, data: Record<string, unknown>, posts: Record<string, Response> = {}) {
  const calls: Call[] = []
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace('https://api.test', '')
    const method = init?.method ?? 'GET'
    calls.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined })
    if (path === '/auth/refresh') return json({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' })
    if (path === '/me') return json(me)
    if (method !== 'GET') return posts[path] ?? new Response(null, { status: 204 })
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
const customer: Customer = { id: 'c1', type: 'pharmacy', code: 'P-1', name: 'Shifa', status: 'located', createdAt: '2026-10-04T08:00:00Z', regionId: 'r', approvalStatus: 'pending', phone: null, address: null, placeId: null, placePath: null }

const visit = (id: string, status: string): PlannedVisit => ({ id, userId: 'u1', customerId: 'c1', status, exceptionReason: null, scheduleDate: '2026-10-04', visitType: 'pharmacy', priority: 'normal', plannedTime: '09:00:00', isUnplanned: false, customerName: 'Shifa', customerCode: 'P-1', assigneeEmail: 'rep@t1', sortOrder: null, postponeCount: 0, postponeReason: null, windowStatus: 'none' })

const place = (id: string, kind: Place['kind'], name: string, parentId: string | null = null, status: Place['status'] = 'active', mergedInto: string | null = null, childCount = 0): Place =>
  ({ id, parentId, kind, name, nameCkb: null, nameAr: null, nameEn: null, status, mergedInto, childCount })

const posts = (calls: Call[]) => calls.filter((c) => c.method !== 'GET' && c.path !== '/auth/refresh')

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
    expect(screen.queryAllByRole('combobox').map((c) => c.getAttribute('aria-label'))).toEqual(['Language'])
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

  it('creates, reassigns and cancels visits of the chosen day', async () => {
    vi.spyOn(window, 'prompt').mockReturnValue(' Shop closed ')
    const planner: Me = { id: 'm', email: 'sv@t1', roles: ['Supervisor'], permissions: ['customers.read', 'visits.cancel', 'visits.write'] }
    const today = iraqToday()
    const rep2: UserSummary = { ...rep, id: 'u2', email: 'rep2@t1' }
    const { api, calls } = server(planner, {
      [`/visit-plan?scheduleDate=${today}`]: [visit('v1', 'Planned')],
      '/users': [rep, rep2],
      '/customers?limit=100': [{ ...customer, approvalStatus: 'approved' }, { ...customer, id: 'c2', code: 'P-2' }],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Visits' }))
    const row = (await screen.findByRole('cell', { name: 'rep@t1' })).closest('tr')!
    // The current assignee is not offered again
    const select = within(row).getByRole('combobox', { name: 'New assignee for P-1' })
    expect(within(select).queryByRole('option', { name: 'rep@t1' })).not.toBeInTheDocument()
    await user.selectOptions(select, 'u2')
    await user.click(within(row).getByRole('button', { name: 'Reassign' }))
    await user.click(within(row).getByRole('button', { name: 'Cancel' }))
    // Only approved customers can be planned
    const customerSelect = screen.getByRole('combobox', { name: 'Customer' })
    expect(within(customerSelect).queryByRole('option', { name: /P-2/ })).not.toBeInTheDocument()
    await user.selectOptions(screen.getByRole('combobox', { name: 'Assignee' }), 'u2')
    await user.selectOptions(customerSelect, 'c1')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Priority' }), 'high')
    await user.type(screen.getByLabelText('Planned time'), '09:30')
    await user.click(screen.getByRole('button', { name: 'Create visit' }))
    expect(posts(calls)).toEqual([
      { method: 'PUT', path: '/visits/v1/assignee', body: { assignedUserId: 'u2' } },
      { method: 'POST', path: '/visits/v1/cancel', body: { reason: 'Shop closed' } },
      { method: 'POST', path: '/visits', body: { assignedUserId: 'u2', customerId: 'c1', scheduleDate: today, visitType: 'pharmacy', priority: 'high', plannedTime: '09:30:00' } },
    ])
  })

  it('hides visit actions the user or the visit state does not allow', async () => {
    const planner: Me = { id: 'm', email: 'sv@t1', roles: ['Supervisor'], permissions: ['visits.write'] }
    const { api } = server(planner, {
      [`/visit-plan?scheduleDate=${iraqToday()}`]: [visit('v1', 'Planned'), { ...visit('v2', 'Completed'), customerCode: 'P-9' }],
      '/users': [rep, { ...rep, id: 'u2', email: 'rep2@t1' }],
    })
    render(<App api={api} />)
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Visits' }))
    await screen.findByText('Completed')
    expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Reassign' })).toHaveLength(1)
    // No customers.read: no create form
    expect(screen.queryByRole('button', { name: 'Create visit' })).not.toBeInTheDocument()
  })

  it('reorders one assignee\'s day, moves a visit to another day and flags visits outside the customer hours', async () => {
    const prompt = vi.spyOn(window, 'prompt').mockReturnValueOnce(null).mockReturnValueOnce(' Customer closed ')
    const planner: Me = { id: 'm', email: 'sv@t1', roles: ['Supervisor'], permissions: ['visits.write'] }
    const today = iraqToday()
    const other: PlannedVisit = { ...visit('v3', 'Planned'), userId: 'u2', assigneeEmail: 'rep2@t1', customerCode: 'P-3' }
    const { api, calls } = server(planner, {
      [`/visit-plan?scheduleDate=${today}`]: [
        { ...visit('v1', 'Planned'), sortOrder: 1, windowStatus: 'outside' },
        { ...visit('v2', 'Planned'), customerCode: 'P-2', sortOrder: 2, postponeCount: 2, postponeReason: 'Not there' },
        other,
        { ...visit('v4', 'Completed'), userId: 'u2', assigneeEmail: 'rep2@t1', customerCode: 'P-4' },
      ],
      '/users': [rep, { ...rep, id: 'u2', email: 'rep2@t1' }],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Visits' }))
    await screen.findByText('P-2 · Shifa')

    // Flags: outside hours, moved before
    expect(screen.getByText(/Outside the customer's visit hours/)).toBeInTheDocument()
    expect(screen.getByText(/Moved 2×/)).toHaveTextContent('Not there')

    // Each assignee has their own order: the first cannot go up, the last cannot go down, a single visit neither
    expect(screen.getByRole('button', { name: 'Move P-1 up' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move P-2 down' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move P-3 up' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Move P-3 down' })).toBeEnabled()   // rep2 has P-3 and P-4
    await user.click(screen.getByRole('button', { name: 'Move P-1 down' }))

    // Moving to another day: only open visits; the reason prompt can be cancelled
    expect(screen.queryByLabelText('New day for P-4')).not.toBeInTheDocument()
    const dayInput = screen.getByLabelText('New day for P-2')
    expect(screen.getAllByRole('button', { name: 'Move to day' })[1]).toBeDisabled()
    fireEvent.change(dayInput, { target: { value: '2099-10-20' } })
    await user.click(screen.getAllByRole('button', { name: 'Move to day' })[1]!)
    expect(prompt).toHaveBeenCalledTimes(1)
    expect(posts(calls).map((c) => c.path)).toEqual(['/visit-plan/order'])   // cancelled: nothing sent
    await user.click(screen.getAllByRole('button', { name: 'Move to day' })[1]!)
    expect(posts(calls)).toEqual([
      { method: 'PUT', path: '/visit-plan/order', body: { scheduleDate: today, visitIds: ['v2', 'v1'], assignedUserId: 'u1' } },
      { method: 'POST', path: '/visits/v2/postpone', body: { scheduleDate: '2099-10-20', reason: 'Customer closed' } },
    ])
  })

  it('plans from a place: shows the list first, skips what the server marks and creates only the ticked customers', async () => {
    const planner: Me = { id: 'm', email: 'sv@t1', roles: ['Supervisor'], permissions: ['visits.write'] }
    const today = iraqToday()
    const candidate = (code: string, extra: object = {}) => ({ customerId: `id-${code}`, code, name: `Name ${code}`, type: 'Pharmacy', place: 'Erbil › N1', lastVisitDate: null, windowStatus: 'none', included: true, skipReason: null, ...extra })
    const preview = { matching: 3, included: 2, candidates: [candidate('C1'), candidate('C2', { lastVisitDate: '2026-09-01' }), candidate('C3', { included: false, skipReason: 'outside_hours' })] }
    const { api, calls } = server(
      planner,
      { [`/visit-plan?scheduleDate=${today}`]: [], '/users': [rep], '/places?limit=2000&kind=city': [place('p1', 'city', 'Erbil', null, 'active', null, 1)], '/places?limit=2000&parentId=p1': [place('p2', 'neighbourhood', 'N1', 'p1')] },
      { '/visit-plan/preview': json(preview), '/visit-plan/generate': json({ created: 1, skipped: 0, visitIds: ['v1'] }) },
    )
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Visits' }))
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Plan for' }), 'u1')

    // Nothing to preview before a place or region is chosen
    expect(screen.getByRole('button', { name: 'Preview' })).toBeDisabled()
    await user.selectOptions(await screen.findByRole('combobox', { name: 'City' }), 'p1')
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Neighbourhood' }), 'p2')
    fireEvent.change(screen.getByLabelText('How many (empty = all)'), { target: { value: '2' } })
    await user.click(screen.getByRole('button', { name: 'Preview' }))

    expect(await screen.findByText('2 of 3 customers will get a visit')).toBeInTheDocument()
    expect(screen.getByText('closed that day')).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: 'C3 Name C3' })).toBeDisabled()
    await user.click(screen.getByRole('checkbox', { name: 'C1 Name C1' }))   // untick one
    await user.click(screen.getByRole('button', { name: 'Create 1 visits' }))

    expect(await screen.findByText('1 visits created, 0 skipped')).toBeInTheDocument()
    expect(posts(calls).map((c) => c.path)).toEqual(['/visit-plan/preview', '/visit-plan/generate'])
    expect(posts(calls)[1]!.body).toMatchObject({ assignedUserId: 'u1', scheduleDate: today, placeId: 'p2', count: 2, order: 'least_recent', respectWindows: true, customerIds: ['id-C2'] })
  })

  it('edits the visit hours of a customer and rejects hours that overlap', async () => {
    const editor: Me = { id: 'm', email: 'rep@t1', roles: ['SalesRep'], permissions: ['customers.read', 'customers.write'] }
    const hours = [{ weekday: 2, opensAt: '09:00:00', closesAt: '13:00:00' }]
    const { api, calls } = server(editor, { '/customers?limit=100': [{ ...customer, approvalStatus: 'approved' }], '/customers/c1/visit-windows': hours })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Visit hours' }))
    expect(await screen.findByLabelText('From')).toHaveValue('09:00')

    // A second window on the same day that overlaps the first is refused before anything is sent
    await user.click(screen.getByRole('button', { name: 'Add hours' }))
    const days = screen.getAllByLabelText('Day')
    await user.selectOptions(days[1]!, '2')
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('must not overlap')
    expect(posts(calls)).toEqual([])

    // Thursday afternoon instead
    await user.selectOptions(days[1]!, '4')
    fireEvent.change(screen.getAllByLabelText('From')[1]!, { target: { value: '15:00' } })
    fireEvent.change(screen.getAllByLabelText('To')[1]!, { target: { value: '18:30' } })
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(posts(calls)).toEqual([
      { method: 'PUT', path: '/customers/c1/visit-windows', body: { windows: [hours[0], { weekday: 4, opensAt: '15:00:00', closesAt: '18:30:00' }] } },
    ])
    expect(screen.queryByLabelText('From')).not.toBeInTheDocument()   // the editor closes after saving
  })

  it('shows the visit hours read-only without customers.write', async () => {
    const reader: Me = { id: 'f', email: 'finance@t1', roles: ['FinanceManager'], permissions: ['customers.read'] }
    const { api } = server(reader, { '/customers?limit=100': [{ ...customer, approvalStatus: 'approved' }], '/customers/c1/visit-windows': [{ weekday: 0, opensAt: '08:00:00', closesAt: '12:00:00' }] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Visit hours' }))
    expect(await screen.findByLabelText('From')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Add hours' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
  })

  it('browses the places tree and adds, renames, moves, merges and deactivates without approval', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const manager2: Me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['customers.read', 'places.manage'] }
    const none = { nameCkb: null, nameAr: null, nameEn: null }
    const erbil = place('ci', 'city', 'Erbil', null, 'active', null, 2)
    const duhok = place('du', 'city', 'Duhok')
    const ankawa = place('an', 'neighbourhood', 'Ankawa', 'ci', 'active', null, 2)
    const bakhtiari = place('bk', 'neighbourhood', 'Bakhtiari', 'ci')
    const main = place('ms', 'street', 'Main Street', 'an', 'active', null, 1)
    const alley2 = place('a2', 'alley', 'Alley 2', 'an')
    const { api, calls } = server(manager2, {
      '/places?limit=2000&kind=city': [erbil, duhok],
      '/places?limit=2000&parentId=ci': [ankawa, bakhtiari],
      '/places?limit=2000&parentId=an': [main, alley2],
      '/places?limit=2000&parentId=an&includeInactive=true': [main, alley2, place('old', 'street', 'Old Street', 'an', 'inactive'), place('gone', 'street', 'Gone Street', 'an', 'inactive', 'ms')],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Places' }))
    await screen.findByRole('button', { name: 'Open Erbil' })

    // Cities: duplicates are merged into the kept one (customers and everything below move along); a city cannot be moved
    expect(screen.queryByRole('combobox', { name: 'Move Duhok to' })).not.toBeInTheDocument()
    await user.selectOptions(screen.getByRole('combobox', { name: 'Merge Duhok into' }), 'ci')
    await user.click(screen.getByRole('button', { name: 'Merge Duhok' }))

    // Below a city: neighbourhoods, and the form adds one under it
    await user.click(screen.getByRole('button', { name: 'Open Erbil' }))
    await screen.findByRole('button', { name: 'Open Ankawa' })
    expect(screen.getByRole('heading', { name: 'Add under Erbil' })).toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Kind' })).not.toBeInTheDocument()   // only a neighbourhood can go under a city
    await user.type(screen.getByRole('textbox', { name: 'Name' }), 'Nishtiman')
    await user.click(screen.getByRole('button', { name: 'Add place' }))

    // Below a neighbourhood: streets and alleys; the kind is chosen
    await user.click(screen.getByRole('button', { name: 'Open Ankawa' }))
    await screen.findByRole('button', { name: 'Open Main Street' })
    expect(screen.queryByRole('button', { name: 'Open Alley 2' })).not.toBeInTheDocument()   // nothing is below an alley
    await user.selectOptions(screen.getByRole('combobox', { name: 'Kind' }), 'alley')
    await user.type(screen.getByRole('textbox', { name: 'Name' }), 'Alley 9')
    await user.click(screen.getByRole('button', { name: 'Add place' }))

    // Fixing a spelling, moving a street to another neighbourhood, deactivating an alley
    await user.click(screen.getByRole('button', { name: 'Edit Main Street' }))
    await user.type(screen.getAllByRole('textbox', { name: 'Kurdish name' })[0]!, 'شەقامی سەرەکی')   // the edit row comes before the add form
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(within(screen.getByRole('combobox', { name: 'Move Main Street to' })).getAllByRole('option').map((o) => o.textContent)).toEqual(['', 'Bakhtiari'])   // the other neighbourhoods of the city
    await user.selectOptions(screen.getByRole('combobox', { name: 'Move Main Street to' }), 'bk')
    await user.click(screen.getByRole('button', { name: 'Move Main Street' }))
    await user.click(screen.getByRole('button', { name: 'Deactivate Alley 2' }))

    // Inactive and merged places are shown only on request; a merged one has no actions
    await user.click(screen.getByRole('checkbox', { name: 'Show inactive' }))
    expect(await screen.findByRole('cell', { name: 'Old Street' })).toBeInTheDocument()
    expect(screen.getByText('Merged')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit Gone Street' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Activate Old Street' }))

    expect(posts(calls)).toEqual([
      { method: 'POST', path: '/places/du/merge', body: { intoPlaceId: 'ci' } },
      { method: 'POST', path: '/places', body: { parentId: 'ci', kind: 'neighbourhood', name: 'Nishtiman', ...none } },
      { method: 'POST', path: '/places', body: { parentId: 'an', kind: 'alley', name: 'Alley 9', ...none } },
      { method: 'PUT', path: '/places/ms', body: { name: 'Main Street', nameCkb: 'شەقامی سەرەکی', nameAr: null, nameEn: null, status: 'active', parentId: null } },
      { method: 'PUT', path: '/places/ms', body: { name: 'Main Street', ...none, status: 'active', parentId: 'bk' } },
      { method: 'PUT', path: '/places/a2', body: { name: 'Alley 2', ...none, status: 'inactive', parentId: null } },
      { method: 'PUT', path: '/places/old', body: { name: 'Old Street', ...none, status: 'active', parentId: null } },
    ])
  })

  it('imports a CSV file, refuses one without valid rows, and exports the tree as CSV and JSON', async () => {
    const manager2: Me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['places.manage'] }
    const { api, calls } = server(manager2, { '/places?limit=2000&kind=city': [], '/places/export?format=csv': 'id,kind\n', '/places/export?format=json': [] }, { '/places/import': json({ created: 5, skipped: 2 }) })
    const downloads: string[] = []
    URL.createObjectURL = vi.fn(() => 'blob:x')
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download)
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Places' }))
    const input = await screen.findByLabelText('Import CSV')

    await user.upload(input, new File(['kind,city,name\nstreet,Erbil,Main\n'], 'bad.csv', { type: 'text/csv' }))   // a street needs its neighbourhood
    expect(await screen.findByRole('alert')).toHaveTextContent('no valid rows')
    expect(posts(calls)).toEqual([])

    const csv = '\uFEFFid,parent_id,kind,city,neighbourhood,street,name,name_ckb,name_ar,name_en,status\r\n'
      + 'x,,city,Erbil,,,Erbil,هەولێر,أربيل,,active\r\n'
      + 'y,x,street,Erbil,Ankawa,,"Main, Street",,,,inactive\r\n'
      + 'z,y,alley,Erbil,Ankawa,Main Street,\'=A1,,,,active\r\n'
    await user.upload(input, new File([csv], 'places.csv', { type: 'text/csv' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Imported: 5 new, 2 already existed.')
    expect(posts(calls)).toEqual([
      {
        method: 'POST',
        path: '/places/import',
        body: {
          rows: [
            { kind: 'city', name: 'Erbil', city: null, neighbourhood: null, street: null, nameCkb: 'هەولێر', nameAr: 'أربيل', nameEn: null, active: true },
            { kind: 'street', name: 'Main, Street', city: 'Erbil', neighbourhood: 'Ankawa', street: null, nameCkb: null, nameAr: null, nameEn: null, active: false },
            { kind: 'alley', name: '=A1', city: 'Erbil', neighbourhood: 'Ankawa', street: 'Main Street', nameCkb: null, nameAr: null, nameEn: null, active: true },
          ],
        },
      },
    ])

    await user.click(screen.getByRole('button', { name: 'Export CSV' }))
    await user.click(screen.getByRole('button', { name: 'Export JSON' }))
    await waitFor(() => expect(downloads).toEqual(['places.csv', 'places.json']))
    expect(calls.filter((c) => c.path.startsWith('/places/export')).map((c) => c.path)).toEqual(['/places/export?format=csv', '/places/export?format=json'])
  })

  it('edits the profile of a customer with lists that follow the places tree', async () => {
    const editor: Me = { id: 'm', email: 'rep@t1', roles: ['SalesRep'], permissions: ['customers.read', 'customers.write'] }
    const path = [{ id: 'ci', kind: 'city', name: 'Erbil' }, { id: 'an', kind: 'neighbourhood', name: 'Ankawa' }, { id: 'ms', kind: 'street', name: 'Main Street' }] as const
    const known = { ...customer, approvalStatus: 'approved', phone: '0750', address: 'By the bridge', placeId: 'ms', placePath: [...path] }
    const { api, calls } = server(editor, {
      '/customers?limit=100': [known],
      '/places?limit=2000&kind=city': [place('ci', 'city', 'Erbil'), place('du', 'city', 'Duhok')],
      '/places?limit=2000&parentId=ci': [place('an', 'neighbourhood', 'Ankawa', 'ci'), place('bk', 'neighbourhood', 'Bakhtiari', 'ci')],
      '/places?limit=2000&parentId=an': [place('ms', 'street', 'Main Street', 'an'), place('a2', 'alley', 'Alley 2', 'an')],
      '/places?limit=2000&parentId=ms': [place('a1', 'alley', 'Alley 1', 'ms')],
      '/places?limit=2000&parentId=du': [],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Profile P-1' }))
    expect(await screen.findByRole('textbox', { name: 'Phone' })).toHaveValue('0750')
    // The saved place and every level above it are chosen; one more list offers what is below the street
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Alley' })).toHaveValue(''))
    expect(screen.getByRole('combobox', { name: 'City' })).toHaveValue('ci')
    expect(screen.getByRole('combobox', { name: 'Neighbourhood' })).toHaveValue('an')

    await user.selectOptions(screen.getByRole('combobox', { name: 'Alley' }), 'a1')
    await user.clear(screen.getByRole('textbox', { name: 'Address note' }))
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(posts(calls)).toEqual([{ method: 'PUT', path: '/customers/c1/profile', body: { phone: '0750', address: null, placeId: 'a1' } }])
  })

  it('drops the lists below a changed level and saves the most precise choice', async () => {
    const editor: Me = { id: 'm', email: 'rep@t1', roles: ['SalesRep'], permissions: ['customers.read', 'customers.write'] }
    const known = { ...customer, approvalStatus: 'approved', placeId: 'ms', placePath: [{ id: 'ci', kind: 'city', name: 'Erbil' }, { id: 'an', kind: 'neighbourhood', name: 'Ankawa' }, { id: 'ms', kind: 'street', name: 'Main Street' }] }
    const { api, calls } = server(editor, {
      '/customers?limit=100': [known],
      '/places?limit=2000&kind=city': [place('ci', 'city', 'Erbil'), place('du', 'city', 'Duhok')],
      '/places?limit=2000&parentId=ci': [place('an', 'neighbourhood', 'Ankawa', 'ci')],
      '/places?limit=2000&parentId=an': [place('ms', 'street', 'Main Street', 'an')],
      '/places?limit=2000&parentId=ms': [],
      '/places?limit=2000&parentId=du': [],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Profile P-1' }))
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Street' })).toHaveValue('ms'))

    // Another city: the neighbourhood and street lists of the old one are gone and Duhok has nothing below it yet
    await user.selectOptions(screen.getByRole('combobox', { name: 'City' }), 'du')
    await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Street' })).not.toBeInTheDocument())
    expect(screen.queryByRole('combobox', { name: 'Neighbourhood' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(posts(calls)).toEqual([{ method: 'PUT', path: '/customers/c1/profile', body: { phone: null, address: null, placeId: 'du' } }])
  })

  it('shows the customer profile read-only without customers.write', async () => {
    const reader: Me = { id: 'f', email: 'finance@t1', roles: ['FinanceManager'], permissions: ['customers.read'] }
    const { api } = server(reader, {
      '/customers?limit=100': [{ ...customer, approvalStatus: 'approved', phone: '0750', placeId: 'ci', placePath: [{ id: 'ci', kind: 'city', name: 'Erbil' }] }],
      '/places?limit=2000&kind=city': [place('ci', 'city', 'Erbil')],
      '/places?limit=2000&parentId=ci': [],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Profile P-1' }))
    expect(await screen.findByRole('textbox', { name: 'Phone' })).toBeDisabled()
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'City' })).toBeDisabled())
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
  })

  it('shows the unread count, lists the messages and marks them read', async () => {
    const supervisor: Me = { id: 's', email: 'sv@t1', roles: ['Supervisor'], permissions: ['customers.read'] }
    const message = (id: string, kind: 'customer.profile_changed' | 'customer.location_changed', readAt: string | null) => ({
      id, kind, entityId: 'c1', actorId: 'u1', actorEmail: id === 'n3' ? null : 'rep@t1', customerCode: id === 'n3' ? null : 'P-1', customerName: id === 'n3' ? null : 'Shifa',
      createdAt: '2026-10-05T07:30:00Z', readAt,
    })
    const { api, calls } = server(supervisor, {
      '/notifications/unread-count': { count: 2 },
      '/notifications?limit=200': [message('n1', 'customer.profile_changed', null), message('n2', 'customer.location_changed', '2026-10-05T08:00:00Z'), message('n3', 'customer.profile_changed', null)],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Notifications (2)' }))

    expect(await screen.findByText('rep@t1 changed the profile of P-1 · Shifa')).toBeInTheDocument()
    expect(screen.getByText('rep@t1 changed the location of P-1 · Shifa')).toBeInTheDocument()
    expect(screen.getByText('someone changed the profile of a customer')).toBeInTheDocument()   // a message without names still reads well
    expect(screen.getByText('2 unread')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Mark read' })).toHaveLength(2)              // the read one has no button

    await user.click(screen.getAllByRole('button', { name: 'Mark read' })[0]!)
    await user.click(screen.getByRole('button', { name: 'Mark all read' }))
    expect(posts(calls).map((c) => c.path)).toEqual(['/notifications/n1/read', '/notifications/read-all'])
  })

  it('shows no number on the Notifications tab when nothing is unread, and survives a failing count', async () => {
    const { api } = server(manager, { '/notifications/unread-count': { count: 0 } })
    render(<App api={api} />)
    expect(await screen.findByRole('button', { name: 'Notifications' })).toBeInTheDocument()
    const { api: failing } = server(manager, {})   // the count route answers 404
    render(<App api={failing} />)
    expect(await screen.findAllByRole('button', { name: 'Notifications' })).toHaveLength(2)
  })

  it('manages regions, teams and members', async () => {
    const salesManager: Me = { id: 'm', email: 'manager@t1', roles: ['SalesManager'], permissions: ['regions.manage', 'regions.read', 'sessions.kill', 'teams.manage', 'teams.read'] }
    const regions = [{ id: 'r', name: 'Erbil' }, { id: 'r2', name: 'Baghdad' }]
    const { api, calls } = server(salesManager, { '/regions': regions, '/teams': [{ id: 't', name: 'Team A', regionId: 'r' }], '/users': [rep, { ...rep, id: 'u2', email: 'rep2@t1', teamId: null, teamName: null }] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Regions & teams' }))
    await user.type(await screen.findByLabelText('New region name'), ' Basra ')
    await user.click(screen.getByRole('button', { name: 'Add region' }))
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Region of Team A' }), 'r2')
    await user.click(await screen.findByRole('button', { name: 'Remove rep@t1 from Team A' }))
    await user.selectOptions(screen.getByRole('combobox', { name: 'Add member to Team A' }), 'u2')
    await user.click(screen.getByRole('button', { name: 'Add' }))
    await user.type(screen.getByLabelText('New team name'), 'Team C')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Region of the new team' }), 'r2')
    await user.click(screen.getByRole('button', { name: 'Add team' }))
    expect(posts(calls)).toEqual([
      { method: 'POST', path: '/regions', body: { name: 'Basra' } },
      { method: 'PUT', path: '/teams/t/region', body: { regionId: 'r2' } },
      { method: 'DELETE', path: '/teams/t/members/u1', body: undefined },
      { method: 'PUT', path: '/teams/t/members/u2', body: undefined },
      { method: 'POST', path: '/teams', body: { name: 'Team C', regionId: 'r2' } },
    ])
  })

  it('shows regions and teams read-only without manage permissions', async () => {
    const { api, calls } = server({ id: 'x', email: 'ceo@t1', roles: ['CompanyManager'], permissions: ['regions.read', 'teams.read'] }, { '/regions': [{ id: 'r', name: 'Erbil' }], '/teams': [{ id: 't', name: 'Team A', regionId: 'r' }] })
    render(<App api={api} />)
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Regions & teams' }))
    expect(await screen.findByText('Team A')).toBeInTheDocument()
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryAllByRole('combobox').map((c) => c.getAttribute('aria-label'))).toEqual(['Language'])
    expect(calls.some((c) => c.path === '/users')).toBe(false)
  })
})
