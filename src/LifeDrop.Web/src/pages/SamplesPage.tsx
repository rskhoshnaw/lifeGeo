import { useCallback, useState } from 'react'
import { AdminApi, can, errorMessage, Permissions, type Me, type Sample } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

// Pharma samples (plan step 2.6): what reps gave to customers. Reps record them in the app; here managers read the list and void a record with a reason.
export function SamplesPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const first = useLoad(useCallback(() => admin.samples(), [admin]))
  const [more, setMore] = useState<Sample[]>([])
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const action = useAction(() => {
    setMore([])
    setDone(false)
    first.reload()
  })
  const items = [...(first.data ?? []), ...more]
  const manage = can(me, ...Permissions.manageSamples)

  async function loadMore() {
    try {
      const page = await admin.samples(items[items.length - 1])
      setMore([...more, ...page])
      if (page.length < 50) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  function voidSample(sample: Sample) {
    const reason = window.prompt(t('web.samples.reasonPrompt'))?.trim()
    if (reason) void action.run(() => admin.voidSample(sample.id, reason))
  }

  return (
    <section className="space-y-6 p-6">
      <h2 className="text-xl font-semibold">{t('web.samples.title')}</h2>
      {(first.error || error || action.error) && <p role="alert" className="text-sm text-red-700">{first.error ?? error ?? action.error}</p>}
      {first.data?.length === 0 && <p className="text-slate-600">{t('web.samples.none')}</p>}
      {items.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.samples.number')}</th>
              <th>{t('web.samples.customer')}</th>
              <th>{t('web.samples.product')}</th>
              <th>{t('web.samples.quantity')}</th>
              <th>{t('web.samples.recipient')}</th>
              <th>{t('web.samples.batch')}</th>
              <th>{t('web.samples.expiry')}</th>
              <th>{t('web.samples.by')}</th>
              <th>{t('web.samples.date')}</th>
              <th>{t('web.samples.status')}</th>
              {manage && <th />}
            </tr>
          </thead>
          <tbody>
            {items.map((s) => (
              <tr key={s.id} className="border-b">
                <td className="py-2">{s.number}</td>
                <td>
                  {s.customerCode} · {s.customerName}
                </td>
                <td>
                  {s.productCode} · {s.productName}
                </td>
                <td>
                  {s.quantity} {s.unit}
                </td>
                <td>{s.recipientName ?? ''}</td>
                <td>{s.batchNumber ?? ''}</td>
                <td>{s.expiryDate ?? ''}</td>
                <td>{s.distributedByEmail ?? ''}</td>
                <td>{s.createdAt.slice(0, 10)}</td>
                <td>
                  {t(`web.samples.status.${s.status}` as TextKey)}
                  {s.voidReason ? ` · ${s.voidReason}` : ''}
                </td>
                {manage && (
                  <td className="text-end">
                    {s.status === 'given' && (
                      <button className="rounded border px-2 py-1" aria-label={t('web.samples.voidAction', { number: s.number })} disabled={action.busy} onClick={() => voidSample(s)}>
                        {t('web.samples.void')}
                      </button>
                    )}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {items.length >= 50 && !done && (
        <button className="rounded border px-3 py-1 text-sm" onClick={() => void loadMore()}>
          …
        </button>
      )}
    </section>
  )
}
