import { useEffect, useMemo, useState } from 'react'
import type { Account, ApiClient } from './api/client.ts'
import { AdminApi, can, errorMessage, Permissions, type Me } from './api/admin.ts'
import { LoginPage } from './pages/LoginPage.tsx'
import { HomePage } from './pages/HomePage.tsx'
import { UsersPage } from './pages/UsersPage.tsx'
import { DevicesPage } from './pages/DevicesPage.tsx'
import { CustomersPage } from './pages/CustomersPage.tsx'
import { VisitsPage } from './pages/VisitsPage.tsx'
import { RegionsPage } from './pages/RegionsPage.tsx'
import { LiveMapPage } from './pages/LiveMapPage.tsx'
import { AuditPage } from './pages/AuditPage.tsx'

type Tab = 'home' | 'map' | 'visits' | 'users' | 'devices' | 'customers' | 'regions' | 'audit'

export default function App({ api }: { api: ApiClient }) {
  const [account, setAccount] = useState<Account | null>(() => api.account)

  useEffect(() => api.onSignedOut(() => setAccount(null)), [api])

  if (!account) return <LoginPage api={api} onSignedIn={setAccount} />
  return <SignedIn api={api} account={account} />
}

function SignedIn({ api, account }: { api: ApiClient; account: Account }) {
  const admin = useMemo(() => new AdminApi(api), [api])
  const [me, setMe] = useState<Me | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('home')

  useEffect(() => {
    admin.me().then(setMe, (e: unknown) => setError(errorMessage(e)))
  }, [admin])

  // Tabs follow the user's permissions; hiding is for convenience only, the server checks every request.
  const tabs: { id: Tab; label: string; visible: boolean }[] = [
    { id: 'home', label: 'Home', visible: true },
    { id: 'map', label: 'Live map', visible: can(me, ...Permissions.liveMap) },
    { id: 'visits', label: 'Visits', visible: can(me, ...Permissions.planVisits) },
    { id: 'users', label: 'Users', visible: can(me, ...Permissions.users) },
    { id: 'devices', label: 'Devices', visible: can(me, ...Permissions.devices) },
    { id: 'customers', label: 'Customers', visible: can(me, ...Permissions.customers, ...Permissions.approveCustomers) },
    { id: 'regions', label: 'Regions & teams', visible: can(me, ...Permissions.regions) },
    { id: 'audit', label: 'Audit', visible: can(me, ...Permissions.audit) },
  ]

  return (
    <div className="min-h-screen">
      <header className="flex items-center justify-between border-b bg-white px-6 py-3">
        <nav className="flex items-center gap-4">
          <span className="font-semibold">LifeDrop</span>
          {tabs
            .filter((t) => t.visible)
            .map((t) => (
              <button key={t.id} className={t.id === tab ? 'font-medium underline' : 'text-slate-600'} aria-current={t.id === tab ? 'page' : undefined} onClick={() => setTab(t.id)}>
                {t.label}
              </button>
            ))}
        </nav>
        <button className="rounded border px-3 py-1 text-sm" onClick={() => void api.logout()}>
          Sign out
        </button>
      </header>
      {error && <p role="alert" className="p-6 text-sm text-red-700">{error}</p>}
      {tab === 'home' && <HomePage account={account} />}
      {me && tab === 'users' && <UsersPage admin={admin} me={me} />}
      {me && tab === 'devices' && <DevicesPage admin={admin} />}
      {me && tab === 'customers' && <CustomersPage admin={admin} me={me} />}
      {me && tab === 'map' && <LiveMapPage admin={admin} />}
      {me && tab === 'visits' && <VisitsPage admin={admin} me={me} />}
      {me && tab === 'regions' && <RegionsPage admin={admin} me={me} />}
      {me && tab === 'audit' && <AuditPage admin={admin} />}
    </div>
  )
}
