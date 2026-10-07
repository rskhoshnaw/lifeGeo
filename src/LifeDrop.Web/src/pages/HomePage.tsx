import type { Account } from '../api/client.ts'
import { iconFor } from '../components/nav.ts'
import { t } from '../i18n.ts'

// Landing page after sign-in: who you are signed in as and a door to every screen your role may use (the menu has the same, this is the quick start).
export function HomePage({ account, shortcuts, onOpen }: { account: Account; shortcuts: { id: string; label: string }[]; onOpen: (id: string) => void }) {
  return (
    <section className="mx-auto max-w-5xl space-y-8 p-6 lg:p-10">
      <div>
        <h2 className="text-2xl font-bold tracking-tight">{t('web.home.welcome')}</h2>
        <p className="mt-1 text-slate-600">{t('web.home.signedInAs', { email: account.email, tenant: account.tenantCode })}</p>
      </div>
      {shortcuts.length > 0 && (
        <div>
          <h3 className="eyebrow mb-3">{t('web.home.shortcuts')}</h3>
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {shortcuts.map((s) => {
              const Icon = iconFor(s.id)
              return (
                <li key={s.id}>
                  <a
                    href={`#${s.id}`}
                    className="panel flex w-full items-center gap-3 px-4 py-3.5 text-start text-slate-900 no-underline transition-colors hover:border-brand-300 hover:bg-brand-50/60"
                    onClick={(e) => {
                      e.preventDefault()
                      onOpen(s.id)
                    }}
                  >
                    <span className="grid size-9 shrink-0 place-items-center rounded-md bg-brand-50 text-brand-700">
                      <Icon className="size-[1.15rem]" strokeWidth={1.75} aria-hidden="true" />
                    </span>
                    <span className="font-medium">{s.label}</span>
                  </a>
                </li>
              )
            })}
          </ul>
        </div>
      )}
    </section>
  )
}
