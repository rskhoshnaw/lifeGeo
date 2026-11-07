import { routeView } from './routeShifts.ts'
import type { RouteHistory, RoutePoint } from '../api/admin.ts'

const p = (at: string, shiftId?: string): RoutePoint => ({ latitude: 36.19, longitude: 44, accuracyMeters: 8, capturedAt: at, isMocked: false, shiftId })
const base = (over: Partial<RouteHistory>): RouteHistory => ({
  userId: 'u', email: null, date: '2026-11-07', truncated: false, stops: [], events: [],
  shifts: [
    { shiftId: 's1', startedAt: '2026-11-07T05:00:00Z', endedAt: '2026-11-07T06:00:00Z', pointCount: 2, pointsPurged: false },
    { shiftId: 's2', startedAt: '2026-11-07T07:00:00Z', endedAt: null, pointCount: 2, pointsPurged: false },
  ],
  points: [p('2026-11-07T05:10:00Z', 's1'), p('2026-11-07T05:50:00Z', 's1'), p('2026-11-07T07:10:00Z', 's2'), p('2026-11-07T07:20:00Z', 's2')],
  ...over,
})

describe('shifts of a day (plan step 5.24)', () => {
  it('shows every shift as its own line when they are shown together, and one shift alone when chosen', () => {
    const route = base({})
    const all = routeView(route, '')
    expect(all.points).toHaveLength(4)
    expect(all.groups.map((g) => [g.label, g.points.length])).toEqual([[1, 2], [2, 2]])
    expect(new Set(all.groups.map((g) => g.color)).size).toBe(2)
    const second = routeView(route, 's2')
    expect(second.points).toHaveLength(2)
    expect(second.groups.map((g) => g.id)).toEqual(['s2'])
  })

  it('places stops by their shift and check-ins by time', () => {
    const route = base({
      stops: [
        { from: '2026-11-07T05:20:00Z', to: '2026-11-07T05:30:00Z', seconds: 600, latitude: 1, longitude: 1, customerName: null, customerDistanceMeters: null, shiftId: 's1' },
        { from: '2026-11-07T07:12:00Z', to: '2026-11-07T07:19:00Z', seconds: 420, latitude: 1, longitude: 1, customerName: null, customerDistanceMeters: null, shiftId: 's2' },
      ],
      events: [{ kind: 'check_in', accepted: true, rejectionReason: null, capturedAt: '2026-11-07T07:15:00Z', latitude: 1, longitude: 1, customerName: 'Shifa', radiusMeters: 50, isMocked: false }],
    })
    expect(routeView(route, 's1').stops).toHaveLength(1)
    expect(routeView(route, 's1').events).toHaveLength(0)
    expect(routeView(route, 's2').events).toHaveLength(1)
    expect(routeView(route, '').stops).toHaveLength(2)
  })

  it('uses the time of the shift when an older server sends no shift id on the points', () => {
    const route = base({ points: [p('2026-11-07T05:10:00Z'), p('2026-11-07T07:10:00Z'), p('2026-11-07T07:20:00Z')] })
    expect(routeView(route, 's1').points).toHaveLength(1)
    expect(routeView(route, 's2').points).toHaveLength(2)
  })

  it('keeps a day with one shift exactly as it was', () => {
    const route = base({ shifts: [{ shiftId: 's1', startedAt: '2026-11-07T05:00:00Z', endedAt: null, pointCount: 4, pointsPurged: false }] })
    const view = routeView(route, '')
    expect(view.points).toBe(route.points)
    expect(view.groups).toHaveLength(1)
  })
})
