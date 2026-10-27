import { useCallback } from 'react'
import { AdminApi } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

// Whether the customer agreed to receive WhatsApp messages from the company (needed for campaigns sent by the WhatsApp Business API, docs/WHATSAPP-API.md). Nothing shows when it cannot be read.
export function WhatsAppConsent({ admin, customerId }: { admin: AdminApi; customerId: string }) {
  const consent = useLoad(useCallback(() => admin.whatsAppConsent(customerId), [admin, customerId]))
  const action = useAction(consent.reload)
  if (consent.error || !consent.data) return null
  const optIn = consent.data.optIn
  return (
    <div className="mt-2 flex flex-wrap items-center gap-3">
      <span>{t(optIn ? 'web.whatsappConsent.yes' : 'web.whatsappConsent.no')}</span>
      <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.setWhatsAppConsent(customerId, !optIn))}>
        {t(optIn ? 'web.whatsappConsent.withdraw' : 'web.whatsappConsent.give')}
      </button>
      <span className="text-slate-600">{t('web.whatsappConsent.hint')}</span>
      {action.error && <span role="alert" className="text-red-700">{action.error}</span>}
    </div>
  )
}
