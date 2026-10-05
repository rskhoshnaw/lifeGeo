import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { iraqToday, type City, type Customer, type Place, type Device, type Me, type PlannedVisit, type UserSummary } from '../api/admin.ts'

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
const customer: Customer = { id: 'c1', type: 'pharmacy', code: 'P-1', name: 'Shifa', status: 'located', createdAt: '2026-10-04T08:00:00Z', regionId: 'r', approvalStatus: 'pending', phone: null, address: null, cityId: null, neighbourhoodId: null, streetId: null }

const visit = (id: string, status: string): PlannedVisit => ({ id, userId: 'u1', customerId: 'c1', status, exceptionReason: null, scheduleDate: '2026-10-04', visitType: 'pharmacy', priority: 'normal', plannedTime: '09:00:00', isUnplanned: false, customerName: 'Shifa', customerCode: 'P-1', assigneeEmail: 'rep@t1', sortOrder: null, postponeCount: 0, postponeReason: null, windowStatus: 'none' })

const erbil: City = { id: 'ci', name: 'Erbil', nameCkb: null, nameAr: null, nameEn: null, status: 'active' }
const place = (id: string, kind: Place['kind'], name: string, status: Place['status'] = 'active', mergedInto: string | null = null): Place =>
  ({ id, cityId: 'ci', kind, name, nameCkb: null, nameAr: null, nameEn: null, status, mergedInto })

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

  it('manages the reference list: approve, rename, merge, deactivate, add', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const manager2: Me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['customers.read', 'places.manage'] }
    const { api, calls } = server(manager2, {
      '/cities?includeInactive=true': [erbil],
      '/places?cityId=ci&limit=2000&includeInactive=true': [
        place('p1', 'street', 'Gulan Street', 'suggested'),
        place('p2', 'street', 'Gulan St'),
        place('p3', 'neighbourhood', 'Ankawa'),
        place('p4', 'street', 'Old Street', 'inactive'),
        place('p5', 'street', 'Gone Street', 'inactive', 'p2'),
      ],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Places' }))
    await user.selectOptions(await screen.findByRole('combobox', { name: 'City' }), 'ci')
    await screen.findByRole('cell', { name: 'Gulan Street' })

    // Active and suggested names are listed; inactive and merged ones are not
    expect(screen.getByRole('cell', { name: 'Ankawa' })).toBeInTheDocument()
    expect(screen.queryByRole('cell', { name: 'Old Street' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Approve Gulan Street' }))
    // Only live names of the same kind are offered as the merge target
    const target = screen.getByRole('combobox', { name: 'Merge Gulan Street into' })
    expect(within(target).getAllByRole('option').map((o) => o.textContent)).toEqual(['', 'Gulan St'])
    await user.selectOptions(target, 'p2')
    await user.click(screen.getByRole('button', { name: 'Merge Gulan Street' }))

    await user.click(screen.getByRole('button', { name: 'Edit Ankawa' }))
    await user.type(screen.getAllByRole('textbox', { name: 'Kurdish name' })[0]!, 'عەنکاوە')   // the edit row comes before the add form
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await user.type(screen.getByRole('textbox', { name: 'New city name' }), 'Duhok')
    await user.click(screen.getByRole('button', { name: 'Add city' }))
    await user.selectOptions(screen.getAllByRole('combobox', { name: 'Kind' })[1]!, 'neighbourhood')
    await user.type(screen.getAllByRole('textbox', { name: 'Name' })[0]!, 'Khabat')
    await user.click(screen.getByRole('button', { name: 'Add place' }))

    // Inactive: the merged name has no actions, the other one can be activated again
    await user.selectOptions(screen.getByRole('combobox', { name: 'Status' }), 'inactive')
    expect(screen.getByText('Merged')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit Gone Street' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Activate Old Street' }))

    const none = { nameCkb: null, nameAr: null, nameEn: null }
    expect(posts(calls)).toEqual([
      { method: 'PUT', path: '/places/p1', body: { name: 'Gulan Street', ...none, status: 'active' } },
      { method: 'POST', path: '/places/p1/merge', body: { intoPlaceId: 'p2' } },
      { method: 'PUT', path: '/places/p3', body: { name: 'Ankawa', nameCkb: 'عەنکاوە', nameAr: null, nameEn: null, status: 'active' } },
      { method: 'POST', path: '/cities', body: { name: 'Duhok' } },
      { method: 'POST', path: '/places', body: { cityId: 'ci', kind: 'neighbourhood', name: 'Khabat', ...none } },
      { method: 'PUT', path: '/places/p4', body: { name: 'Old Street', ...none, status: 'active' } },
    ])
  })

  it('imports a CSV file of places and refuses a file without valid rows', async () => {
    const manager2: Me = { id: 'm', email: 'm@t1', roles: ['SalesManager'], permissions: ['places.manage'] }
    const { api, calls } = server(manager2, { '/cities?includeInactive=true': [erbil] }, { '/places/import': json({ citiesCreated: 1, placesCreated: 2, skipped: 1 }) })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Places' }))
    const input = await screen.findByLabelText('Import CSV')

    await user.upload(input, new File(['city,kind,name_ckb\nErbil,road,Bad kind\n'], 'bad.csv', { type: 'text/csv' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('no valid rows')
    expect(posts(calls)).toEqual([])

    const csv = '\uFEFFcity,kind,name_ckb,name_ar,name_en,name_local,osm,active\r\n'
      + 'Erbil,street,,,,"100m Road, east",way/1,yes\r\n'
      + 'Erbil,neighbourhood,عەنکاوە,,Ankawa,,node/2,no\r\n'
      + ',street,,,,No city,,yes\r\n'
    await user.upload(input, new File([csv], 'places.csv', { type: 'text/csv' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Imported: 2 new names, 1 new cities, 1 already existed.')
    expect(posts(calls)).toEqual([
      {
        method: 'POST',
        path: '/places/import',
        body: {
          rows: [
            { city: 'Erbil', kind: 'street', name: '100m Road, east', nameCkb: null, nameAr: null, nameEn: null, osm: 'way/1', active: true },
            { city: 'Erbil', kind: 'neighbourhood', name: 'Ankawa', nameCkb: 'عەنکاوە', nameAr: null, nameEn: 'Ankawa', osm: 'node/2', active: false },
          ],
        },
      },
    ])
  })

  it('edits the profile of a customer with the city, neighbourhood and street lists', async () => {
    const editor: Me = { id: 'm', email: 'rep@t1', roles: ['SalesRep'], permissions: ['customers.read', 'customers.write'] }
    const known = { ...customer, approvalStatus: 'approved', phone: '0750', address: 'By the bridge', cityId: 'ci', streetId: 'p2' }
    const { api, calls } = server(editor, {
      '/customers?limit=100': [known],
      '/cities': [erbil, { ...erbil, id: 'cz', name: 'Zakho' }],
      '/places?cityId=ci&limit=2000': [place('p2', 'street', 'Gulan St'), place('p3', 'neighbourhood', 'Ankawa')],
      '/places?cityId=cz&limit=2000': [],
    })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Profile P-1' }))
    expect(await screen.findByRole('textbox', { name: 'Phone' })).toHaveValue('0750')
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Street' })).toHaveValue('p2'))

    // Choosing another city clears the neighbourhood and street of the old one
    await user.selectOptions(screen.getByRole('combobox', { name: 'City' }), 'cz')
    expect(screen.getByRole('combobox', { name: 'Street' })).toHaveValue('')
    await user.selectOptions(screen.getByRole('combobox', { name: 'City' }), 'ci')
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Neighbourhood' }), 'p3')
    await waitFor(() => expect(within(screen.getByRole('combobox', { name: 'Street' })).getAllByRole('option').length).toBe(2))
    await user.selectOptions(screen.getByRole('combobox', { name: 'Street' }), 'p2')
    await user.clear(screen.getByRole('textbox', { name: 'Address note' }))
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(posts(calls)).toEqual([
      { method: 'PUT', path: '/customers/c1/profile', body: { phone: '0750', address: null, cityId: 'ci', neighbourhoodId: 'p3', streetId: 'p2' } },
    ])
  })

  it('shows the customer profile read-only without customers.write', async () => {
    const reader: Me = { id: 'f', email: 'finance@t1', roles: ['FinanceManager'], permissions: ['customers.read'] }
    const { api } = server(reader, { '/customers?limit=100': [{ ...customer, approvalStatus: 'approved', phone: '0750' }], '/cities': [erbil] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Customers' }))
    await user.click(await screen.findByRole('button', { name: 'Profile P-1' }))
    expect(await screen.findByRole('textbox', { name: 'Phone' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
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
