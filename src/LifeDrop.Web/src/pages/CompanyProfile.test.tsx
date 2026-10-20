import { render, screen } from '@testing-library/react'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { setTextOverrides } from '../i18n.ts'
import type { Me } from '../api/admin.ts'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

// The panel follows the profile the server sends before sign-in (plan step 4.48): parts the company switched off have no tab, its own wording replaces the built-in text.
describe('company profile in the panel (plan step 4.48)', () => {
  afterEach(() => setTextOverrides(null))

  it('leaves out the tabs of hidden modules and uses the own wording', async () => {
    const me: Me = { id: 'm', email: 'manager@t1', roles: ['SalesManager'], permissions: ['customers.read', 'payments.read', 'samples.read', 'orders.read'] }
    const fetcher = (async (input: RequestInfo | URL) => {
      const path = String(input).replace('https://api.test', '')
      if (path === '/auth/refresh') return json({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' })
      if (path === '/me') return json(me)
      if (path.startsWith('/company-config/')) return json({ tenantCode: 'T1', name: 'T1', industry: 'pharma', hiddenModules: ['payments', 'samples'], texts: { 'web.tab.customers': { en: 'My shops' } } })
      return json([])
    }) as typeof fetch
    sessionStorage.clear()
    sessionStorage.setItem('lifedrop.refresh', 'r0')
    sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
    render(<App api={new ApiClient('https://api.test', fetcher, sessionStorage)} />)

    expect(await screen.findByRole('button', { name: 'My shops' })).toBeInTheDocument()   // own wording
    expect(screen.getByRole('button', { name: 'Orders' })).toBeInTheDocument()            // not switched off
    expect(screen.queryByRole('button', { name: 'Payments' })).toBeNull()                 // switched off
    expect(screen.queryByRole('button', { name: 'Samples' })).toBeNull()
  })
})
