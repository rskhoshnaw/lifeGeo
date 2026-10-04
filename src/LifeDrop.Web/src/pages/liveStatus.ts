import type { LiveMapEntry } from '../api/admin.ts'

// Status text for one user on shift; a point older than 5 minutes (isLive false, set by the server) is never "Live".
export function status(entry: LiveMapEntry, now: number = Date.now()): string {
  if (!entry.lastUpdateAt) return 'No position yet'
  if (entry.isLive) return 'Live'
  const minutes = Math.max(1, Math.round((now - Date.parse(entry.lastUpdateAt)) / 60_000))
  return `Not live (last update ${minutes} min ago)`
}
