import { useCallback } from 'react'
import { AdminApi, type Notification } from '../api/admin.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { TelegramCard } from '../components/TelegramCard.tsx'
import { t } from '../i18n.ts'

const when = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short', timeStyle: 'short' })

// The signed-in user's messages (plan step 1.23-c): who changed which customer of the user's area. Everybody sees only
// their own. Reading a message does not change the customer; it only clears the unread count.
export function NotificationsPage({ admin, onChanged }: { admin: AdminApi; onChanged: () => void }) {
  const list = useLoad(useCallback(() => admin.notifications(), [admin]))
  const action = useAction(() => {
    list.reload()
    onChanged()
  })
  const items = list.data ?? []
  const unread = items.filter((n) => n.readAt === null).length
  const text = (n: Notification) =>
    t(n.kind === 'customer.location_changed' ? 'web.notifications.locationChanged' : 'web.notifications.profileChanged', {
      actor: n.actorEmail ?? t('web.notifications.someone'),
      customer: n.customerCode ? `${n.customerCode}${n.customerName ? ` · ${n.customerName}` : ''}` : t('web.notifications.aCustomer'),
    })

  return (
    <section className="space-y-4 p-6 text-sm">
      <div className="flex items-center gap-4">
        <h2 className="text-xl font-semibold">{t('web.notifications.title')}</h2>
        {unread > 0 && (
          <>
            <span>{t('web.notifications.unreadBadge', { count: unread })}</span>
            <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.markAllRead())}>
              {t('web.notifications.markAllRead')}
            </button>
          </>
        )}
      </div>
      <TelegramCard admin={admin} />
      {(action.error ?? list.error) && <p role="alert" className="text-red-700">{action.error ?? list.error}</p>}
      {list.data && items.length === 0 && <p className="text-slate-600">{t('web.notifications.none')}</p>}
      <ul className="space-y-2">
        {items.map((n) => (
          <li key={n.id} className="flex flex-wrap items-center gap-3 border-b pb-2">
            <span className={n.readAt === null ? 'font-semibold' : 'text-slate-600'}>{text(n)}</span>
            <time className="text-slate-500" dateTime={n.createdAt}>
              {when.format(new Date(n.createdAt))}
            </time>
            {n.readAt === null && (
              <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.markRead(n.id))}>
                {t('web.notifications.markRead')}
              </button>
            )}
          </li>
        ))}
      </ul>
    </section>
  )
}
