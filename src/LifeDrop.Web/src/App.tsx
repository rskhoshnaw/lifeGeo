import { useEffect, useState } from 'react'
import type { Account, ApiClient } from './api/client.ts'
import { LoginPage } from './pages/LoginPage.tsx'
import { HomePage } from './pages/HomePage.tsx'

export default function App({ api }: { api: ApiClient }) {
  const [account, setAccount] = useState<Account | null>(() => api.account)

  useEffect(() => api.onSignedOut(() => setAccount(null)), [api])

  if (!account) return <LoginPage api={api} onSignedIn={setAccount} />

  return (
    <div className="min-h-screen">
      <header className="flex items-center justify-between border-b bg-white px-6 py-3">
        <span className="font-semibold">LifeDrop</span>
        <button className="rounded border px-3 py-1 text-sm" onClick={() => void api.logout()}>
          Sign out
        </button>
      </header>
      <HomePage account={account} />
    </div>
  )
}
