import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { iraqToday, type FormField, type KpiReport, type Me, type PlatformAuditEntry, type SavedReport, type Tenant } from '../api/admin.ts'

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
    if (method !== 'GET') return posts[`${method} ${path}`] ?? new Response(null, { status: 204 })
    return path in data ? json(data[path]) : json({ code: 'NOT_FOUND' }, 404)
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return { api: new ApiClient('https://api.test', fetcher, sessionStorage), calls }
}

const sent = (calls: Call[]) => calls.filter((c) => c.method !== 'GET' && c.path !== '/auth/refresh')
const me = (permissions: string[]): Me => ({ id: 'm', email: 'manager@t1', roles: ['SalesManager'], permissions })

const field = (id: string, key: string, over: Partial<FormField> = {}): FormField => ({
  id, key, type: 'text', appliesTo: 'all', labelEn: `Label ${key}`, labelCkb: null, labelAr: null, labelFa: null, options: [], isRequired: false, sortOrder: 0, isActive: true, ...over,
})

afterEach(() => vi.restoreAllMocks())

describe('form builder screen (step 3.2)', () => {
  it('only shows the tab with forms.manage, adds a choice field and switches one off', async () => {
    const { api: noForms } = server(me(['customers.read']), {})
    const view = render(<App api={noForms} />)
    expect(await screen.findByRole('button', { name: 'Customers' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Visit forms' })).not.toBeInTheDocument()
    view.unmount()

    const { api, calls } = server(me(['forms.manage']), { '/form-fields': [field('f1', 'notes'), field('f2', 'rating', { type: 'choice', options: ['good', 'bad'], isRequired: true })] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Visit forms' }))
    expect(await screen.findByText('Label notes')).toBeInTheDocument()
    expect(screen.getByText('good · bad')).toBeInTheDocument()

    await user.type(screen.getByLabelText('Key (a-z, 0-9, _)'), 'rating2')
    await user.selectOptions(screen.getByLabelText('Type'), 'choice')
    await user.selectOptions(screen.getByLabelText('Visit type'), 'doctor')
    await user.type(screen.getByLabelText('Label (English)'), 'Rating')
    await user.type(screen.getByLabelText('Choices (separated by commas)'), 'good, bad , neutral,')
    await user.click(screen.getByLabelText('Required'))
    await user.click(screen.getByRole('button', { name: 'Add field' }))
    expect(sent(calls)).toEqual([
      { method: 'POST', path: '/form-fields', body: { key: 'rating2', type: 'choice', appliesTo: 'doctor', labelEn: 'Rating', labelCkb: '', labelAr: '', labelFa: '', options: ['good', 'bad', 'neutral'], isRequired: true, sortOrder: 0 } },
    ])

    const row = screen.getByText('Label notes').closest('tr')!
    await user.click(within(row).getByRole('button', { name: 'Deactivate' }))
    await user.click(within(row).getByRole('button', { name: 'Make required' }))
    expect(sent(calls).slice(1)).toEqual([
      { method: 'PUT', path: '/form-fields/f1', body: { isActive: false } },
      { method: 'PUT', path: '/form-fields/f1', body: { isRequired: true } },
    ])
  })
})

describe('saved reports (step 3.2)', () => {
  const empty: KpiReport = { from: '2026-10-01', to: '2026-10-05', total: { userId: '', email: null, teamName: null, counts: { visitsPlanned: 0, visitsCompleted: 0, visitsFailed: 0, delayBase: 0, delayedVisits: 0, newCustomers: 0, approvedNewCustomers: 0, orders: 0, ordersDelivered: 0, ordersDeliveryFailed: 0, orderTotals: [], followUpBase: 0, followUpsOverdue: 0 }, rates: { visitCompletion: null, visitFailure: null, delay: null, orderDelivery: null, followUpOverdue: null } }, users: [], alerts: [] }
  const saved: SavedReport = { id: 's1', name: 'Weekly KPIs', range: 'last_7_days', from: null, to: null, regionId: null, teamId: null, customerType: null, createdAt: '2026-10-05T06:00:00Z',
    schedule: { frequency: 'weekly', weekday: 1, dayOfMonth: null, hour: 8, isActive: true, nextRunAt: '2026-10-12T05:00:00Z', lastRunAt: null } }

  it('saves the filters with a schedule, runs, pauses and deletes a report', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const data = { [`/reports/kpis?from=${iraqToday().slice(0, 8)}01&to=${iraqToday()}`]: empty, '/regions': [], '/teams': [], '/saved-reports': [saved] }
    const { api, calls } = server(me(['reports.read']), data, { 'POST /saved-reports/s1/run': json({ run: { id: 'r1', reportId: 's1', trigger: 'manual', status: 'ok', errorCode: null, from: '2026-09-28', to: '2026-10-04', ranAt: '2026-10-05T06:00:00Z' }, report: { ...empty, from: '2026-09-28', to: '2026-10-04' } }) })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Reports' }))
    expect(await screen.findByText('Weekly KPIs')).toBeInTheDocument()
    expect(screen.getByText(/Next run: 2026-10-12 05:00/)).toBeInTheDocument()

    await user.type(screen.getByLabelText('Report name'), 'Daily')
    await user.selectOptions(screen.getByLabelText('Period'), 'yesterday')
    await user.selectOptions(screen.getByLabelText('Schedule'), 'weekly')
    await user.selectOptions(screen.getByLabelText('Day of the week'), '3')
    await user.clear(screen.getByLabelText('Hour (Iraq time)'))
    await user.type(screen.getByLabelText('Hour (Iraq time)'), '7')
    await user.click(screen.getByRole('button', { name: 'Save this report' }))
    expect(sent(calls)).toEqual([{ method: 'POST', path: '/saved-reports', body: { name: 'Daily', range: 'yesterday', schedule: { frequency: 'weekly', hour: 7, isActive: true, weekday: 3 } } }])

    await user.click(screen.getByRole('button', { name: 'Run now' }))
    await user.click(screen.getByRole('button', { name: 'Pause' }))
    await user.click(screen.getByRole('button', { name: 'Delete' }))
    expect(sent(calls).slice(1)).toEqual([
      { method: 'POST', path: '/saved-reports/s1/run', body: {} },
      { method: 'PUT', path: '/saved-reports/s1', body: { name: 'Weekly KPIs', range: 'last_7_days', schedule: { frequency: 'weekly', weekday: 1, hour: 8, isActive: false } } },
      { method: 'DELETE', path: '/saved-reports/s1', body: undefined },
    ])
  })
})

describe('companies screen (step 3.1)', () => {
  const tenants: Tenant[] = [
    { id: 't0', code: 'platform', name: 'Platform', isActive: true, users: 1, activeUsers: 1 },
    { id: 't1', code: 'acme', name: 'Acme', isActive: true, users: 3, activeUsers: 2 },
  ]
  const log: PlatformAuditEntry[] = [{ id: 'a1', actorId: 'm', actorEmail: 'root@platform', action: 'platform.tenant_created', targetTenantId: 't1', targetTenantCode: 'acme', reason: null, detail: 'code=acme', occurredAt: '2026-10-05T06:00:00Z' }]

  it('creates, switches off and reads another company with a reason', async () => {
    vi.spyOn(window, 'prompt').mockReturnValue('unpaid invoice')
    const { api, calls } = server(me(['platform.tenants.manage']), { '/platform/tenants': tenants, '/platform/audit-logs': log, '/platform/tenants/t1/audit-logs?reason=support%20ticket%204711': [{ id: 'e1', actorId: 'u', actorEmail: 'rep@acme', action: 'payment.recorded', entityId: 'p-1', occurredAt: '2026-10-05T05:00:00Z' }] })
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Companies' }))
    expect(await screen.findByText('Acme')).toBeInTheDocument()
    expect(screen.getByText('root@platform')).toBeInTheDocument()

    await user.type(screen.getByLabelText('Company code'), 'newco')
    await user.type(screen.getByLabelText('Name'), 'New Co')
    await user.type(screen.getByLabelText('Manager email'), 'boss@newco.test')
    await user.type(screen.getByLabelText('Manager password (at least 12 characters)'), 'a-long-password-1')
    await user.click(screen.getByRole('button', { name: 'Create company' }))
    expect(sent(calls)).toEqual([{ method: 'POST', path: '/platform/tenants', body: { code: 'newco', name: 'New Co', managerEmail: 'boss@newco.test', managerPassword: 'a-long-password-1' } }])

    const acme = screen.getByText('Acme').closest('tr')!
    await user.click(within(acme).getByRole('button', { name: 'Deactivate' }))
    expect(within(screen.getByText('Platform').closest('tr')!).queryByRole('button', { name: 'Deactivate' })).not.toBeInTheDocument()   // the platform company stays on
    expect(sent(calls).slice(1)).toEqual([{ method: 'PUT', path: '/platform/tenants/t1', body: { isActive: false, reason: 'unpaid invoice' } }])

    await user.selectOptions(screen.getByLabelText('Company'), 't1')
    await user.type(screen.getByLabelText('Reason for reading (at least 10 characters; it is recorded)'), 'support ticket 4711')
    await user.click(screen.getByRole('button', { name: 'Read' }))
    expect(await screen.findByText('payment.recorded')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/platform/tenants/t1/audit-logs?reason=support%20ticket%204711')).toBe(true)
  })
})
