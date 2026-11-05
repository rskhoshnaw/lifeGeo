import { t } from '../i18n.ts'
import { whatsappUrl } from './whatsapp.ts'

// A small link that opens the WhatsApp chat with a person (a staff member or a customer). Nothing shows when there is no usable number.
export function WhatsAppButton({ phone, who }: { phone: string | null | undefined; who: string }) {
  const url = whatsappUrl(phone)
  if (!url) return null
  return (
    <a href={url} target="_blank" rel="noopener noreferrer" aria-label={t('web.whatsapp.to', { who })} title={t('web.whatsapp.to', { who })} className="ms-2 rounded border px-1.5 py-0.5 text-xs text-green-700">
      {t('web.customers.whatsapp')}
    </a>
  )
}
