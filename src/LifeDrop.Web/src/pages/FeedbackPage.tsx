import { useCallback, useEffect, useState } from 'react'
import { AdminApi, errorMessage, type Feedback, type FeedbackFilters, type FeedbackStatus, type UserSummary } from '../api/admin.ts'
import { Badge, type Tone } from '../components/Badge.tsx'
import { useAction, useLoad } from '../components/useLoad.ts'
import { t, type TextKey } from '../i18n.ts'

const statuses: FeedbackStatus[] = ['new', 'in_review', 'done']
const kinds = ['suggestion', 'complaint', 'note'] as const
const tone: Record<FeedbackStatus, Tone> = { new: 'warn', in_review: 'info', done: 'good' }
const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'short' })

// Feedback from the field (plan step 5.30): what the users sent from the app (a suggestion, a complaint or a note, about the work or about a customer, with photos). A manager
// reads the list (the whole company, or the own region), opens one, answers it (the author is told and sees the answer in the app) and sets its status.
export function FeedbackPage({ admin }: { admin: AdminApi }) {
  const [filters, setFilters] = useState<FeedbackFilters>({})
  const users = useLoad(useCallback(() => admin.users().catch(() => [] as UserSummary[]), [admin]))
  const first = useLoad(useCallback(() => admin.feedbackList(filters), [admin, filters]))
  const [more, setMore] = useState<Feedback[]>([])
  const [done, setDone] = useState(false)
  const [openId, setOpenId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const items = [...(first.data ?? []), ...more]
  const open = items.find((x) => x.id === openId) ?? null

  const change = (next: FeedbackFilters) => {
    setFilters(next)
    setMore([])
    setDone(false)
  }

  async function loadMore() {
    try {
      const page = await admin.feedbackList(filters, items[items.length - 1]?.createdAt)
      setMore([...more, ...page])
      if (page.length < 50) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  function refresh() {
    setMore([])
    setDone(false)
    first.reload()
  }

  return (
    <section className="space-y-4 p-6 text-sm">
      <h2 className="text-xl font-semibold">{t('web.feedback.title')}</h2>
      {(error ?? first.error) && <p role="alert" className="text-red-700">{error ?? first.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.feedback.allStatuses')} className="rounded border px-2 py-1" value={filters.status ?? ''} onChange={(e) => change({ ...filters, status: e.target.value as FeedbackStatus | '' })}>
          <option value="">{t('web.feedback.allStatuses')}</option>
          {statuses.map((s) => (
            <option key={s} value={s}>{t(`web.feedback.status.${s}` as TextKey)}</option>
          ))}
        </select>
        <select aria-label={t('web.feedback.allKinds')} className="rounded border px-2 py-1" value={filters.kind ?? ''} onChange={(e) => change({ ...filters, kind: e.target.value as (typeof kinds)[number] | '' })}>
          <option value="">{t('web.feedback.allKinds')}</option>
          {kinds.map((k) => (
            <option key={k} value={k}>{t(`web.feedback.kind.${k}` as TextKey)}</option>
          ))}
        </select>
        {(users.data ?? []).length > 0 && (
          <select aria-label={t('web.feedback.allUsers')} className="rounded border px-2 py-1" value={filters.userId ?? ''} onChange={(e) => change({ ...filters, userId: e.target.value })}>
            <option value="">{t('web.feedback.allUsers')}</option>
            {(users.data ?? []).map((u) => (
              <option key={u.id} value={u.id}>{u.email ?? u.id}</option>
            ))}
          </select>
        )}
      </div>
      {first.data && items.length === 0 && <p className="text-slate-600">{t('web.feedback.none')}</p>}
      {items.length > 0 && (
        <table className="w-full text-start">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.feedback.date')}</th>
              <th>{t('web.feedback.author')}</th>
              <th>{t('web.feedback.kindLabel')}</th>
              <th>{t('web.feedback.statusLabel')}</th>
              <th>{t('web.feedback.customer')}</th>
              <th>{t('web.feedback.text')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((f) => (
              <tr key={f.id} className={`border-b ${f.id === openId ? 'bg-slate-50' : ''}`}>
                <td className="py-2">{when.format(new Date(f.createdAt))}</td>
                <td>{f.userEmail ?? f.userId}</td>
                <td>{t(`web.feedback.kind.${f.kind}` as TextKey)}</td>
                <td><Badge tone={tone[f.status]}>{t(`web.feedback.status.${f.status}` as TextKey)}</Badge></td>
                <td>{f.customerCode ? `${f.customerCode} · ${f.customerName ?? ''}` : '—'}</td>
                <td className="max-w-md truncate">{f.body}</td>
                <td className="text-end">
                  <button className="rounded border px-2 py-1" aria-label={`${t('web.feedback.open')} ${f.userEmail ?? ''}`} onClick={() => setOpenId(f.id === openId ? null : f.id)}>
                    {t('web.feedback.open')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {open && <FeedbackDetail key={open.id} admin={admin} item={open} onChanged={refresh} onClose={() => setOpenId(null)} />}
      {first.data && first.data.length === 50 && !done && (
        <button className="rounded border px-3 py-1" onClick={() => void loadMore()}>
          {t('web.action.loadMore')}
        </button>
      )}
    </section>
  )
}

// One feedback with its photos, the answers so far, a box for a new answer and the status buttons.
function FeedbackDetail({ admin, item, onChanged, onClose }: { admin: AdminApi; item: Feedback; onChanged: () => void; onClose: () => void }) {
  const [reply, setReply] = useState('')
  const [photos, setPhotos] = useState<string[]>([])
  const action = useAction(onChanged)

  const photoKey = item.photoIds.join(',')
  useEffect(() => {
    let active = true
    const urls: string[] = []
    const ids = photoKey ? photoKey.split(',') : []
    void Promise.all(ids.map((photoId) => admin.feedbackPhotoUrl(item.id, photoId).catch(() => null))).then((loaded) => {
      const ok = loaded.filter((u): u is string => u !== null)
      urls.push(...ok)
      if (active) setPhotos(ok)
      else ok.forEach((u) => URL.revokeObjectURL(u))
    })
    return () => {
      active = false
      urls.forEach((u) => URL.revokeObjectURL(u))
    }
  }, [admin, item.id, photoKey])

  async function send() {
    const text = reply.trim()
    if (!text) return
    await action.run(async () => {
      await admin.replyFeedback(item.id, text)
      setReply('')   // only an answer that was saved is cleared
    })
  }

  return (
    <div className="space-y-3 rounded border p-4">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold">{t(`web.feedback.kind.${item.kind}` as TextKey)} · {item.userEmail ?? item.userId}</h3>
        <button className="rounded border px-2 py-1" onClick={onClose}>{t('web.feedback.close')}</button>
      </div>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      <p className="whitespace-pre-wrap">{item.body}</p>
      {item.customerCode && <p className="text-slate-600">{t('web.feedback.customer')}: {item.customerCode} · {item.customerName}</p>}
      {photos.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {photos.map((url, i) => (
            <a key={url} href={url} target="_blank" rel="noreferrer">
              <img src={url} alt={t('web.feedback.photoAlt', { number: i + 1 })} className="h-28 rounded border object-cover" />
            </a>
          ))}
        </div>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-slate-600">{t('web.feedback.statusLabel')}:</span>
        {statuses.map((s) => (
          <button key={s} className={`rounded border px-2 py-1 ${item.status === s ? 'bg-slate-100 font-semibold' : ''}`} disabled={action.busy || item.status === s} onClick={() => void action.run(() => admin.setFeedbackStatus(item.id, s))}>
            {t(`web.feedback.status.${s}` as TextKey)}
          </button>
        ))}
      </div>
      <h4 className="font-medium">{t('web.feedback.replies')}</h4>
      {item.replies.length === 0 && <p className="text-slate-600">{t('web.feedback.noReplies')}</p>}
      <ul className="space-y-2">
        {item.replies.map((r) => (
          <li key={r.id} className="rounded bg-slate-50 p-2">
            <div className="text-xs text-slate-600">{r.userEmail ?? r.userId} · {when.format(new Date(r.createdAt))}</div>
            <div className="whitespace-pre-wrap">{r.body}</div>
          </li>
        ))}
      </ul>
      <div className="flex items-start gap-2">
        <textarea aria-label={t('web.feedback.replyPlaceholder')} placeholder={t('web.feedback.replyPlaceholder')} className="min-h-16 flex-1 rounded border px-2 py-1" maxLength={1000} value={reply} onChange={(e) => setReply(e.target.value)} />
        <button className="rounded border px-3 py-1" disabled={action.busy || !reply.trim()} onClick={() => void send()}>{t('web.feedback.send')}</button>
      </div>
    </div>
  )
}
