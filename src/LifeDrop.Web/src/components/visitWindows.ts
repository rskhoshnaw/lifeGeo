import type { VisitWindow } from '../api/admin.ts'

// Same rules as the server (plan step 1.23): closing after opening, no overlap inside one day, at most 21 rows.
export function windowsAreValid(windows: VisitWindow[]): boolean {
  if (windows.length > 21) return false
  if (windows.some((w) => w.weekday < 0 || w.weekday > 6 || w.opensAt >= w.closesAt)) return false
  for (let day = 0; day < 7; day++) {
    const sorted = windows.filter((w) => w.weekday === day).sort((a, b) => a.opensAt.localeCompare(b.opensAt))
    if (sorted.some((w, i) => i > 0 && w.opensAt < sorted[i - 1]!.closesAt)) return false
  }
  return true
}
