import { useState, type FormEvent } from 'react'
import { Credit } from '../components/Credit.tsx'
import { t, type TextKey } from '../i18n.ts'
import { LanguagePicker } from '../components/I18n.tsx'
import { Logo } from '../components/Shell.tsx'
import { contours } from '../components/contours.ts'
import { companyFromSearch } from '../components/inviteLinks.ts'
import { readRemembered, saveRemembered } from '../components/rememberMe.ts'
import { LoginFailed, type Account, type ApiClient, type LoginError } from '../api/client.ts'

const messages: Record<LoginError, TextKey> = {
  invalid_credentials: 'web.login.error.invalid_credentials',
  device_only: 'web.login.error.device_only',
  too_many_attempts: 'web.login.error.too_many_attempts',
  network: 'web.login.error.network',
  server: 'web.login.error.server',
}

export function LoginPage({ api, onSignedIn }: { api: ApiClient; onSignedIn: (account: Account) => void }) {
  // A company link (…/?company=acme, plan step 4.7) fills the company code; the user only types the e-mail and the password.
  const [remembered] = useState(readRemembered)
  const [tenantCode, setTenantCode] = useState(() => companyFromSearch(window.location.search) || remembered?.tenantCode || '')
  const [email, setEmail] = useState(remembered?.email ?? '')
  const [remember, setRemember] = useState(remembered !== null)
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    if (!tenantCode.trim() || !email.trim() || !password) {
      setError(t('web.login.fillAll'))
      return
    }
    setBusy(true)
    setError(null)
    try {
      const account = await api.login(tenantCode.trim(), email.trim(), password)
      saveRemembered(remember, { tenantCode: tenantCode.trim(), email: email.trim() })
      onSignedIn(account)
    } catch (e) {
      setError(t(messages[e instanceof LoginFailed ? e.reason : 'server']))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="grid min-h-screen lg:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)]">
      {/* The brand side: contour lines of a hill, the mark and one sentence about what the product is for. */}
      <section className="relative hidden overflow-hidden bg-brand-800 text-brand-50 lg:block" aria-hidden="true">
        <svg viewBox="0 0 600 600" preserveAspectRatio="xMidYMid slice" className="absolute inset-0 size-full text-brand-300 opacity-[0.28]" fill="none" stroke="currentColor" strokeWidth="1">
          {contours.map((d, i) => (
            <path key={i} d={d} />
          ))}
        </svg>
        <div className="relative flex h-full flex-col justify-between p-12">
          <div className="flex items-center gap-3">
            <Logo className="size-9 text-brand-200" />
            <span className="text-2xl font-bold tracking-tight">LifeDrop</span>
          </div>
          <p className="max-w-md text-[1.7rem] font-semibold leading-snug text-white">{t('web.login.tagline')}</p>
        </div>
      </section>

      <section className="flex flex-col px-6 py-6 sm:px-12">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2.5 text-brand-700 lg:invisible">
            <Logo />
            <span className="text-lg font-bold tracking-tight text-slate-900">LifeDrop</span>
          </div>
          <LanguagePicker />
        </div>
        <div className="mx-auto flex w-full max-w-sm flex-1 flex-col justify-center pb-12">
          <h1 className="mb-6 text-2xl font-bold tracking-tight">{t('web.login.title')}</h1>
          <form onSubmit={submit} className="space-y-4" noValidate>
            <label className="block">
              <span className="text-sm font-medium text-slate-700">{t('web.login.companyCode')}</span>
              <input className="mt-1.5 w-full" value={tenantCode} onChange={(e) => setTenantCode(e.target.value)} autoComplete="organization" />
            </label>
            <label className="block">
              <span className="text-sm font-medium text-slate-700">{t('web.login.email')}</span>
              <input className="mt-1.5 w-full" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" />
            </label>
            <label className="block">
              <span className="text-sm font-medium text-slate-700">{t('web.login.password')}</span>
              <input className="mt-1.5 w-full" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
            </label>
            <label className="flex items-center gap-2 text-sm text-slate-700">
              <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
              {t('web.login.remember')}
            </label>
            {error && <p role="alert" className="rounded-md bg-red-100 px-3 py-2 text-sm text-red-700">{error}</p>}
            <button type="submit" disabled={busy} className="w-full rounded-md bg-brand-700 px-3 py-2.5 text-white disabled:opacity-50">
              {busy ? t('web.login.busy') : t('web.login.submit')}
            </button>
          </form>
          <Credit className="mt-8" />
        </div>
      </section>
    </main>
  )
}
