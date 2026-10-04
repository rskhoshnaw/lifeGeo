import { useState, type FormEvent } from 'react'
import { LoginFailed, type Account, type ApiClient, type LoginError } from '../api/client.ts'

const messages: Record<LoginError, string> = {
  invalid_credentials: 'Wrong company code, email or password.',
  device_only: 'This account can sign in only from its approved phone.',
  too_many_attempts: 'Too many attempts. Wait a minute and try again.',
  network: 'No connection to the server. Check your internet and try again.',
  server: 'The server could not sign you in. Try again later.',
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
      setError('Fill in all fields.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await api.login(tenantCode.trim(), email.trim(), password))
    } catch (e) {
      setError(messages[e instanceof LoginFailed ? e.reason : 'server'])
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="mx-auto mt-24 max-w-sm px-4">
      <h1 className="mb-6 text-2xl font-semibold">LifeDrop</h1>
      <form onSubmit={submit} className="space-y-4" noValidate>
        <label className="block">
          <span className="text-sm">Company code</span>
          <input className="mt-1 w-full rounded border px-3 py-2" value={tenantCode} onChange={(e) => setTenantCode(e.target.value)} autoComplete="organization" />
        </label>
        <label className="block">
          <span className="text-sm">Email</span>
          <input className="mt-1 w-full rounded border px-3 py-2" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" />
        </label>
        <label className="block">
          <span className="text-sm">Password</span>
          <input className="mt-1 w-full rounded border px-3 py-2" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
        </label>
        {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
        <button type="submit" disabled={busy} className="w-full rounded bg-slate-900 px-3 py-2 text-white disabled:opacity-50">
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </main>
  )
}
