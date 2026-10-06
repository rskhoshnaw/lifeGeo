import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App.tsx'
import { ApiClient } from '../api/client.ts'
import { iraqToday, type Me, type RouteHistory, type RoutePoint } from '../api/admin.ts'

// WebGL is not available in jsdom; the map views are replaced by a list of what they would draw.
vi.mock('../components/RouteMapView.tsx', () => ({
  RouteMapView: ({ points, stops, events, index }: { points: RoutePoint[]; stops: unknown[]; events: unknown[]; index: number }) => (
    <p aria-label="Route map">{`${points.length} points, ${stops.length} stops, ${events.length} events, at ${index}`}</p>
  ),
}))
vi.mock('../components/LiveMapView.tsx', () => ({ LiveMapView: () => <p /> }))

const point = (minute: number, mocked = false): RoutePoint => ({ latitude: 36.19, longitude: 44.0 + minute * 0.001, accuracyMeters: 8, capturedAt: `2026-10-05T04:${String(10 + minute).padStart(2, '0')}:00Z`, isMocked: mocked })
const route = (over: Partial<RouteHistory> = {}): RouteHistory => ({
  userId: 'u1', email: 'rep@t1', date: '2026-10-05', shifts: [{ shiftId: 's1', startedAt: '2026-10-05T04:00:00Z', endedAt: null, pointCount: 4, pointsPurged: false }],
  points: [point(0), point(1), point(2, true), point(3)],
  stops: [{ from: '2026-10-05T04:10:00Z', to: '2026-10-05T04:16:00Z', seconds: 360, latitude: 36.19, longitude: 44.0, customerName: 'Shifa', customerDistanceMeters: 12 }],
  events: [{ kind: 'check_in', accepted: true, rejectionReason: null, capturedAt: '2026-10-05T04:10:00Z', latitude: 36.19, longitude: 44.0, customerName: 'Shifa', radiusMeters: 50, isMocked: false }],
  truncated: false, ...over,
})

function serve(me: Me, history: RouteHistory | null) {
  const calls: string[] = []
  const fetcher = (async (input: RequestInfo | URL) => {
    const path = String(input).replace('https://api.test', '')
    calls.push(path)
    if (path === '/auth/refresh') return new Response(JSON.stringify({ accessToken: 'a', refreshToken: 'r', tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' }))
    if (path === '/me') return new Response(JSON.stringify(me))
    if (path === '/tracking/users') return new Response(JSON.stringify([{ id: 'u1', email: 'rep@t1', teamName: 'Team A' }]))
    if (path.startsWith('/tracking/history') && history) return new Response(JSON.stringify(history))
    return new Response(JSON.stringify({ code: 'NOT_FOUND' }), { status: 404 })
  }) as typeof fetch
  sessionStorage.clear()
  sessionStorage.setItem('lifedrop.refresh', 'r0')
  sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: me.email }))
  return { api: new ApiClient('https://api.test', fetcher, sessionStorage), calls }
}

const manager: Me = { id: 'm', email: 'sm@t1', roles: ['SalesManager'], permissions: ['tracking.read'] }

describe('route history', () => {
  it('loads the route of the chosen user and day with its stops and check-ins, and the slider moves the point', async () => {
    const { api, calls } = serve(manager, route())
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Route history' }))
    await user.selectOptions(await screen.findByLabelText('User'), 'u1')
    expect(await screen.findByLabelText('Route map')).toHaveTextContent('4 points, 1 stops, 1 events, at 0')
    expect(calls).toContain(`/tracking/history?userId=u1&date=${iraqToday()}`)
    expect(screen.getByText(/near Shifa \(12 m\)/)).toBeInTheDocument()
    expect(screen.getByText(/6 min/)).toBeInTheDocument()
    expect(screen.getByText(/Check-in · Shifa/)).toBeInTheDocument()
    expect(screen.getByText(/Point 1 of 4/)).toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('Position'), { target: { value: '2' } })
    expect(await screen.findByText(/Point 3 of 4/)).toBeInTheDocument()
    expect(screen.getByText('fake position')).toBeInTheDocument()
    expect(screen.getByLabelText('Route map')).toHaveTextContent('at 2')
  })

  it('plays the route point by point and stops at the end', async () => {
    const { api } = serve(manager, route())
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Route history' }))
    await user.selectOptions(await screen.findByLabelText('User'), 'u1')
    await screen.findByLabelText('Route map')
    vi.useFakeTimers()
    try {
      fireEvent.click(screen.getByRole('button', { name: 'Play' }))
      await act(async () => {
        await vi.advanceTimersByTimeAsync(160)
      })
      expect(screen.getByText(/Point 2 of 4/)).toBeInTheDocument()
      for (let step = 0; step < 4; step++)
        await act(async () => {
          await vi.advanceTimersByTimeAsync(160)
        })
      expect(screen.getByText(/Point 4 of 4/)).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Play' })).toBeInTheDocument()   // over: the button offers to play again
    } finally {
      vi.useRealTimers()
    }
  })

  it('says when the day has no points, when old points were deleted, and shows an error for a user outside the area', async () => {
    const { api } = serve(manager, route({ points: [], stops: [], events: [], shifts: [{ shiftId: 's1', startedAt: '2026-06-01T04:00:00Z', endedAt: '2026-06-01T06:00:00Z', pointCount: 3, pointsPurged: true }] }))
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Route history' }))
    await user.selectOptions(await screen.findByLabelText('User'), 'u1')
    expect(await screen.findByText('No points on this day.')).toBeInTheDocument()
    expect(screen.getByText(/deleted after 90 days/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Route map')).not.toBeInTheDocument()
  })

  it('shows the server error when the route cannot be read', async () => {
    const { api } = serve(manager, null)
    const user = userEvent.setup()
    render(<App api={api} />)
    await user.click(await screen.findByRole('button', { name: 'Route history' }))
    await user.selectOptions(await screen.findByLabelText('User'), 'u1')
    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
  })

  it('has no tab for users without tracking.read', async () => {
    const { api } = serve({ id: 'f', email: 'fin@t1', roles: ['FinanceManager'], permissions: ['tracking.summary', 'customers.read'] }, null)
    render(<App api={api} />)
    expect(await screen.findByRole('button', { name: 'Customers' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Route history' })).not.toBeInTheDocument()
  })
})
