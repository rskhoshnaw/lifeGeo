import { useCallback, useEffect, useMemo, useState } from 'react'
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
import { CatalogPage } from './pages/CatalogPage.tsx'
import { OrdersPage } from './pages/OrdersPage.tsx'
import { WarehousePage } from './pages/WarehousePage.tsx'
import { PlacesPage } from './pages/PlacesPage.tsx'
import { NotificationsPage } from './pages/NotificationsPage.tsx'
import { I18nProvider, LanguagePicker } from './components/I18n.tsx'
import { t, useLang } from './i18n.ts'

type Tab = 'home' | 'map' | 'visits' | 'users' | 'devices' | 'customers' | 'places' | 'catalog' | 'orders' | 'warehouse' | 'regions' | 'audit' | 'notifications'

export default function App({ api }: { api: ApiClient }) {
  return (
    <I18nProvider>
      <Root api={api} />
    </I18nProvider>
  )
}

function Root({ api }: { api: ApiClient }) {
  useLang() // re-render everything when the language changes
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
  const [unread, setUnread] = useState(0)

  // The unread count next to the Notifications tab: read when the page opens and again every minute. A failure is ignored (the tab
  // still works, the number just stays).
  const refreshUnread = useCallback(() => {
    admin.unreadCount().then(setUnread, () => {})
  }, [admin])
  useEffect(() => {
    refreshUnread()
    const timer = setInterval(refreshUnread, 60_000)
    return () => clearInterval(timer)
  }, [refreshUnread])

  useEffect(() => {
    admin.me().then(setMe, (e: unknown) => setError(errorMessage(e)))
  }, [admin])

  // Tabs follow the user's permissions; hiding is for convenience only, the server checks every request.
  const tabs: { id: Tab; label: string; visible: boolean }[] = [
    { id: 'home', label: t('web.tab.home'), visible: true },
    { id: 'map', label: t('web.tab.map'), visible: can(me, ...Permissions.liveMap) },
    { id: 'visits', label: t('web.tab.visits'), visible: can(me, ...Permissions.planVisits) },
    { id: 'users', label: t('web.tab.users'), visible: can(me, ...Permissions.users) },
    { id: 'devices', label: t('web.tab.devices'), visible: can(me, ...Permissions.devices) },
    { id: 'customers', label: t('web.tab.customers'), visible: can(me, ...Permissions.customers, ...Permissions.approveCustomers) },
    { id: 'places', label: t('web.tab.places'), visible: can(me, ...Permissions.places) },
    { id: 'catalog', label: t('web.tab.catalog'), visible: can(me, ...Permissions.catalog) },
    { id: 'orders', label: t('web.tab.orders'), visible: can(me, ...Permissions.orders) },
    { id: 'warehouse', label: t('web.tab.warehouse'), visible: can(me, ...Permissions.warehouseWork) },
    { id: 'regions', label: t('web.tab.regions'), visible: can(me, ...Permissions.regions) },
    { id: 'audit', label: t('web.tab.audit'), visible: can(me, ...Permissions.audit) },
    { id: 'notifications', label: unread > 0 ? `${t('web.tab.notifications')} (${unread})` : t('web.tab.notifications'), visible: true },
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
        <div className="flex items-center gap-2">
          <LanguagePicker />
          <button className="rounded border px-3 py-1 text-sm" onClick={() => void api.logout()}>
            {t('web.signOut')}
          </button>
        </div>
      </header>
      {error && <p role="alert" className="p-6 text-sm text-red-700">{error}</p>}
      {tab === 'home' && <HomePage account={account} />}
      {me && tab === 'users' && <UsersPage admin={admin} me={me} />}
      {me && tab === 'devices' && <DevicesPage admin={admin} />}
      {me && tab === 'customers' && <CustomersPage admin={admin} me={me} />}
      {me && tab === 'map' && <LiveMapPage admin={admin} />}
      {me && tab === 'visits' && <VisitsPage admin={admin} me={me} />}
      {me && tab === 'places' && <PlacesPage admin={admin} />}
      {me && tab === 'catalog' && <CatalogPage admin={admin} me={me} />}
      {me && tab === 'orders' && <OrdersPage admin={admin} me={me} />}
      {me && tab === 'warehouse' && <WarehousePage admin={admin} />}
      {me && tab === 'regions' && <RegionsPage admin={admin} me={me} />}
      {me && tab === 'audit' && <AuditPage admin={admin} />}
      {me && tab === 'notifications' && <NotificationsPage admin={admin} onChanged={refreshUnread} />}
    </div>
  )
}
