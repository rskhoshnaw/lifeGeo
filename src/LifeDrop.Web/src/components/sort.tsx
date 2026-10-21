import { useMemo, useState, type ReactNode } from 'react'

type Dir = 'asc' | 'desc'
export type SortValue = string | number | boolean | null | undefined

// Click-to-sort for table headers: first click ascending, second descending, third back to the original order.
export function useSort<T>(rows: T[] | null | undefined, getters: Record<string, (row: T) => SortValue>) {
  const [sort, setSort] = useState<{ key: string; dir: Dir } | null>(null)
  const sorted = useMemo(() => {
    if (!rows || !sort || !getters[sort.key]) return rows
    const get = getters[sort.key]
    const sign = sort.dir === 'asc' ? 1 : -1
    return [...rows].sort((a, b) => {
      const x = get(a)
      const y = get(b)
      if (x == null || x === '') return y == null || y === '' ? 0 : 1 // empty values always last
      if (y == null || y === '') return -1
      if (typeof x === 'number' && typeof y === 'number') return (x - y) * sign
      return String(x).localeCompare(String(y), undefined, { numeric: true, sensitivity: 'base' }) * sign
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, sort])
  function toggle(key: string) {
    setSort((s) => (s?.key !== key ? { key, dir: 'asc' } : s.dir === 'asc' ? { key, dir: 'desc' } : null))
  }
  return { rows: sorted, sort, toggle }
}

export function SortTh({ id, sort, onSort, className, children }: { id: string; sort: { key: string; dir: Dir } | null; onSort: (key: string) => void; className?: string; children: ReactNode }) {
  const active = sort?.key === id
  return (
    <th className={className} aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none'}>
      <button type="button" className="font-semibold hover:underline" onClick={() => onSort(id)}>
        {children}
        <span aria-hidden="true" className="ms-1 text-xs">{active ? (sort.dir === 'asc' ? '▲' : '▼') : '↕'}</span>
      </button>
    </th>
  )
}
