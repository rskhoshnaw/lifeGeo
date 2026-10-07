import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { auditQuery, type AuditEntry, type Me } from '../api/admin.ts'

const auditor: Me = { id: 'a', email: 'auditor@t1', roles: ['FinanceManager'], permissions: ['audit.read'] }
const row = (i: number): AuditEntry => ({
  id: `id-${i}`, actorId: i % 2 ? 'u1' : 'u2', actorEmail: i % 2 ? 'rep-a@t1' : null, action: 'visit.created', entityId: `v${i}`,
  occurredAt: new Date(Date.UTC(2026, 9, 4, 12, 0, 0) - i * 60_000).toISOString(),
})

// Fake API: /audit-logs returns a full page of 50 for the first request, then the given rows.
function serve(me: Me, later: AuditEntry[] = [row(51)]) {
  const audit: string[] = []
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace('https://api.test', '')
    if ((init?.method ?? 'GET') !== 'GET' && path !== '/auth/refresh') throw new Error(`unexpected ${init?.method} ${path}`)
    if (path === '/auth/refresh') return new Response(JSON.stringify({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' }))
    if (path === '/me') return new Response(JSON.stringify(me))
    if (path === '/notifications/unread-count') return new Response(JSON.stringify({ count: 0 }))
    if (path.startsWith('/company-config/')) return new Response('', { status: 404 })
    audit.push(path)
    const body = audit.length === 1 ? Array.from({ length: 50 }, (_, i) => row(i + 1)) : later
    return new Response(JSON.stringify(body))
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return { api: new ApiClient('https://api.test', fetcher, sessionStorage), audit }
}

describe('audit viewer', () => {
  it('builds the query with Iraq-day bounds and the cursor of the last row', () => {
    expect(auditQuery({ action: 'visit.created', fromDate: '2026-10-01', toDate: '2026-10-02' })).toBe(
      '/audit-logs?limit=50&action=visit.created&from=2026-10-01T00%3A00%3A00%2B03%3A00&to=2026-10-02T23%3A59%3A59.999%2B03%3A00',
    )
    expect(auditQuery({}, row(3))).toBe(`/audit-logs?limit=50&cursorOccurredAt=${encodeURIComponent(row(3).occurredAt)}&cursorId=id-3`)
  })

  it('loads, pages, filters by action, dates and actor, and offers no way to change rows', async () => {
    const { api, audit } = serve(auditor)
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Audit' }))
    expect(await screen.findByText('v50')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Load more' }))
    expect(await screen.findByText('v51')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()

    await user.type(screen.getByLabelText('Action'), ' visit.created ')
    await user.type(screen.getByLabelText('From date'), '2026-10-01')
    await user.click(screen.getByRole('button', { name: 'Search' }))
    await user.click((await screen.findAllByRole('button', { name: 'rep-a@t1' }))[0]!)
    expect(audit).toEqual([
      '/audit-logs?limit=50',
      `/audit-logs?limit=50&cursorOccurredAt=${encodeURIComponent(row(50).occurredAt)}&cursorId=id-50`,
      '/audit-logs?limit=50&action=visit.created&from=2026-10-01T00%3A00%3A00%2B03%3A00',
      '/audit-logs?limit=50&action=visit.created&actorId=u1&from=2026-10-01T00%3A00%3A00%2B03%3A00',
    ])
    expect(screen.getByLabelText('Actor id')).toHaveValue('u1')
    expect(screen.queryByRole('button', { name: /delete|edit/i })).not.toBeInTheDocument()
  })

  it('refuses a start date after the end date without asking the server', async () => {
    const { api, audit } = serve(auditor)
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Audit' }))
    await screen.findByText('v50')
    await user.type(screen.getByLabelText('From date'), '2026-10-05')
    await user.type(screen.getByLabelText('To date'), '2026-10-01')
    await user.click(screen.getByRole('button', { name: 'Search' }))
    expect(screen.getByRole('alert')).toHaveTextContent('The start date is after the end date.')
    expect(audit).toHaveLength(1)
  })

  it('hides the tab without audit.read', async () => {
    render(<App api={serve({ ...auditor, permissions: ['customers.read'] }).api} />)
    expect(await screen.findByRole('button', { name: 'Customers' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Audit' })).not.toBeInTheDocument()
  })
})
