import { t } from '../i18n.ts'
import { whatsappUrl } from './whatsapp.ts'

const supportPhone = '+9647722656566'

/** Developer credit and support line, shown on the sign-in screen and at the foot of the menu. */
export function Credit({ className = '' }: { className?: string }) {
  return (
    <p className={`text-xs text-slate-500 ${className}`}>
      <a href={whatsappUrl(supportPhone) ?? undefined} target="_blank" rel="noopener noreferrer" dir="ltr" className="inline-block font-medium underline">
        {t('web.support', { phone: supportPhone })}
      </a>
      <span aria-hidden> · </span>
      <span dir="ltr" className="inline-block">
        © 2026{' '}
        <a href="https://pixelo-design.com/" target="_blank" rel="noopener noreferrer" className="font-medium">
          Pixelo Design
        </a>
      </span>
    </p>
  )
}
