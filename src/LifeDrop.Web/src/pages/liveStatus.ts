import type { LiveMapEntry } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'

// Last time we heard from the phone: a position or a heartbeat (a still phone sends no new position).
export function contactAt(entry: LiveMapEntry): string | null {
  const a = entry.lastUpdateAt ?? null
  const b = entry.lastSeenAt ?? null
  if (a && b) return Date.parse(a) >= Date.parse(b) ? a : b
  return a ?? b
}

// Status text for one user on shift; no contact within the live window (isLive false, set by the server) is never "Live".
export function status(entry: LiveMapEntry, now: number = Date.now()): string {
  const contact = contactAt(entry)
  if (!contact) return t('web.map.noPosition')
  if (entry.isLive) return t('web.map.live')
  const minutes = Math.max(1, Math.round((now - Date.parse(contact)) / 60_000))
  return t('web.map.stale', { minutes })
}

// What is wrong with the phone, as translation keys; empty when all is well or nothing was reported.
export function problems(entry: LiveMapEntry): TextKey[] {
  const h = entry.health
  if (!h) return []
  const out: TextKey[] = []
  if (h.locationServices === false) out.push('web.map.problem.services')
  if (h.locationPermission && h.locationPermission !== 'always' && h.locationPermission !== 'unknown')
    out.push(h.locationPermission === 'denied' ? 'web.map.problem.denied' : 'web.map.problem.notAlways')
  if (h.preciseLocation === false) out.push('web.map.problem.approx')
  if (h.notifications === false) out.push('web.map.problem.notifications')
  if (h.platform === 'android' && h.batteryExempt === false) out.push('web.map.problem.battery')
  if (h.platform === 'ios' && (h.backgroundRefresh === 'denied' || h.backgroundRefresh === 'restricted')) out.push('web.map.problem.refresh')
  return out
}

// A shift open far longer than any workday was most likely forgotten.
export function leftOpen(entry: LiveMapEntry, now: number = Date.now()): boolean {
  return now - Date.parse(entry.shiftStartedAt) > 14 * 3_600_000
}
