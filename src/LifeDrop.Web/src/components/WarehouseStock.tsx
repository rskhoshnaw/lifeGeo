import { useCallback, useMemo, useState } from 'react'
import { AdminApi, type WarehouseStockRow } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

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
              <li key={`${r.warehouseId}:${r.productId}`}>{r.warehouseName}: {r.productCode} · {r.productName} — {number(r.quantity)} {r.unit}</li>
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
        <div className="space-y-1 rounded border bg-slate-50 p-2">
          <h4 className="font-semibold">{current.code} · {current.name}</h4>
          {current.rows.map((r) => (
            <div key={r.productId} className="flex items-center gap-3">
              <span className="flex-1">{r.productCode} · {r.productName}{(r.reserved ?? 0) > 0 && <em className="text-slate-600"> ({t('web.stock.reserved', { reserved: number(r.reserved ?? 0), free: number(r.quantity - (r.reserved ?? 0)) })})</em>}</span>
              {canEdit ? (
                <>
                  <input aria-label={t('web.stock.quantityOf', { name: r.productName })} type="number" min={0} step="any" className="w-28 rounded border px-2 py-1" value={draftOf(r)}
                    onChange={(e) => setDrafts({ ...drafts, [`${r.warehouseId}:${r.productId}`]: e.target.value })} />
                  <span className="w-12">{r.unit}</span>
                  <button className="rounded border px-2 py-1" aria-label={t('web.stock.saveOf', { name: r.productName })} disabled={action.busy || draftOf(r).trim() === '' || Number(draftOf(r)) === r.quantity} onClick={() => void save(r)}>
                    {t('web.stock.save')}
                  </button>
                </>
              ) : (
                <span>{r.counted ? `${number(r.quantity)} ${r.unit}` : t('web.stock.notCounted')}</span>
              )}
            </div>
          ))}
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
                <td>{number(p.total)} {p.unit}</td>
                <td>{p.where.join(' · ') || '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
