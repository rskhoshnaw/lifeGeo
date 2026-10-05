import type { LiveMapEntry } from '../api/admin.ts'
import { t } from '../i18n.ts'

// Status text for one user on shift; a point older than 5 minutes (isLive false, set by the server) is never "Live".
export function status(entry: LiveMapEntry, now: number = Date.now()): string {
  if (!entry.lastUpdateAt) return t('web.map.noPosition')
  if (entry.isLive) return t('web.map.live')
  const minutes = Math.max(1, Math.round((now - Date.parse(entry.lastUpdateAt)) / 60_000))
  return t('web.map.stale', { minutes })
}
