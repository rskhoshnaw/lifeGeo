// How close a product's expiry day is: expired (before today), soon (within 90 days, the limit of the stock warning) or fine.
export type ExpiryStatus = 'none' | 'ok' | 'soon' | 'expired'

export function expiryStatus(expiryDate: string | null | undefined, today: Date = new Date()): ExpiryStatus {
  if (!expiryDate) return 'none'
  const day = new Date(`${expiryDate.slice(0, 10)}T00:00:00`)
  if (Number.isNaN(day.getTime())) return 'none'
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate())
  const limit = new Date(start)
  limit.setDate(limit.getDate() + 90)
  return day < start ? 'expired' : day <= limit ? 'soon' : 'ok'
}

export interface ProductFilters {
  text: string
  specialty: string
  manufacturer: string
  stock: '' | 'in' | 'out'
}

// The gallery filters, applied in the browser to the list the server sent (text: code, name or scientific name).
export function filterProducts<T extends { code: string; name: string; scientificName?: string | null; specialty?: string | null; manufacturer?: string | null; inStock?: boolean }>(items: T[], f: ProductFilters): T[] {
  const q = f.text.trim().toLowerCase()
  return items.filter(
    (p) =>
      (!q || [p.code, p.name, p.scientificName].some((v) => v?.toLowerCase().includes(q))) &&
      (!f.specialty || p.specialty === f.specialty) &&
      (!f.manufacturer || (p.manufacturer ?? '').toLowerCase() === f.manufacturer.toLowerCase()) &&
      (!f.stock || (f.stock === 'in') === !!p.inStock),
  )
}
