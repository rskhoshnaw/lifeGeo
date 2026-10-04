import { useState, type FormEvent } from 'react'
import { t, type TextKey } from '../i18n.ts'
import { LanguagePicker } from '../components/I18n.tsx'
import { LoginFailed, type Account, type ApiClient, type LoginError } from '../api/client.ts'

const messages: Record<LoginError, TextKey> = {
  invalid_credentials: 'web.login.error.invalid_credentials',
  device_only: 'web.login.error.device_only',
  too_many_attempts: 'web.login.error.too_many_attempts',
  network: 'web.login.error.network',
  server: 'web.login.error.server',
}

export function LoginPage({ api, onSignedIn }: { api: ApiClient; onSignedIn: (account: Account) => void }) {
  const [tenantCode, setTenantCode] = useState('')
  const [email, setEmail] = useState('')
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
      onSignedIn(await api.login(tenantCode.trim(), email.trim(), password))
    } catch (e) {
      setError(t(messages[e instanceof LoginFailed ? e.reason : 'server']))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="mx-auto mt-24 max-w-sm px-4">
      <div className="mb-6 flex items-center justify-between">
        <h1 className="text-2xl font-semibold">LifeDrop</h1>
        <LanguagePicker />
      </div>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <label className="block">
          <span className="text-sm">{t('web.login.companyCode')}</span>
          <input className="mt-1 w-full rounded border px-3 py-2" value={tenantCode} onChange={(e) => setTenantCode(e.target.value)} autoComplete="organization" />
        </label>
        <label className="block">
          <span className="text-sm">{t('web.login.email')}</span>
          <input className="mt-1 w-full rounded border px-3 py-2" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" />
        </label>
        <label className="block">
          <span className="text-sm">{t('web.login.password')}</span>
          <input className="mt-1 w-full rounded border px-3 py-2" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
        </label>
        {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
        <button type="submit" disabled={busy} className="w-full rounded bg-slate-900 px-3 py-2 text-white disabled:opacity-50">
          {busy ? t('web.login.busy') : t('web.login.submit')}
        </button>
      </form>
    </main>
  )
}
