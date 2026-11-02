import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import type { LiveMapEntry, Me } from '../api/admin.ts'

// WebGL is not available in jsdom; the map view is replaced by a line that shows which person it was told to focus on.
vi.mock('../components/LiveMapView.tsx', () => ({
  LiveMapView: ({ focus }: { focus: { userId: string; seq: number } | null }) => <p aria-label="Map focus">{focus ? `${focus.userId}#${focus.seq}` : 'none'}</p>,
}))

const entry = (over: Partial<LiveMapEntry>): LiveMapEntry => ({
  userId: 'u1', email: 'rep-a@t1', teamName: 'Team A', shiftStartedAt: '2026-10-05T05:00:00Z', lastUpdateAt: '2026-10-05T07:59:00Z',
  isLive: true, precise: true, latitude: 36.19, longitude: 44.01, accuracyMeters: 12, isMocked: false, lastCustomerName: null, lastVisitAt: null, ...over,
})

function serve(entries: LiveMapEntry[]) {
  const me: Me = { id: 'm', email: 'manager@t1', roles: ['SalesManager'], permissions: ['tracking.read'] }
  const fetcher = (async (input: RequestInfo | URL) => {
    const path = String(input).replace('https://api.test', '')
    const body = path === '/auth/refresh' ? { accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' } : path === '/me' ? me : entries
    return new Response(JSON.stringify(body), { status: path === '/live-map' || path === '/me' || path === '/auth/refresh' ? 200 : 404 })
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return new ApiClient('https://api.test', fetcher, sessionStorage)
}

describe('find a person on the live map', () => {
  it('a click on a name tells the map to focus on that person, each click again', async () => {
    render(<App api={serve([entry({}), entry({ userId: 'u2', email: 'rep-b@t1' })])} />)
    const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Live map' }))
    expect(await screen.findByLabelText('Map focus')).toHaveTextContent('none')
    await user.click(screen.getByRole('button', { name: 'Show rep-b@t1 on the map' }))
    expect(screen.getByLabelText('Map focus')).toHaveTextContent('u2#1')
    await user.click(screen.getByRole('button', { name: 'Show rep-b@t1 on the map' }))
    expect(screen.getByLabelText('Map focus')).toHaveTextContent('u2#2')
  })

  it('a person without a position has no button', async () => {
    render(<App api={serve([entry({}), entry({ userId: 'u3', email: 'rep-c@t1', latitude: null, longitude: null, lastUpdateAt: null, isLive: false })])} />)
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Live map' }))
    await screen.findByLabelText('Map focus')
    expect(screen.getByRole('button', { name: 'Show rep-a@t1 on the map' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Show rep-c@t1 on the map' })).not.toBeInTheDocument()
    expect(screen.getByText('rep-c@t1')).toBeInTheDocument()
  })
})
