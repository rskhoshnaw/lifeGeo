import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, Permissions, type Customer, type Me, type PriceRule, type Product, type ProductInput } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

const money = (value: number) => value.toLocaleString('en-US', { maximumFractionDigits: 3 })

// Products and prices (plan step 2.1): everyone with catalog.read looks; the sales manager (catalog.manage) adds and edits products and
// price rules. The server decides which price a customer pays; the check at the bottom shows exactly that.
export function CatalogPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const canManage = can(me, ...Permissions.manageCatalog)
  const [search, setSearch] = useState('')
  const [inactive, setInactive] = useState(false)
  const [editing, setEditing] = useState<Product | 'new' | null>(null)
  const [rulesFor, setRulesFor] = useState<Product | null>(null)
  const products = useLoad(useCallback(() => admin.products(canManage && inactive, search.trim()), [admin, canManage, inactive, search]))

  return (
    <section className="space-y-6 p-6">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-xl font-semibold">{t('web.catalog.title')}</h2>
        <input aria-label={t('web.catalog.search')} placeholder={t('web.catalog.search')} className="rounded border px-2 py-1 text-sm" value={search} onChange={(e) => setSearch(e.target.value)} />
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
      {products.error && <p role="alert" className="text-sm text-red-700">{products.error}</p>}
      {products.data && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.catalog.code')}</th>
              <th>{t('web.catalog.name')}</th>
              <th>{t('web.catalog.unit')}</th>
              <th>{t('web.catalog.basePrice')}</th>
              <th>{t('web.catalog.status')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {products.data.map((p) => (
              <tr key={p.id} className="border-b">
                <td className="py-2">{p.code}</td>
                <td>{p.name}</td>
                <td>{p.unit}</td>
                <td>
                  {money(p.basePrice)} {p.currency}
                </td>
                <td>{t(`web.catalog.${p.status}`)}</td>
                <td className="space-x-2 text-end">
                  {canManage && (
                    <button className="rounded border px-2 py-1" aria-label={t('web.catalog.editProduct', { code: p.code })} onClick={() => setEditing(p)}>
                      {t('web.places.edit')}
                    </button>
                  )}
                  <button className="rounded border px-2 py-1" aria-label={t('web.catalog.rulesFor', { code: p.code })} onClick={() => setRulesFor(p)}>
                    {t('web.catalog.rules')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && (
        <ProductForm
          key={editing === 'new' ? 'new' : editing.id}
          admin={admin}
          product={editing === 'new' ? null : editing}
          onDone={() => {
            setEditing(null)
            products.reload()
          }}
        />
      )}
      {rulesFor && <PriceRules key={rulesFor.id} admin={admin} product={rulesFor} canManage={canManage} canRegions={can(me, ...Permissions.regions)} />}
      {can(me, ...Permissions.customers) && <CustomerPriceCheck admin={admin} />}
    </section>
  )
}

function ProductForm({ admin, product, onDone }: { admin: AdminApi; product: Product | null; onDone: () => void }) {
  const [code, setCode] = useState(product?.code ?? '')
  const [name, setName] = useState(product?.name ?? '')
  const [ckb, setCkb] = useState(product?.nameCkb ?? '')
  const [ar, setAr] = useState(product?.nameAr ?? '')
  const [en, setEn] = useState(product?.nameEn ?? '')
  const [unit, setUnit] = useState(product?.unit ?? '')
  const [currency, setCurrency] = useState<'IQD' | 'USD'>(product?.currency ?? 'IQD')
  const [price, setPrice] = useState(product ? String(product.basePrice) : '')
  const [status, setStatus] = useState<'active' | 'inactive'>(product?.status ?? 'active')
  const action = useAction(onDone)
  const field = 'rounded border px-2 py-1'

  // Fields are kept as typed and trimmed only here, so spaces can be typed.
  function submit(e: FormEvent) {
    e.preventDefault()
    const input: ProductInput = {
      name: name.trim(), nameCkb: ckb.trim() || null, nameAr: ar.trim() || null, nameEn: en.trim() || null,
      unit: unit.trim(), currency, basePrice: Number(price), status,
    }
    void action.run(() => (product ? admin.updateProduct(product.id, input) : admin.createProduct({ ...input, code: code.trim() })))
  }

  return (
    <form onSubmit={submit} className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{product ? t('web.catalog.editProduct', { code: product.code }) : t('web.catalog.newProduct')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <div className="flex flex-wrap gap-2">
        <input aria-label={t('web.catalog.code')} placeholder={t('web.catalog.code')} className={field} maxLength={40} disabled={!!product} title={product ? t('web.catalog.codeFixed') : undefined} value={code} onChange={(e) => setCode(e.target.value)} />
        <input aria-label={t('web.catalog.name')} placeholder={t('web.catalog.name')} className={`${field} min-w-56`} maxLength={160} value={name} onChange={(e) => setName(e.target.value)} />
        <input aria-label={t('web.catalog.nameCkb')} placeholder={t('web.catalog.nameCkb')} className={field} maxLength={160} value={ckb} onChange={(e) => setCkb(e.target.value)} />
        <input aria-label={t('web.catalog.nameAr')} placeholder={t('web.catalog.nameAr')} className={field} maxLength={160} value={ar} onChange={(e) => setAr(e.target.value)} />
        <input aria-label={t('web.catalog.nameEn')} placeholder={t('web.catalog.nameEn')} className={field} maxLength={160} value={en} onChange={(e) => setEn(e.target.value)} />
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <input aria-label={t('web.catalog.unit')} placeholder={t('web.catalog.unit')} className={field} maxLength={40} value={unit} onChange={(e) => setUnit(e.target.value)} />
        <select aria-label={t('web.catalog.currency')} className={field} value={currency} onChange={(e) => setCurrency(e.target.value as 'IQD' | 'USD')}>
          <option value="IQD">IQD</option>
          <option value="USD">USD</option>
        </select>
        <input aria-label={t('web.catalog.basePrice')} placeholder={t('web.catalog.basePrice')} type="number" min={0} step="any" className={field} value={price} onChange={(e) => setPrice(e.target.value)} />
        <select aria-label={t('web.catalog.status')} className={field} value={status} onChange={(e) => setStatus(e.target.value as 'active' | 'inactive')}>
          <option value="active">{t('web.catalog.active')}</option>
          <option value="inactive">{t('web.catalog.inactive')}</option>
        </select>
        <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !name.trim() || !unit.trim() || price === '' || (!product && !code.trim())}>
          {t('web.catalog.save')}
        </button>
        <button type="button" className="rounded border px-3 py-1" onClick={onDone}>
          {t('web.customers.closeHours')}
        </button>
      </div>
    </form>
  )
}

function PriceRules({ admin, product, canManage, canRegions }: { admin: AdminApi; product: Product; canManage: boolean; canRegions: boolean }) {
  const rules = useLoad(useCallback(() => admin.priceRules(product.id), [admin, product.id]))
  const customers = useLoad(useCallback(() => (canManage ? admin.customers() : Promise.resolve([] as Customer[])), [admin, canManage]))
  const regions = useLoad(useCallback(() => (canManage && canRegions ? admin.regions() : Promise.resolve([])), [admin, canManage, canRegions]))
  const action = useAction(rules.reload)
  const [kind, setKind] = useState<'customer' | 'region'>('customer')
  const [target, setTarget] = useState('')
  const [price, setPrice] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  function add(e: FormEvent) {
    e.preventDefault()
    if (!target || price === '') return
    void action.run(async () => {
      await admin.createPriceRule(product.id, {
        ...(kind === 'customer' ? { customerId: target } : { regionId: target }),
        price: Number(price), validFrom: from, validTo: to || null,
      })
      setPrice('')
    })
  }

  function end(rule: PriceRule) {
    const label = rule.targetName ?? ''
    const day = window.prompt(t('web.catalog.endPrompt', { target: label }))?.trim()
    if (day) void action.run(() => admin.endPriceRule(rule.id, day))
  }

  const error = action.error ?? rules.error
  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.catalog.rulesFor', { code: product.code })}</h3>
      <p className="text-slate-600">{t('web.catalog.ruleHint')}</p>
      {error && <p role="alert" className="text-red-700">{error}</p>}
      {rules.data?.length === 0 && <p className="text-slate-600">{t('web.catalog.noRules')}</p>}
      {rules.data && rules.data.length > 0 && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-1">{t('web.catalog.target')}</th>
              <th>{t('web.catalog.price')}</th>
              <th>{t('web.catalog.from')}</th>
              <th>{t('web.catalog.to')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {rules.data.map((r) => (
              <tr key={r.id} className="border-b">
                <td className="py-1">
                  {t(r.scope === 'customer' ? 'web.catalog.forCustomer' : 'web.catalog.forRegion')}: {r.targetName}
                </td>
                <td>
                  {money(r.price)} {product.currency}
                </td>
                <td>{r.validFrom}</td>
                <td>{r.validTo ?? t('web.catalog.open')}</td>
                <td className="text-end">
                  {canManage && (
                    <button className="rounded border px-2 py-1" aria-label={t('web.catalog.endRuleFor', { target: r.targetName ?? '' })} disabled={action.busy} onClick={() => end(r)}>
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
          <select aria-label={t('web.catalog.target')} className="rounded border px-2 py-1" value={kind} onChange={(e) => { setKind(e.target.value as 'customer' | 'region'); setTarget('') }}>
            <option value="customer">{t('web.catalog.forCustomer')}</option>
            {(regions.data?.length ?? 0) > 0 && <option value="region">{t('web.catalog.forRegion')}</option>}
          </select>
          <select aria-label={t('web.catalog.targetValue')} className="rounded border px-2 py-1" value={target} onChange={(e) => setTarget(e.target.value)}>
            <option value="">{t(kind === 'customer' ? 'web.catalog.chooseCustomer' : 'web.catalog.chooseRegion')}</option>
            {kind === 'customer'
              ? (customers.data ?? []).filter((c) => c.approvalStatus === 'approved').map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)
              : (regions.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
          <input aria-label={t('web.catalog.price')} placeholder={t('web.catalog.price')} type="number" min={0} step="any" className="w-32 rounded border px-2 py-1" value={price} onChange={(e) => setPrice(e.target.value)} />
          <input aria-label={t('web.catalog.from')} type="date" className="rounded border px-2 py-1" value={from} onChange={(e) => setFrom(e.target.value)} />
          <input aria-label={t('web.catalog.to')} type="date" className="rounded border px-2 py-1" value={to} onChange={(e) => setTo(e.target.value)} />
          <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !target || price === ''}>
            {t('web.catalog.addRule')}
          </button>
        </form>
      )}
    </div>
  )
}

// What a customer pays on a day, as the server computes it (customer rule, else region rule, else base price).
function CustomerPriceCheck({ admin }: { admin: AdminApi }) {
  const customers = useLoad(useCallback(() => admin.customers(), [admin]))
  const [customer, setCustomer] = useState('')
  const [day, setDay] = useState('')
  const prices = useLoad(useCallback(() => (customer ? admin.customerPrices(customer, day || new Date().toISOString().slice(0, 10)) : Promise.resolve(null)), [admin, customer, day]))
  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.catalog.checkTitle')}</h3>
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.catalog.forCustomer')} className="rounded border px-2 py-1" value={customer} onChange={(e) => setCustomer(e.target.value)}>
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
          <tbody>
            {prices.data.map((p) => (
              <tr key={p.productId} className="border-b">
                <td className="py-1">{p.code}</td>
                <td>{p.name}</td>
                <td>
                  {money(p.price)} {p.currency} / {p.unit}
                </td>
                <td>{t(`web.catalog.source.${p.source}` as TextKey)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
