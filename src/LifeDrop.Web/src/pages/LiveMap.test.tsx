import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import type { LiveMapEntry, Me } from '../api/admin.ts'
import { status } from './liveStatus.ts'

// WebGL is not available in jsdom; the map view is replaced by a list of the markers it would draw.
vi.mock('../components/LiveMapView.tsx', () => ({
  LiveMapView: ({ entries }: { entries: LiveMapEntry[] }) => (
    <ul aria-label="Map markers">
      {entries.filter((e) => e.latitude !== null).map((e) => (
        <li key={e.userId}>{`${e.email} ${e.isLive ? 'live' : 'stale'}`}</li>
      ))}
    </ul>
  ),
}))

const entry = (over: Partial<LiveMapEntry>): LiveMapEntry => ({
  userId: 'u1', email: 'rep-a@t1', teamName: 'Team A', shiftStartedAt: '2026-10-05T05:00:00Z', lastUpdateAt: '2026-10-05T07:59:00Z',
  isLive: true, precise: true, latitude: 36.19, longitude: 44.01, accuracyMeters: 12, isMocked: false, lastCustomerName: 'Shifa',
  lastVisitAt: '2026-10-05T07:40:00Z', ...over,
})

function serve(me: Me, map: LiveMapEntry[]) {
  const fetcher = (async (input: RequestInfo | URL) => {
    const path = String(input).replace('https://api.test', '')
    const body = path === '/auth/refresh' ? { accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' } : path === '/me' ? me : map
    return new Response(JSON.stringify(body), { status: path === '/live-map' || path === '/me' || path === '/auth/refresh' ? 200 : 404 })
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return new ApiClient('https://api.test', fetcher, sessionStorage)
}

describe('live map', () => {
  it('never calls an old point live', () => {
    const now = Date.parse('2026-10-05T08:00:00Z')
    expect(status(entry({}), now)).toBe('Live')
    expect(status(entry({ isLive: false, lastUpdateAt: '2026-10-05T07:40:00Z' }), now)).toBe('Not live (last update 20 min ago)')
    expect(status(entry({ isLive: false, lastUpdateAt: null }), now)).toBe('No position yet')
  })

  it('shows markers for precise entries, live and stale', async () => {
    const api = serve({ id: 'm', email: 'manager@t1', roles: ['SalesManager'], permissions: ['tracking.read'] }, [
      entry({}),
      entry({ userId: 'u2', email: 'rep-b@t1', isLive: false, lastUpdateAt: '2026-10-05T07:40:00Z', isMocked: true }),
    ])
    render(<App api={api} />)
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Live map' }))
    const markers = await screen.findByRole('list', { name: 'Map markers' })
    expect(markers).toHaveTextContent('rep-a@t1 live')
    expect(markers).toHaveTextContent('rep-b@t1 stale')
    expect(screen.getByText('mock location')).toBeInTheDocument()
  })

  it('shows status only, without a map, for a summary-only role', async () => {
    const api = serve({ id: 'f', email: 'finance@t1', roles: ['FinanceManager'], permissions: ['tracking.summary'] }, [
      entry({ precise: false, latitude: null, longitude: null, accuracyMeters: null, isMocked: null }),
    ])
    render(<App api={api} />)
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Live map' }))
    expect(await screen.findByText('Positions are not shown for your role; status only.')).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Map markers' })).not.toBeInTheDocument()
    expect(screen.getByText('Shifa (10:40)')).toBeInTheDocument()
  })

  it('hides the tab without tracking permissions', async () => {
    render(<App api={serve({ id: 'x', email: 'x@t1', roles: [], permissions: ['customers.read'] }, [])} />)
    expect(await screen.findByRole('button', { name: 'Customers' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Live map' })).not.toBeInTheDocument()
  })
})
