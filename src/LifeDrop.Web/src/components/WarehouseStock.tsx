import { useCallback, useMemo, useState } from 'react'
import { AdminApi, type WarehouseStockRow } from '../api/admin.ts'
import { expiryStatus } from '../pages/productView.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'
import { unitLabel } from '../pages/productView.ts'

const number = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })

interface WarehouseSummary {
  id: string
  code: string
  name: string
  city: string | null
  rows: WarehouseStockRow[]
  units: number
  counted: number
  empty: number
}

// Stock per warehouse and per product (owner request 2026-11-13): the managers see every warehouse, a warehouse worker only his own (the server decides).
// Each warehouse has its own row of figures; "edit" opens its products so the quantities can be counted and saved. Below, every product with the total of all warehouses.
export function WarehouseStock({ admin, canEdit }: { admin: AdminApi; canEdit: boolean }) {
  const stock = useLoad(useCallback(() => admin.warehouseStock(), [admin]))
  const action = useAction(stock.reload)
  const [open, setOpen] = useState<string | null>(null)
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  // The products of the opened warehouse as cards with photos or as a table (owner request 2026-11-26); the photo comes with every stock row.
  const [view, setView] = useState<'gallery' | 'table'>('gallery')
  const [find, setFind] = useState('')
  const [onlyOpen, setOnlyOpen] = useState(false)   // only the products not counted yet
  const warehouses = useMemo(() => {
    const map = new Map<string, WarehouseSummary>()
    for (const r of stock.data ?? []) {
      const w = map.get(r.warehouseId) ?? { id: r.warehouseId, code: r.warehouseCode, name: r.warehouseName, city: r.cityName, rows: [], units: 0, counted: 0, empty: 0 }
      w.rows.push(r)
      w.units += r.quantity
      if (r.counted) w.counted += 1
      if (r.counted && r.quantity === 0) w.empty += 1
      map.set(r.warehouseId, w)
    }
    return [...map.values()]
  }, [stock.data])

  const products = useMemo(() => {
    const map = new Map<string, { id: string; code: string; name: string; unit: string; total: number; where: string[] }>()
    for (const r of stock.data ?? []) {
      const p = map.get(r.productId) ?? { id: r.productId, code: r.productCode, name: r.productName, unit: r.unit, total: 0, where: [] }
      p.total += r.quantity
      if (r.quantity > 0) p.where.push(`${r.cityName ?? r.warehouseName}: ${number(r.quantity)}`)
      map.set(r.productId, p)
    }
    return [...map.values()]
  }, [stock.data])

  // Warnings: counted stock at or below the product's limit, and products that expire within 90 days (by the product's expiry day).
  const low = (stock.data ?? []).filter((r) => r.counted && r.minStock != null && r.quantity <= r.minStock)
  const soonLimit = new Date(Date.now() + 90 * 86_400_000).toISOString().slice(0, 10)
  const expiring = [...new Map((stock.data ?? []).filter((r) => r.expiryDate && r.expiryDate <= soonLimit).map((r) => [r.productId, r])).values()]

  const draftOf = (r: WarehouseStockRow) => drafts[`${r.warehouseId}:${r.productId}`] ?? String(r.quantity)
  const save = (r: WarehouseStockRow) =>
    action.run(async () => {
      const quantity = Number(draftOf(r))
      await admin.setWarehouseStock(r.warehouseId, r.productId, quantity)
      setDrafts((d) => {
        const next = { ...d }
        delete next[`${r.warehouseId}:${r.productId}`]
        return next
      })
    })

  if (stock.error) return <p role="alert" className="text-sm text-red-700">{stock.error}</p>
  if (!stock.data) return null
  const current = warehouses.find((w) => w.id === open)
  const q = find.trim().toLowerCase()
  const shownRows = (current?.rows ?? []).filter((r) => (!q || r.productCode.toLowerCase().includes(q) || r.productName.toLowerCase().includes(q)) && (!onlyOpen || !r.counted))

  return (
    <div className="space-y-3 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.stock.title')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {warehouses.length === 0 && <p className="text-slate-600">{t('web.stock.none')}</p>}
      {low.length > 0 && (
        <div role="status" className="rounded border border-amber-400 bg-amber-50 p-2">
          <strong>{t('web.stock.low')}</strong>
          <ul>
            {low.map((r) => (
              <li key={`${r.warehouseId}:${r.productId}`}>{r.warehouseName}: {r.productCode} · {r.productName} — {number(r.quantity)} {unitLabel(r.unit)}</li>
            ))}
          </ul>
        </div>
      )}
      {expiring.length > 0 && (
        <div role="status" className="rounded border border-red-400 bg-red-50 p-2">
          <strong>{t('web.stock.expiring')}</strong>
          <ul>
            {expiring.map((r) => (
              <li key={r.productId}>{r.productCode} · {r.productName} — {r.expiryDate}</li>
            ))}
          </ul>
        </div>
      )}
      {warehouses.length > 0 && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-1">{t('web.stock.warehouse')}</th>
              <th>{t('web.stock.city')}</th>
              <th>{t('web.stock.counted')}</th>
              <th>{t('web.stock.units')}</th>
              <th>{t('web.stock.empty')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {warehouses.map((w) => (
              <tr key={w.id} className="border-b">
                <td className="py-1">{w.code} · {w.name}</td>
                <td>{w.city ?? '—'}</td>
                <td>{w.counted} / {w.rows.length}</td>
                <td>{number(w.units)}</td>
                <td>{w.empty}</td>
                <td className="text-end">
                  <button className="rounded border px-2 py-1" aria-label={t('web.stock.open', { name: w.name })} onClick={() => setOpen(open === w.id ? null : w.id)}>
                    {t(canEdit ? 'web.stock.edit' : 'web.stock.view')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {current && (
        <div className="space-y-3 rounded border bg-slate-50 p-3">
          <div className="flex flex-wrap items-center gap-3">
            <h4 className="font-semibold">{current.code} · {current.name}{current.city ? ` · ${current.city}` : ''}</h4>
            <input aria-label={t('web.catalog.search')} placeholder={t('web.catalog.search')} className="rounded border px-2 py-1" value={find} onChange={(e) => setFind(e.target.value)} />
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={onlyOpen} onChange={(e) => setOnlyOpen(e.target.checked)} />
              {t('web.stock.onlyNotCounted')}
            </label>
            <div className="inline-flex overflow-hidden rounded border" role="group">
              {(['gallery', 'table'] as const).map((v) => (
                <button key={v} type="button" aria-pressed={view === v} className={`px-3 py-1 ${view === v ? 'bg-slate-800 text-white' : 'bg-white'}`} onClick={() => setView(v)}>
                  {t(`web.catalog.${v}`)}
                </button>
              ))}
            </div>
          </div>
          {view === 'gallery' ? (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5">
              {shownRows.map((r) => {
                const photo = r.photoKey
                const status = expiryStatus(r.expiryDate)
                return (
                  <div key={r.productId} className="overflow-hidden rounded-xl border bg-white shadow-sm">
                    {photo ? <img src={admin.productPhotoUrl(photo)} alt="" loading="lazy" className="h-32 w-full bg-slate-100 object-contain" /> : <div aria-hidden className="flex h-32 items-center justify-center bg-slate-100 text-3xl opacity-60">💊</div>}
                    <div className="space-y-1 p-2">
                      <div className="line-clamp-2 min-h-10 font-semibold">{r.productName}</div>
                      <div className="text-xs text-slate-500">{r.productCode}{(r.reserved ?? 0) > 0 && ` · ${t('web.stock.reserved', { reserved: number(r.reserved ?? 0), free: number(r.quantity - (r.reserved ?? 0)) })}`}</div>
                      {status !== 'none' && status !== 'ok' && <div className={`text-xs ${status === 'expired' ? 'font-semibold text-red-600' : 'text-orange-600'}`}>{r.expiryDate}</div>}
                      {canEdit ? (
                        <div className="flex items-center gap-1">
                          <input aria-label={t('web.stock.quantityOf', { name: r.productName })} type="number" min={0} step="any" className="w-full min-w-0 rounded border px-2 py-1" value={draftOf(r)}
                            onChange={(e) => setDrafts({ ...drafts, [`${r.warehouseId}:${r.productId}`]: e.target.value })} />
                          <span className="text-xs">{unitLabel(r.unit)}</span>
                          <button className="rounded border px-2 py-1" aria-label={t('web.stock.saveOf', { name: r.productName })} disabled={action.busy || draftOf(r).trim() === '' || Number(draftOf(r)) === r.quantity} onClick={() => void save(r)}>
                            {t('web.stock.save')}
                          </button>
                        </div>
                      ) : (
                        <div className="font-bold">{r.counted ? `${number(r.quantity)} ${unitLabel(r.unit)}` : t('web.stock.notCounted')}</div>
                      )}
                    </div>
                  </div>
                )
              })}
            </div>
          ) : (
            <div className="space-y-1">
              {shownRows.map((r) => (
                <div key={r.productId} className="flex items-center gap-3">
                  <span className="flex-1">{r.productCode} · {r.productName}{(r.reserved ?? 0) > 0 && <em className="text-slate-600"> ({t('web.stock.reserved', { reserved: number(r.reserved ?? 0), free: number(r.quantity - (r.reserved ?? 0)) })})</em>}</span>
                  {canEdit ? (
                    <>
                      <input aria-label={t('web.stock.quantityOf', { name: r.productName })} type="number" min={0} step="any" className="w-28 rounded border px-2 py-1" value={draftOf(r)}
                        onChange={(e) => setDrafts({ ...drafts, [`${r.warehouseId}:${r.productId}`]: e.target.value })} />
                      <span className="w-12">{unitLabel(r.unit)}</span>
                      <button className="rounded border px-2 py-1" aria-label={t('web.stock.saveOf', { name: r.productName })} disabled={action.busy || draftOf(r).trim() === '' || Number(draftOf(r)) === r.quantity} onClick={() => void save(r)}>
                        {t('web.stock.save')}
                      </button>
                    </>
                  ) : (
                    <span>{r.counted ? `${number(r.quantity)} ${unitLabel(r.unit)}` : t('web.stock.notCounted')}</span>
                  )}
                </div>
              ))}
            </div>
          )}
          {shownRows.length === 0 && <p className="text-slate-600">{t('web.catalog.noResults')}</p>}
        </div>
      )}
      {products.length > 0 && warehouses.length > 0 && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-1">{t('web.stock.product')}</th>
              <th>{t('web.stock.total')}</th>
              <th>{t('web.stock.where')}</th>
            </tr>
          </thead>
          <tbody>
            {products.map((p) => (
              <tr key={p.id} className="border-b">
                <td className="py-1">{p.code} · {p.name}</td>
                <td>{number(p.total)} {unitLabel(p.unit)}</td>
                <td>{p.where.join(' · ') || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
