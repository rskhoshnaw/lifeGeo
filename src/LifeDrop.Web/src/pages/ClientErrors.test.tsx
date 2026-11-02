import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { clientErrorsQuery, type ClientError, type Me } from '../api/admin.ts'

const manager: Me = { id: 'm', email: 'manager@t1', roles: ['CompanyManager'], permissions: ['client_errors.read'] }
const error = (i: number, stage: ClientError['stage'] = 'company_lookup'): ClientError => ({
  id: `id-${i}`, reportedAt: new Date(Date.UTC(2026, 10, 1, 12, 0, 0) - i * 60_000).toISOString(), occurredAt: new Date(Date.UTC(2026, 10, 1, 11, 0, 0) - i * 60_000).toISOString(),
  stage, errorKind: 'dns', errorType: 'HttpRequestException', message: `No such host is known ${i}`, enteredCompanyCode: 'Lifedrops', enteredEmail: 'panar@t1',
  deviceId: 'abcdef123456', appVersion: '1.0.0', platform: 'android',
})

function serve(me: Me) {
  const calls: string[] = []
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input).replace('https://api.test', '')
    if ((init?.method ?? 'GET') !== 'GET' && path !== '/auth/refresh') throw new Error(`unexpected ${init?.method} ${path}`)
    if (path === '/auth/refresh') return new Response(JSON.stringify({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' }))
    if (path === '/me') return new Response(JSON.stringify(me))
    if (path === '/notifications/unread-count') return new Response(JSON.stringify({ count: 0 }))
    if (path.startsWith('/company-config/')) return new Response('', { status: 404 })
    calls.push(path)
    if (path.includes('stage=login')) return new Response(JSON.stringify([error(9, 'login')]))
    return new Response(JSON.stringify(calls.length === 1 ? Array.from({ length: 50 }, (_, i) => error(i + 1)) : [error(51)]))
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return { api: new ApiClient('https://api.test', fetcher, sessionStorage), calls }
}

describe('connection errors of the app', () => {
  it('builds the query with the stage and the cursor of the last row', () => {
    expect(clientErrorsQuery()).toBe('/client-errors?limit=50')
    expect(clientErrorsQuery('login', error(3))).toBe(`/client-errors?limit=50&stage=login&cursorReportedAt=${encodeURIComponent(error(3).reportedAt)}&cursorId=id-3`)
  })

  it('lists the typed company code and e-mail, pages and filters by step', async () => {
    const { api, calls } = serve(manager)
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Connection errors' }))
    expect(await screen.findByText(/No such host is known 50/)).toBeInTheDocument()
    expect(screen.getAllByText('Lifedrops')).toHaveLength(50)
    await user.click(screen.getByRole('button', { name: 'Load more' }))
    expect(await screen.findByText(/No such host is known 51/)).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Step'), 'login')
    expect(await screen.findByText(/No such host is known 9/)).toBeInTheDocument()
    expect(calls).toEqual([
      '/client-errors?limit=50',
      `/client-errors?limit=50&cursorReportedAt=${encodeURIComponent(error(50).reportedAt)}&cursorId=id-50`,
      '/client-errors?limit=50&stage=login',
    ])
  })

  it('is hidden without the permission', async () => {
    const { api } = serve({ ...manager, permissions: ['audit.read'] })
    render(<App api={api} />)
    expect(await screen.findByRole('button', { name: 'Audit' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Connection errors' })).not.toBeInTheDocument()
  })
})
