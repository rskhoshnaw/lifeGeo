import { useCallback, useRef, useState, type FormEvent } from 'react'
import { AdminApi, can, doctorSpecialties, Permissions, type CityPrice, type CustomerDiscount, type Me, type Place, type Product, type ProductInput } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { expiryStatus, filterProducts, type ProductFilters } from './productView.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })

// Products and prices (plan step 2.1): everyone with catalog.read looks; the sales manager (catalog.manage) adds and edits products and
// price rules. The server decides which price a customer pays; the check at the bottom shows exactly that.
export function CatalogPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const canManage = can(me, ...Permissions.manageCatalog)
  const [search, setSearch] = useState('')
  const [inactive, setInactive] = useState(false)
  const [editing, setEditing] = useState<Product | 'new' | null>(null)
  const [newCount, setNewCount] = useState(0)   // a new key empties the form after "Save and new product"
  const [view, setView] = useState<'gallery' | 'table'>('gallery')
  const [filters, setFilters] = useState<ProductFilters>({ text: '', specialty: '', manufacturer: '', stock: '' })
  const [details, setDetails] = useState<Product | null>(null)
  const [rulesFor, setRulesFor] = useState<Product | null>(null)
  const products = useLoad(useCallback(() => admin.products(canManage && inactive, search.trim()), [admin, canManage, inactive, search]))
  const quick = useAction(products.reload)

  // The list changes one thing at a time (status or base price); the server takes the whole product, so everything else is sent as it is.
  const asInput = (p: Product, change: Partial<ProductInput>): ProductInput => ({
    name: p.name, nameCkb: p.nameCkb, nameAr: p.nameAr, nameEn: p.nameEn, unit: p.unit, currency: p.currency, basePrice: p.basePrice, status: p.status, ...change,
  })

  function changePrice(p: Product) {
    const text = window.prompt(t('web.catalog.pricePrompt', { code: p.code, currency: p.currency }), String(p.basePrice))?.trim()
    const price = Number(text)
    if (text && Number.isFinite(price) && price >= 0) void quick.run(() => admin.updateProduct(p.id, asInput(p, { basePrice: price })))
  }

  const makers = [...new Set((products.data ?? []).map((p) => p.manufacturer?.trim()).filter((m): m is string => !!m))].sort((a, b) => a.localeCompare(b))
  const shown = filterProducts(products.data ?? [], filters)

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.catalog.title')}</h2>
        <input aria-label={t('web.catalog.search')} placeholder={t('web.catalog.search')} className="rounded border px-2 py-1 text-sm" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select aria-label={t('web.catalog.specialty')} className="rounded border px-2 py-1 text-sm" value={filters.specialty} onChange={(e) => setFilters({ ...filters, specialty: e.target.value })}>
          <option value="">{t('web.catalog.allSpecialties')}</option>
          {doctorSpecialties.map((x) => (
            <option key={x} value={x}>{t(`specialty.${x}` as TextKey)}</option>
          ))}
        </select>
        <select aria-label={t('web.catalog.manufacturer')} className="rounded border px-2 py-1 text-sm" value={filters.manufacturer} onChange={(e) => setFilters({ ...filters, manufacturer: e.target.value })}>
          <option value="">{t('web.catalog.allManufacturers')}</option>
          {makers.map((m) => (
            <option key={m} value={m}>{m}</option>
          ))}
        </select>
        <select aria-label={t('web.catalog.stock')} className="rounded border px-2 py-1 text-sm" value={filters.stock} onChange={(e) => setFilters({ ...filters, stock: e.target.value as ProductFilters['stock'] })}>
          <option value="">{t('web.catalog.allStock')}</option>
          <option value="in">{t('web.catalog.inStock')}</option>
          <option value="out">{t('web.catalog.outOfStock')}</option>
        </select>
        <div className="inline-flex overflow-hidden rounded border text-sm" role="group">
          {(['gallery', 'table'] as const).map((v) => (
            <button key={v} type="button" aria-pressed={view === v} className={`px-3 py-1 ${view === v ? 'bg-slate-800 text-white' : ''}`} onClick={() => setView(v)}>
              {t(`web.catalog.${v}`)}
            </button>
          ))}
        </div>
        {canManage && (
          <>
            <label className="flex items-center gap-1 text-sm">
              <input type="checkbox" checked={inactive} onChange={(e) => setInactive(e.target.checked)} />
              {t('web.catalog.showInactive')}
            </label>
            <button className="rounded border px-3 py-1 text-sm" onClick={() => setEditing('new')}>
              {t('web.catalog.newProduct')}
            </button>
          </>
        )}
      </div>
      {(products.error ?? quick.error) && <p role="alert" className="text-sm text-red-700">{products.error ?? quick.error}</p>}
      {products.data && view === 'gallery' && (
        <>
          {shown.length === 0 && <p className="text-sm text-slate-500">{t('web.catalog.noResults')}</p>}
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5">
            {shown.map((p) => {
              const status = expiryStatus(p.expiryDate)
              return (
                <button
                  key={p.id}
                  type="button"
                  aria-label={p.name}
                  onClick={() => setDetails(p)}
                  className={`group overflow-hidden rounded-xl border bg-white text-start shadow-sm transition hover:-translate-y-0.5 hover:shadow-md focus:outline-none focus-visible:ring-2 focus-visible:ring-rose-500 ${p.status === 'inactive' || p.available === false ? 'opacity-60' : ''}`}
                >
                  <ProductPhoto admin={admin} product={p} className="h-40 w-full" />
                  <div className="space-y-0.5 p-3">
                    <div className="line-clamp-2 min-h-10 text-sm font-semibold">{p.name}</div>
                    <div className="min-h-4 truncate text-xs italic text-slate-500">{p.scientificName ?? ''}</div>
                    <div className={`min-h-4 text-xs ${status === 'expired' ? 'font-semibold text-red-600' : status === 'soon' ? 'text-orange-600' : 'text-slate-500'}`}>
                      {p.expiryDate ? `${status === 'expired' ? `${t('web.catalog.expired')} · ` : `${t('web.catalog.expiry')}: `}${p.expiryDate.slice(0, 10)}` : ''}
                    </div>
                    <div className="pt-1 text-base font-bold text-rose-600">
                      {money(p.basePrice)} {p.currency}
                    </div>
                  </div>
                </button>
              )
            })}
          </div>
        </>
      )}
      {products.data && view === 'table' && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.catalog.code')}</th>
              <th>{t('web.catalog.name')}</th>
              <th>{t('web.catalog.unit')}</th>
              <th>{t('web.catalog.basePrice')}</th>
              <th>{t('web.catalog.specialty')}</th>
              <th>{t('web.catalog.stock')}</th>
              <th>{t('web.catalog.status')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {shown.map((p) => (
              <tr key={p.id} className="border-b">
                <td className="py-2">
                  {p.photoKey && <img src={admin.productPhotoUrl(p.photoKey)} alt="" className="me-2 inline h-8 w-8 rounded border bg-slate-100 object-contain" />}
                  {p.code}
                </td>
                <td>{p.name}</td>
                <td>{p.unit}</td>
                <td>
                  {money(p.basePrice)} {p.currency}
                </td>
                <td>{p.specialty ? t(`specialty.${p.specialty}` as TextKey) : ''}</td>
                <td>{p.stockQty == null ? '' : p.inStock ? `${t('web.catalog.inStock')} (${money(p.stockQty)})` : t('web.catalog.outOfStock')}</td>
                <td>{t(`web.catalog.${p.status}`)}{p.available === false && <em> · {t('web.catalog.unavailable')}</em>}</td>
                <td className="space-x-2 text-end">
                  {canManage && (
                    <>
                      <button className="rounded border px-2 py-1" aria-label={t('web.catalog.editProduct', { code: p.code })} onClick={() => setEditing(p)}>
                        {t('web.places.edit')}
                      </button>
                      <button className="rounded border px-2 py-1" aria-label={`${t('web.catalog.changePrice')} ${p.code}`} disabled={quick.busy} onClick={() => changePrice(p)}>
                        {t('web.catalog.changePrice')}
                      </button>
                      <button
                        className="rounded border px-2 py-1"
                        aria-label={`${p.status === 'active' ? t('web.catalog.deactivate') : t('web.catalog.activate')} ${p.code}`}
                        disabled={quick.busy}
                        onClick={() => void quick.run(() => admin.updateProduct(p.id, asInput(p, { status: p.status === 'active' ? 'inactive' : 'active' })))}
                      >
                        {p.status === 'active' ? t('web.catalog.deactivate') : t('web.catalog.activate')}
                      </button>
                    </>
                  )}
                  <button className="rounded border px-2 py-1" aria-label={t('web.catalog.cityPricesFor', { code: p.code })} onClick={() => setRulesFor(p)}>
                    {t('web.catalog.cityPrices')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {details && (
        <ProductDetails
          admin={admin}
          product={details}
          canManage={canManage}
          onClose={() => setDetails(null)}
          onEdit={() => {
            setEditing(details)
            setDetails(null)
          }}
        />
      )}
      {editing && (
        <ProductForm
          key={editing === 'new' ? `new-${newCount}` : editing.id}
          admin={admin}
          product={editing === 'new' ? null : editing}
          onDone={(next) => {
            if (next) setNewCount((n) => n + 1)
            setEditing(next ? 'new' : null)
            products.reload()
          }}
        />
      )}
      {rulesFor && <CityPrices key={rulesFor.id} admin={admin} product={rulesFor} canManage={canManage} />}
      {can(me, ...Permissions.customers) && <CustomerPrices admin={admin} canManage={canManage} />}
    </section>
  )
}

function ProductPhoto({ admin, product, className }: { admin: AdminApi; product: Product; className: string }) {
  return product.photoKey ? (
    <img src={admin.productPhotoUrl(product.photoKey)} alt="" loading="lazy" className={`${className} bg-slate-100 object-contain`} />
  ) : (
    <div aria-hidden className={`${className} flex items-center justify-center bg-slate-100 text-4xl opacity-60`}>💊</div>
  )
}

// Everything entered for one product, opened from the gallery card.
function ProductDetails({ admin, product: p, canManage, onClose, onEdit }: { admin: AdminApi; product: Product; canManage: boolean; onClose: () => void; onEdit: () => void }) {
  const status = expiryStatus(p.expiryDate)
  const rows: [string, string | null | undefined, string?][] = [
    [t('web.catalog.code'), p.code],
    [t('web.catalog.specialty'), p.specialty ? t(`specialty.${p.specialty}` as TextKey) : null],
    [t('web.catalog.manufacturer'), p.manufacturer],
    [t('web.catalog.unit'), p.unit],
    [
      t('web.catalog.expiry'),
      p.expiryDate ? `${p.expiryDate.slice(0, 10)}${status === 'expired' ? ` · ${t('web.catalog.expired')}` : status === 'soon' ? ` · ${t('web.catalog.expiresSoon')}` : ''}` : null,
      status === 'expired' ? 'text-red-600' : status === 'soon' ? 'text-orange-600' : undefined,
    ],
    [t('web.catalog.stock'), p.stockQty == null ? null : p.inStock ? `${t('web.catalog.inStock')} (${money(p.stockQty)})` : t('web.catalog.outOfStock')],
    [t('web.catalog.status'), `${t(`web.catalog.${p.status}`)}${p.available === false ? ` · ${t('web.catalog.unavailable')}` : ''}`],
    [t('web.catalog.notes'), p.notes],
  ]
  return (
    <div role="dialog" aria-modal="true" aria-label={t('web.catalog.details')} className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" onClick={onClose}>
      <div className="max-h-full w-full max-w-lg overflow-y-auto rounded-2xl bg-white shadow-xl" onClick={(e) => e.stopPropagation()}>
        <ProductPhoto admin={admin} product={p} className="h-64 w-full" />
        <div className="space-y-1 p-5">
          <h3 className="text-xl font-semibold">{p.name}</h3>
          {p.scientificName && <p className="italic text-slate-500">{p.scientificName}</p>}
          <p className="pb-2 text-2xl font-bold text-rose-600">
            {money(p.basePrice)} {p.currency} / {p.unit}
          </p>
          <dl className="divide-y text-sm">
            {rows.filter(([, v]) => !!v).map(([label, value, color]) => (
              <div key={label} className="flex gap-3 py-2">
                <dt className="w-1/3 text-slate-500">{label}</dt>
                <dd className={`w-2/3 whitespace-pre-wrap ${color ?? ''}`}>{value}</dd>
              </div>
            ))}
          </dl>
          <div className="flex justify-end gap-2 pt-3">
            {canManage && (
              <button type="button" className="rounded border px-3 py-1 text-sm" onClick={onEdit}>
                {t('web.places.edit')}
              </button>
            )}
            <button type="button" className="rounded bg-slate-800 px-3 py-1 text-sm text-white" onClick={onClose}>
              {t('web.catalog.close')}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

// The units of a product (owner decision 2026-11-18); the English word is what is stored, the list shows it in the panel language. A unit an older product already has stays selectable.
const productUnits = ['box', 'strip', 'bottle', 'vial', 'ampoule', 'tube', 'sachet', 'pack', 'carton', 'piece'] as const
type ProductUnit = (typeof productUnits)[number]

function ProductForm({ admin, product, onDone }: { admin: AdminApi; product: Product | null; onDone: (saveAndNew?: boolean) => void }) {
  const [code, setCode] = useState(product?.code ?? '')
  const [name, setName] = useState(product?.name ?? '')
  const [unit, setUnit] = useState(product?.unit ?? '')
  const [currency, setCurrency] = useState<'IQD' | 'USD'>(product?.currency ?? 'IQD')
  const [price, setPrice] = useState(product ? String(product.basePrice) : '')
  const [status, setStatus] = useState<'active' | 'inactive'>(product?.status ?? 'active')
  const [specialty, setSpecialty] = useState(product?.specialty ?? '')
  const [scientific, setScientific] = useState(product?.scientificName ?? '')
  const [available, setAvailable] = useState(product?.available ?? true)
  const [minStock, setMinStock] = useState(product?.minStock == null ? '' : String(product.minStock))
  const [maker, setMaker] = useState(product?.manufacturer ?? '')
  const [expiry, setExpiry] = useState(product?.expiryDate ?? '')
  const [stock, setStock] = useState(product?.stockQty == null ? '' : String(product.stockQty))
  const [notes, setNotes] = useState(product?.notes ?? '')
  const [photo, setPhoto] = useState<File | null>(null)
  const [dropPhoto, setDropPhoto] = useState(false)
  const saveNext = useRef(false)   // true when "Save and new product" was pressed: the form opens empty after the save
  const formRef = useRef<HTMLFormElement>(null)
  const action = useAction(() => onDone(saveNext.current))
  const field = 'rounded border px-2 py-1'

  // Fields are kept as typed and trimmed only here, so spaces can be typed.
  function submit(e: FormEvent) {
    e.preventDefault()
    const input: ProductInput = {
      // The names in other languages are not entered any more (owner decision 2026-11-18); what an older product has is kept as it is.
      name: name.trim(), nameCkb: product?.nameCkb ?? null, nameAr: product?.nameAr ?? null, nameEn: product?.nameEn ?? null,
      unit: unit.trim(), currency, basePrice: Number(price), status,
    }
    void action.run(async () => {
      let id = product?.id
      if (product) await admin.updateProduct(product.id, input)
      else {
        // A new product: when a photo or any detail was filled in, they are saved right after it is created.
        const created = await admin.createProduct({ ...input, code: code.trim() })
        const extras = photo !== null || specialty !== '' || scientific.trim() !== '' || maker.trim() !== '' || expiry !== '' || stock.trim() !== '' || notes.trim() !== '' || minStock.trim() !== '' || !available
        if (!extras || !created.id) return
        id = created.id
      }
      if (!id) return
      await admin.setProductDetails(id, {
        specialty: specialty || null, manufacturer: maker.trim() || null, expiryDate: expiry || null,
        stockQty: stock.trim() === '' ? null : Number(stock), notes: notes.trim() || null, scientificName: scientific.trim() || null,
        available, minStock: minStock.trim() === '' ? null : Number(minStock),
      })
      if (photo) await admin.setProductPhoto(id, photo)
      else if (dropPhoto && product?.photoKey) await admin.removeProductPhoto(id)
    })
  }

  return (
    <form ref={formRef} onSubmit={submit} className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{product ? t('web.catalog.editProduct', { code: product.code }) : t('web.catalog.newProduct')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <div className="flex flex-wrap gap-2">
        <input aria-label={t('web.catalog.code')} placeholder={t('web.catalog.code')} className={field} maxLength={40} disabled={!!product} title={product ? t('web.catalog.codeFixed') : undefined} value={code} onChange={(e) => setCode(e.target.value)} />
        <input aria-label={t('web.catalog.name')} placeholder={t('web.catalog.name')} className={`${field} min-w-56`} maxLength={160} value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.catalog.specialty')} className={field} value={specialty} onChange={(e) => setSpecialty(e.target.value)}>
          <option value="">{t('web.catalog.specialty')}</option>
          {doctorSpecialties.map((x) => (
            <option key={x} value={x}>{t(`specialty.${x}` as TextKey)}</option>
          ))}
        </select>
        <label className="flex items-center gap-1" title={t('web.catalog.availableHint')}>
          <input type="checkbox" checked={available} onChange={(e) => setAvailable(e.target.checked)} />
          {t('web.catalog.available')}
        </label>
        <input aria-label={t('web.catalog.minStock')} placeholder={t('web.catalog.minStock')} type="number" min={0} step="any" className={field} value={minStock} onChange={(e) => setMinStock(e.target.value)} />
        <input aria-label={t('web.catalog.scientificName')} placeholder={t('web.catalog.scientificName')} className={`${field} min-w-56`} maxLength={150} value={scientific} onChange={(e) => setScientific(e.target.value)} />
        <input aria-label={t('web.catalog.manufacturer')} placeholder={t('web.catalog.manufacturer')} className={field} maxLength={100} value={maker} onChange={(e) => setMaker(e.target.value)} />
        <label className="flex items-center gap-1">
          {t('web.catalog.expiry')}
          <input type="date" className={field} value={expiry} onChange={(e) => setExpiry(e.target.value)} />
        </label>
        <input aria-label={t('web.catalog.stock')} placeholder={t('web.catalog.stockHint')} type="number" min={0} step="any" className={field} value={stock} onChange={(e) => setStock(e.target.value)} />
        <label className="flex items-center gap-1">
          {t('web.catalog.photo')}
          <input type="file" accept="image/jpeg,image/png,image/webp" aria-label={t('web.catalog.photo')} onChange={(e) => setPhoto(e.target.files?.[0] ?? null)} />
        </label>
        {product?.photoKey && (
          <>
            <img src={admin.productPhotoUrl(product.photoKey)} alt={product.name} className="h-12 w-12 rounded border bg-slate-100 object-contain" />
            <label className="flex items-center gap-1">
              <input type="checkbox" checked={dropPhoto} onChange={(e) => setDropPhoto(e.target.checked)} />
              {t('web.catalog.removePhoto')}
            </label>
          </>
        )}
        <input aria-label={t('web.catalog.notes')} placeholder={t('web.catalog.notes')} className={`${field} min-w-56 flex-1`} maxLength={500} value={notes} onChange={(e) => setNotes(e.target.value)} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.catalog.unit')} className={field} value={unit} onChange={(e) => setUnit(e.target.value)}>
          <option value="">{t('web.catalog.unit')}</option>
          {[...productUnits, ...(unit && !productUnits.includes(unit as ProductUnit) ? [unit] : [])].map((u) => (
            <option key={u} value={u}>{productUnits.includes(u as ProductUnit) ? t(`web.unit.${u}` as TextKey) : u}</option>
          ))}
        </select>
        <select aria-label={t('web.catalog.currency')} className={field} value={currency} onChange={(e) => setCurrency(e.target.value as 'IQD' | 'USD')}>
          <option value="IQD">IQD</option>
          <option value="USD">USD</option>
        </select>
        <input aria-label={t('web.catalog.basePrice')} placeholder={t('web.catalog.basePrice')} type="number" min={0} step="any" className={field} value={price} onChange={(e) => setPrice(e.target.value)} />
        <select aria-label={t('web.catalog.status')} className={field} value={status} onChange={(e) => setStatus(e.target.value as 'active' | 'inactive')}>
          <option value="active">{t('web.catalog.active')}</option>
          <option value="inactive">{t('web.catalog.inactive')}</option>
        </select>
        <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !name.trim() || !unit.trim() || price === '' || (!product && !code.trim())} onClick={() => (saveNext.current = false)}>
          {t('web.catalog.save')}
        </button>
        {!product && (
          <button
            type="button"
            className="rounded border border-slate-800 bg-slate-800 px-3 py-1 text-white disabled:opacity-50"
            disabled={action.busy || !name.trim() || !unit.trim() || price === '' || !code.trim()}
            onClick={() => {
              saveNext.current = true
              formRef.current?.requestSubmit()
            }}
          >
            {t('web.catalog.saveAndNew')}
          </button>
        )}
        <button type="button" className="rounded border px-3 py-1" onClick={() => onDone()}>
          {t('web.customers.closeHours')}
        </button>
      </div>
    </form>
  )
}

function CityPrices({ admin, product, canManage }: { admin: AdminApi; product: Product; canManage: boolean }) {
  const prices = useLoad(useCallback(() => admin.cityPrices(product.id), [admin, product.id]))
  const cities = useLoad(useCallback(() => (canManage ? admin.places(null) : Promise.resolve([] as Place[])), [admin, canManage]))
  const action = useAction(prices.reload)
  const [city, setCity] = useState('')
  const [price, setPrice] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  function add(e: FormEvent) {
    e.preventDefault()
    if (!city || price === '') return
    void action.run(async () => {
      await admin.createCityPrice(product.id, { cityId: city, price: Number(price), validFrom: from || null, validTo: to || null })
      setPrice('')
    })
  }

  function end(row: CityPrice) {
    const day = window.prompt(t('web.catalog.endPrompt', { target: row.cityName }))?.trim()
    if (day) void action.run(() => admin.endCityPrice(row.id, day))
  }

  const error = action.error ?? prices.error
  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.catalog.cityPricesFor', { code: product.code })}</h3>
      <p className="text-slate-600">{t('web.catalog.cityHint')}</p>
      {error && <p role="alert" className="text-red-700">{error}</p>}
      {prices.data?.length === 0 && <p className="text-slate-600">{t('web.catalog.noCityPrices')}</p>}
      {prices.data && prices.data.length > 0 && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-1">{t('web.catalog.city')}</th>
              <th>{t('web.catalog.price')}</th>
              <th>{t('web.catalog.from')}</th>
              <th>{t('web.catalog.to')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {prices.data.map((r) => (
              <tr key={r.id} className="border-b">
                <td className="py-1">{r.cityName}</td>
                <td>
                  {money(r.price)} {product.currency}
                </td>
                <td>{r.validFrom}</td>
                <td>{r.validTo ?? t('web.catalog.open')}</td>
                <td className="text-end">
                  {canManage && (
                    <button className="rounded border px-2 py-1" aria-label={t('web.catalog.endPriceFor', { target: r.cityName })} disabled={action.busy} onClick={() => end(r)}>
                      {t('web.catalog.endRule')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {canManage && (
        <form onSubmit={add} className="flex flex-wrap items-center gap-2">
          <select aria-label={t('web.catalog.city')} className="rounded border px-2 py-1" value={city} onChange={(e) => setCity(e.target.value)}>
            <option value="">{t('web.catalog.chooseCity')}</option>
            {(cities.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          <input aria-label={t('web.catalog.price')} placeholder={t('web.catalog.price')} type="number" min={0} step="any" className="w-32 rounded border px-2 py-1" value={price} onChange={(e) => setPrice(e.target.value)} />
          <input aria-label={t('web.catalog.from')} type="date" className="rounded border px-2 py-1" value={from} onChange={(e) => setFrom(e.target.value)} />
          <input aria-label={t('web.catalog.to')} type="date" className="rounded border px-2 py-1" value={to} onChange={(e) => setTo(e.target.value)} />
          <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !city || price === ''}>
            {t('web.catalog.addRule')}
          </button>
        </form>
      )}
    </div>
  )
}

const discountText = (d: { kind: 'percent' | 'amount'; value: number }) => (d.kind === 'percent' ? `-${money(d.value)}%` : `-${money(d.value)}`)

// What one customer pays on a day as the server works it out (general or city price, then the discounts), and the customer's discounts.
function CustomerPrices({ admin, canManage }: { admin: AdminApi; canManage: boolean }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const [customer, setCustomer] = useState('')
  const [day, setDay] = useState('')
  const prices = useLoad(
    useCallback(() => (customer ? admin.customerPrices(customer, day || new Date().toISOString().slice(0, 10)) : Promise.resolve(null)), [admin, customer, day]),
  )
  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.catalog.checkTitle')}</h3>
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.catalog.customer')} className="rounded border px-2 py-1" value={customer} onChange={(e) => setCustomer(e.target.value)}>
          <option value="">{t('web.catalog.chooseCustomer')}</option>
          {(customers.data ?? []).map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {c.name}
            </option>
          ))}
        </select>
        <input aria-label={t('web.catalog.checkDate')} type="date" className="rounded border px-2 py-1" value={day} onChange={(e) => setDay(e.target.value)} />
      </div>
      {prices.error && <p role="alert" className="text-red-700">{prices.error}</p>}
      {prices.data && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-1">{t('web.catalog.code')}</th>
              <th>{t('web.catalog.name')}</th>
              <th>{t('web.catalog.listPrice')}</th>
              <th>{t('web.catalog.discounts')}</th>
              <th>{t('web.catalog.finalPrice')}</th>
            </tr>
          </thead>
          <tbody>
            {prices.data.map((p) => (
              <tr key={p.productId} className="border-b">
                <td className="py-1">{p.code}</td>
                <td>{p.name}</td>
                <td>
                  {money(p.listPrice)} {p.currency} ({t(`web.catalog.source.${p.listSource}` as TextKey)})
                </td>
                <td>{p.discounts.map(discountText).join(' ')}</td>
                <td className="font-semibold">
                  {money(p.price)} {p.currency} / {p.unit}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {customer && <CustomerDiscounts key={customer} admin={admin} customerId={customer} canManage={canManage} onChanged={prices.reload} />}
    </div>
  )
}

function CustomerDiscounts({ admin, customerId, canManage, onChanged }: { admin: AdminApi; customerId: string; canManage: boolean; onChanged: () => void }) {
  const discounts = useLoad(useCallback(() => admin.discounts(customerId), [admin, customerId]))
  const products = useLoad(useCallback(() => (canManage ? admin.products(false) : Promise.resolve([] as Product[])), [admin, canManage]))
  const action = useAction(() => {
    discounts.reload()
    onChanged()
  })
  const [product, setProduct] = useState('')
  const [kind, setKind] = useState<'percent' | 'amount'>('percent')
  const [value, setValue] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  // An amount is in the currency of one product, so it needs a product.
  const effectiveKind = product ? kind : 'percent'

  function add(e: FormEvent) {
    e.preventDefault()
    if (value === '') return
    void action.run(async () => {
      await admin.createDiscount(customerId, { productId: product || null, kind: effectiveKind, value: Number(value), validFrom: from || null, validTo: to || null })
      setValue('')
    })
  }

  function end(d: CustomerDiscount) {
    const day = window.prompt(t('web.catalog.endPrompt', { target: discountText(d) }))?.trim()
    if (day) void action.run(() => admin.endDiscount(d.id, day))
  }

  const error = action.error ?? discounts.error
  return (
    <div className="space-y-2 border-t pt-2">
      <h4 className="font-semibold">{t('web.catalog.discounts')}</h4>
      <p className="text-slate-600">{t('web.catalog.discountHint')}</p>
      {error && <p role="alert" className="text-red-700">{error}</p>}
      {discounts.data?.length === 0 && <p className="text-slate-600">{t('web.catalog.noDiscounts')}</p>}
      {discounts.data?.map((d) => (
        <div key={d.id} className="flex flex-wrap items-center gap-3 border-b py-1">
          <span className="min-w-24 font-semibold">{discountText(d)}</span>
          <span>{d.productCode ?? t('web.catalog.allProducts')}</span>
          <span>
            {d.validFrom} → {d.validTo ?? t('web.catalog.open')}
          </span>
          {canManage && (
            <button className="rounded border px-2 py-1" aria-label={t('web.catalog.endDiscountFor', { value: discountText(d) })} disabled={action.busy} onClick={() => end(d)}>
              {t('web.catalog.endRule')}
            </button>
          )}
        </div>
      ))}
      {canManage && (
        <form onSubmit={add} className="flex flex-wrap items-center gap-2">
          <select aria-label={t('web.catalog.product')} className="rounded border px-2 py-1" value={product} onChange={(e) => setProduct(e.target.value)}>
            <option value="">{t('web.catalog.allProducts')}</option>
            {(products.data ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.code} · {p.name}
              </option>
            ))}
          </select>
          <select aria-label={t('web.catalog.discountKind')} className="rounded border px-2 py-1" value={effectiveKind} disabled={!product} onChange={(e) => setKind(e.target.value as 'percent' | 'amount')}>
            <option value="percent">{t('web.catalog.percent')}</option>
            <option value="amount">{t('web.catalog.amount')}</option>
          </select>
          <input aria-label={t('web.catalog.value')} placeholder={t('web.catalog.value')} type="number" min={0} step="any" className="w-28 rounded border px-2 py-1" value={value} onChange={(e) => setValue(e.target.value)} />
          <input aria-label={t('web.catalog.from')} type="date" className="rounded border px-2 py-1" value={from} onChange={(e) => setFrom(e.target.value)} />
          <input aria-label={t('web.catalog.to')} type="date" className="rounded border px-2 py-1" value={to} onChange={(e) => setTo(e.target.value)} />
          <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || value === ''}>
            {t('web.catalog.addDiscount')}
          </button>
        </form>
      )}
    </div>
  )
}
