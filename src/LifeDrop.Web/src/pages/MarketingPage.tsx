import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, customerTypes, Permissions, type Me, type MarketingMessage } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { whatsappUrl } from '../components/whatsapp.ts'

// Marketing messages (plan step 5.7). Nothing is sent by the server: a campaign is a queue; the user opens each WhatsApp chat (wa.me) and presses "sent" himself.
// Sending many messages in a row from a personal number can get it banned, so the page asks to wait between messages (see docs/MARKETING.md).
export function MarketingPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const canUse = can(me, ...Permissions.useMarketing)
  const canManage = can(me, ...Permissions.manageMarketing)
  const [open, setOpen] = useState<string | null>(null)
  const campaigns = useLoad(useCallback(() => (canUse ? admin.marketingCampaigns() : Promise.resolve([])), [admin, canUse]))

  return (
    <section className="space-y-6 p-6">
      <h2 className="text-xl font-semibold">{t('web.marketing.title')}</h2>
      {canManage && <Templates admin={admin} />}
      {canUse && <NewCampaign admin={admin} onCreated={(id) => { campaigns.reload(); setOpen(id) }} />}
      {campaigns.error && <p role="alert" className="text-sm text-red-700">{campaigns.error}</p>}
      {canUse && campaigns.data?.length === 0 && <p className="text-slate-600">{t('web.marketing.noCampaigns')}</p>}
      {campaigns.data && campaigns.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.marketing.campaign')}</th>
              <th>{t('web.orders.date')}</th>
              <th>{t('web.marketing.waiting')}</th>
              <th>{t('web.marketing.sent')}</th>
              <th>{t('web.marketing.skipped')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {campaigns.data.map((c) => (
              <tr key={c.id} className="border-b">
                <td className="py-2">
                  {c.name}
                  {c.createdByEmail && <span className="text-slate-500"> · {c.createdByEmail}</span>}
                </td>
                <td>{c.createdAt.slice(0, 10)}</td>
                <td>{c.queued}</td>
                <td>{c.sent}</td>
                <td>{c.skipped}</td>
                <td className="text-end">
                  <button className="rounded border px-2 py-1" aria-label={t('web.marketing.queueOf', { name: c.name })} onClick={() => setOpen(open === c.id ? null : c.id)}>
                    {t('web.marketing.queue')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {open && <Queue key={open} admin={admin} campaignId={open} onChanged={campaigns.reload} />}
    </section>
  )
}

function Templates({ admin }: { admin: AdminApi }) {
  const list = useLoad(useCallback(() => admin.marketingTemplates(true), [admin]))
  const action = useAction(list.reload)
  const [name, setName] = useState('')
  const [body, setBody] = useState('')

  function add(e: FormEvent) {
    e.preventDefault()
    void action.run(async () => {
      await admin.createMarketingTemplate(name.trim(), body.trim())
      setName('')
      setBody('')
    })
  }

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.marketing.templates')}</h3>
      <p className="text-slate-600">{t('web.marketing.nameHint')}</p>
      {(action.error ?? list.error) && <p role="alert" className="text-red-700">{action.error ?? list.error}</p>}
      {list.data?.map((m) => (
        <div key={m.id} className="flex items-start gap-3 border-b py-1">
          <span className="min-w-32 font-medium">{m.name}</span>
          <span className="flex-1 whitespace-pre-wrap">{m.body}{!m.isActive && <em> ({t('web.orders.inactive')})</em>}</span>
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.updateMarketingTemplate(m.id, m.name, m.body, !m.isActive))}>
            {t(m.isActive ? 'web.orders.deactivate' : 'web.orders.activate')}
          </button>
        </div>
      ))}
      <form onSubmit={add} className="flex flex-wrap items-start gap-2">
        <input aria-label={t('web.marketing.templateName')} placeholder={t('web.marketing.templateName')} maxLength={100} className="rounded border px-2 py-1" value={name} onChange={(e) => setName(e.target.value)} />
        <textarea aria-label={t('web.marketing.templateBody')} placeholder={t('web.marketing.templateBody')} maxLength={1000} rows={3} className="min-w-64 flex-1 rounded border px-2 py-1" value={body} onChange={(e) => setBody(e.target.value)} />
        <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !name.trim() || !body.trim()}>
          {t('web.marketing.addTemplate')}
        </button>
      </form>
    </div>
  )
}

function NewCampaign({ admin, onCreated }: { admin: AdminApi; onCreated: (id: string) => void }) {
  const templates = useLoad(useCallback(() => admin.marketingTemplates(), [admin]))
  const [name, setName] = useState('')
  const [template, setTemplate] = useState('')
  const [types, setTypes] = useState<string[]>([])
  const [result, setResult] = useState<{ queued: number; skippedNoPhone: number } | null>(null)
  const action = useAction()

  const toggle = (type: string) => setTypes(types.includes(type) ? types.filter((x) => x !== type) : [...types, type])

  function create(e: FormEvent) {
    e.preventDefault()
    void action.run(async () => {
      const created = await admin.createMarketingCampaign(name.trim(), template, types)
      setResult(created)
      setName('')
      onCreated(created.id)
    })
  }

  return (
    <form onSubmit={create} className="space-y-2 rounded border p-3 text-sm">
      <h3 className="font-semibold">{t('web.marketing.newCampaign')}</h3>
      {(action.error ?? templates.error) && <p role="alert" className="text-red-700">{action.error ?? templates.error}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <input aria-label={t('web.marketing.campaignName')} placeholder={t('web.marketing.campaignName')} maxLength={100} className="rounded border px-2 py-1" value={name} onChange={(e) => setName(e.target.value)} />
        <select aria-label={t('web.marketing.chooseTemplate')} className="rounded border px-2 py-1" value={template} onChange={(e) => setTemplate(e.target.value)}>
          <option value="">{t('web.marketing.chooseTemplate')}</option>
          {(templates.data ?? []).map((m) => (
            <option key={m.id} value={m.id}>
              {m.name}
            </option>
          ))}
        </select>
        {customerTypes.map((type) => (
          <label key={type} className="flex items-center gap-1">
            <input type="checkbox" checked={types.includes(type)} onChange={() => toggle(type)} />
            {type}
          </label>
        ))}
        <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !name.trim() || !template}>
          {t('web.marketing.create')}
        </button>
      </div>
      <p className="text-slate-600">{t('web.marketing.allTypes')}</p>
      {result && <p>{t('web.marketing.created', { queued: String(result.queued), skipped: String(result.skippedNoPhone) })}</p>}
    </form>
  )
}

// The queue of one campaign: the chat opens in a new tab, then the user presses "sent" (or "skip"). A pause between messages is advised, not forced.
function Queue({ admin, campaignId, onChanged }: { admin: AdminApi; campaignId: string; onChanged: () => void }) {
  const messages = useLoad(useCallback(() => admin.marketingMessages(campaignId), [admin, campaignId]))
  const action = useAction(() => {
    messages.reload()
    onChanged()
  })
  const waiting = (messages.data ?? []).filter((m: MarketingMessage) => m.status === 'queued')

  return (
    <div className="space-y-2 rounded border p-3 text-sm">
      <p className="text-slate-600">{t('web.marketing.pause')}</p>
      {(messages.error ?? action.error) && <p role="alert" className="text-red-700">{messages.error ?? action.error}</p>}
      {messages.data && waiting.length === 0 && <p>{t('web.marketing.allDone')}</p>}
      {waiting.map((m) => (
        <div key={m.id} className="flex flex-wrap items-start gap-3 border-b py-1">
          <span className="min-w-32 font-medium">{m.customerName}</span>
          <span className="min-w-28">{m.phone}</span>
          <span className="flex-1 whitespace-pre-wrap">{m.message}</span>
          <a className="rounded border px-2 py-1" target="_blank" rel="noreferrer" href={whatsappUrl(m.phone, m.message) ?? undefined} aria-label={t('web.marketing.openOf', { name: m.customerName })}>
            {t('web.marketing.open')}
          </a>
          <button className="rounded border px-2 py-1" disabled={action.busy} aria-label={t('web.marketing.sentOf', { name: m.customerName })} onClick={() => void action.run(() => admin.markMessageSent(m.id))}>
            {t('web.marketing.markSent')}
          </button>
          <button className="rounded border px-2 py-1" disabled={action.busy} aria-label={t('web.marketing.skipOf', { name: m.customerName })} onClick={() => void action.run(() => admin.skipMessage(m.id))}>
            {t('web.marketing.skip')}
          </button>
        </div>
      ))}
    </div>
  )
}
