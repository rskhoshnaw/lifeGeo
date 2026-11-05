import { describe, expect, it } from 'vitest'
import type { LiveMapEntry } from '../api/admin.ts'
import { contactAt, leftOpen, problems, status } from './liveStatus.ts'

const base: LiveMapEntry = {
  userId: 'u', email: null, teamName: null, shiftStartedAt: '2026-11-05T05:00:00Z', lastUpdateAt: '2026-11-05T05:01:00Z',
  isLive: true, precise: true, latitude: 1, longitude: 1, accuracyMeters: 5, isMocked: false, lastCustomerName: null, lastVisitAt: null
}
const health = { appVersion: '1', platform: 'android', osVersion: null, deviceModel: null, locationPermission: 'always', preciseLocation: true,
  locationServices: true, notifications: true, batteryExempt: true, backgroundRefresh: null, batteryPercent: 80, lowPower: false }

describe('live status', () => {
  it('uses the later of position and heartbeat', () => {
    expect(contactAt({ ...base, lastSeenAt: '2026-11-05T05:09:00Z' })).toBe('2026-11-05T05:09:00Z')
    expect(contactAt({ ...base, lastUpdateAt: null, lastSeenAt: null })).toBeNull()
  })
  it('reports stale minutes from the last contact', () => {
    const s = status({ ...base, isLive: false, lastSeenAt: '2026-11-05T05:10:00Z' }, Date.parse('2026-11-05T05:25:00Z'))
    expect(s).toContain('15')
  })
  it('lists missing permissions only, unknown is fine', () => {
    expect(problems(base)).toEqual([])
    expect(problems({ ...base, health })).toEqual([])
    expect(problems({ ...base, health: { ...health, locationPermission: 'when_in_use', batteryExempt: false } }))
      .toEqual(['web.map.problem.notAlways', 'web.map.problem.battery'])
    expect(problems({ ...base, health: { ...health, platform: 'ios', batteryExempt: false, backgroundRefresh: 'denied', locationPermission: 'denied' } }))
      .toEqual(['web.map.problem.denied', 'web.map.problem.refresh'])
  })
  it('flags shifts open for more than 14 hours', () => {
    expect(leftOpen(base, Date.parse('2026-11-06T00:00:00Z'))).toBe(true)
    expect(leftOpen(base, Date.parse('2026-11-05T10:00:00Z'))).toBe(false)
  })
})
