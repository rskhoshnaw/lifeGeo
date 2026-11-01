import { useEffect, useState } from 'react'
import { t } from '../i18n.ts'
import { clientErrorPageSize, clientErrorStages, errorMessage, type AdminApi, type ClientError } from '../api/admin.ts'

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'medium' })

// Connection errors of the mobile app (plan step 5.10): DNS, timeout and similar failures that never reached the server. The app keeps them on the phone and sends them when the connection
// works again, so a manager can see why someone could not sign in (for example a wrongly typed company code). Read-only; the server deletes them after 40 days.
export function ClientErrorsPage({ admin }: { admin: AdminApi }) {
  const [stage, setStage] = useState('')
  const [rows, setRows] = useState<ClientError[] | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function load(nextStage: string, after?: ClientError, current: ClientError[] = []) {
    setBusy(true)
    setError(null)
    try {
      const page = await admin.clientErrors(nextStage || undefined, after)
      setRows(after ? [...current, ...page] : page)
      setHasMore(page.length === clientErrorPageSize)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    void load('')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [admin])

  return (
    <section className="space-y-4 p-6">
      <h2 className="text-xl font-semibold">{t('web.clientErrors.title')}</h2>
      <p className="text-sm text-slate-600">{t('web.clientErrors.note')}</p>
      <label className="flex items-center gap-2 text-sm">
        {t('web.clientErrors.stage')}
        <select
          className="rounded border px-2 py-1"
          value={stage}
          disabled={busy}
          onChange={(e) => {
            setStage(e.target.value)
            void load(e.target.value)
          }}
        >
          <option value="">{t('web.clientErrors.allStages')}</option>
          {clientErrorStages.map((s) => (
            <option key={s} value={s}>
              {t(`web.clientErrors.stage.${s}`)}
            </option>
          ))}
        </select>
      </label>
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      {rows?.length === 0 && <p className="text-slate-600">{t('web.clientErrors.none')}</p>}
      {rows && rows.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.clientErrors.occurred')}</th>
              <th>{t('web.clientErrors.stage')}</th>
              <th>{t('web.clientErrors.kind')}</th>
              <th>{t('web.clientErrors.enteredCode')}</th>
              <th>{t('web.clientErrors.enteredEmail')}</th>
              <th>{t('web.clientErrors.device')}</th>
              <th>{t('web.clientErrors.version')}</th>
              <th>{t('web.clientErrors.message')}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="border-b align-top">
                <td className="py-2 whitespace-nowrap" title={`${t('web.clientErrors.received')}: ${when.format(new Date(r.reportedAt))}`}>{when.format(new Date(r.occurredAt))}</td>
                <td>{t(`web.clientErrors.stage.${r.stage}`)}</td>
                <td>{t(`web.clientErrors.kind.${r.errorKind}`)}</td>
                <td dir="ltr">{r.enteredCompanyCode ?? '—'}</td>
                <td dir="ltr">{r.enteredEmail ?? '—'}</td>
                <td className="font-mono text-xs" dir="ltr">{[r.platform, r.deviceId?.slice(0, 8)].filter(Boolean).join(' ') || '—'}</td>
                <td dir="ltr">{r.appVersion ?? '—'}</td>
                <td className="text-xs text-slate-700" dir="ltr">{r.errorType}: {r.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {rows && hasMore && (
        <button className="rounded border px-3 py-1 text-sm" disabled={busy} onClick={() => void load(stage, rows[rows.length - 1], rows)}>
          {t('web.action.loadMore')}
        </button>
      )}
    </section>
  )
}
