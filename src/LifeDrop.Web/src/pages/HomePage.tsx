import type { Account } from '../api/client.ts'
import { t } from '../i18n.ts'

// Landing page after sign-in; management screens are in the header tabs.
export function HomePage({ account }: { account: Account }) {
  return (
    <section className="p-6">
      <h2 className="text-xl font-semibold">{t('web.home.welcome')}</h2>
      <p className="mt-2 text-slate-600">
        {t('web.home.signedInAs', { email: account.email, tenant: account.tenantCode })}
      </p>
    </section>
  )
}
