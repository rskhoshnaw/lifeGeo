import { useCallback, useState } from 'react'
import { AdminApi, errorMessage, type TelegramLink } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

// The user's own Telegram link (plan step 3.4, D17). Linking is finished in Telegram: the user opens the link (or sends the code) to the company bot, which proves the
// chat is theirs. Shown only as a short note while the company has not turned the channels on.
export function TelegramCard({ admin }: { admin: AdminApi }) {
  const status = useLoad(useCallback(() => admin.channels(), [admin]))
  const [link, setLink] = useState<TelegramLink | null>(null)
  const action = useAction(() => status.reload())
  const [failed, setFailed] = useState<string | null>(null)

  async function connect() {
    setFailed(null)
    try {
      setLink(await admin.startTelegramLink())
    } catch (e) {
      setFailed(errorMessage(e))
    }
  }

  const data = status.data
  if (!data) return status.error ? <p role="alert" className="text-red-700">{status.error}</p> : null
  const bot = link?.botUsername ?? data.botUsername

  return (
    <div className="panel space-y-2 p-4" aria-label={t('web.channels.title')}>
      <h3 className="font-medium">{t('web.channels.title')}</h3>
      {!data.enabled && <p className="text-slate-600">{t('web.channels.off')}</p>}
      {data.enabled && data.telegramLinked && (
        <>
          <p>{t('web.channels.connected')}</p>
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(async () => { setLink(null); await admin.unlinkTelegram() })}>
            {t('web.channels.disconnect')}
          </button>
        </>
      )}
      {data.enabled && !data.telegramLinked && !link && (
        <button className="rounded border px-2 py-1" onClick={() => void connect()}>
          {t('web.channels.connect')}
        </button>
      )}
      {data.enabled && !data.telegramLinked && link && (
        <div className="space-y-2">
          {bot && (
            <a className="inline-block rounded bg-brand-700 px-3 py-1.5 text-white" href={`https://t.me/${bot}?start=${encodeURIComponent(link.code)}`} target="_blank" rel="noopener noreferrer">
              {t('web.channels.open')}
            </a>
          )}
          <p className="text-slate-600">{bot ? t('web.channels.sendTo', { bot }) : t('web.channels.sendToBot')}</p>
          <code dir="ltr" className="block w-fit rounded bg-slate-100 px-2 py-1">/start {link.code}</code>
          <p className="text-slate-600">{t('web.channels.expires')}</p>
          <button className="rounded border px-2 py-1" onClick={() => status.reload()}>
            {t('web.channels.check')}
          </button>
        </div>
      )}
      {(failed ?? action.error) && <p role="alert" className="text-red-700">{failed ?? action.error}</p>}
      {data.enabled && <p className="text-xs text-slate-500">{t('web.channels.privacy')}</p>}
    </div>
  )
}
