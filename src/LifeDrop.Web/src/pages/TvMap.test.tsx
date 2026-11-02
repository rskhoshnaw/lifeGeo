import { render, screen } from '@testing-library/react'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import type { LiveMapEntry, Me } from '../api/admin.ts'
import { isTvOnly } from './TvPage.tsx'

// WebGL is not available in jsdom; the map view is replaced by a list of the markers it would draw.
vi.mock('../components/LiveMapView.tsx', () => ({
  LiveMapView: ({ entries }: { entries: LiveMapEntry[] }) => (
    <ul aria-label="Map markers">
      {entries.filter((e) => e.latitude !== null).map((e) => <li key={e.userId}>{e.email}</li>)}
    </ul>
  ),
}))

const entry: LiveMapEntry = {
  userId: 'u1', email: 'rep-a@t1', teamName: 'Team A', shiftStartedAt: '2026-10-05T05:00:00Z', lastUpdateAt: '2026-10-05T07:59:00Z',
  isLive: true, precise: true, latitude: 36.19, longitude: 44.01, accuracyMeters: 12, isMocked: false, lastCustomerName: 'Shifa', lastVisitAt: '2026-10-05T07:40:00Z',
}
const me = (roles: string[], permissions: string[]): Me => ({ id: 'tv', email: 'tv@t1', roles, permissions })

function serve(account: Me) {
  const fetcher = (async (input: RequestInfo | URL) => {
    const path = String(input).replace('https://api.test', '')
    const body = path === '/auth/refresh' ? { accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' } : path === '/me' ? account : [entry]
    return new Response(JSON.stringify(body), { status: path === '/live-map' || path === '/me' || path === '/auth/refresh' ? 200 : 404 })
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: account.email }))
  return new ApiClient('https://api.test', fetcher, sessionStorage)
}

describe('TV screen account', () => {
  it('knows an account that only has the TV role', () => {
    expect(isTvOnly(me(['TvDisplay'], ['tracking.read']))).toBe(true)
    expect(isTvOnly(me(['TvDisplay', 'SalesManager'], ['tracking.read']))).toBe(false)
    expect(isTvOnly(me([], []))).toBe(false)
  })

  it('opens straight on the live map, with no totals switch', async () => {
    render(<App api={serve(me(['TvDisplay'], ['tracking.read']))} />)
    expect(await screen.findByRole('heading', { name: 'Live map' })).toBeInTheDocument()
    expect(await screen.findByRole('list', { name: 'Map markers' })).toHaveTextContent('rep-a@t1')
    expect(screen.queryByRole('button', { name: 'Totals' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Full screen' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'LifeDrop' })).not.toBeInTheDocument()   // a screen account has no side menu
  })
})
