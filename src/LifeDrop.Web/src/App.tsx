import { useCallback, useEffect, useMemo, useState } from 'react'
import type { Account, ApiClient } from './api/client.ts'
import { AdminApi, can, errorMessage, Permissions, type Me } from './api/admin.ts'
import { LoginPage } from './pages/LoginPage.tsx'
import { HomePage } from './pages/HomePage.tsx'
import { UsersPage } from './pages/UsersPage.tsx'
import { AccountPage } from './pages/AccountPage.tsx'
import { DevicesPage } from './pages/DevicesPage.tsx'
import { CustomersPage } from './pages/CustomersPage.tsx'
import { VisitsPage } from './pages/VisitsPage.tsx'
import { RegionsPage } from './pages/RegionsPage.tsx'
import { LiveMapPage } from './pages/LiveMapPage.tsx'
import { AuditPage } from './pages/AuditPage.tsx'
import { CatalogPage } from './pages/CatalogPage.tsx'
import { OrdersPage } from './pages/OrdersPage.tsx'
import { ReportsPage } from './pages/ReportsPage.tsx'
import { FormsPage } from './pages/FormsPage.tsx'
import { TvPage } from './pages/TvPage.tsx'
import { PlatformPage } from './pages/PlatformPage.tsx'
import { RouteHistoryPage } from './pages/RouteHistoryPage.tsx'
import { AlertsPage } from './pages/AlertsPage.tsx'
import { FollowUpsPage } from './pages/FollowUpsPage.tsx'
import { SamplesPage } from './pages/SamplesPage.tsx'
import { PaymentsPage } from './pages/PaymentsPage.tsx'
import { WarehousePage } from './pages/WarehousePage.tsx'
import { PlacesPage } from './pages/PlacesPage.tsx'
import { NotificationsPage } from './pages/NotificationsPage.tsx'
import { I18nProvider } from './components/I18n.tsx'
import { Shell } from './components/Shell.tsx'
import { setCurrentProfile, t, useLang } from './i18n.ts'

type Tab = 'home' | 'map' | 'visits' | 'users' | 'devices' | 'customers' | 'places' | 'catalog' | 'orders' | 'payments' | 'samples' | 'followups' | 'alerts' | 'route' | 'reports' | 'warehouse' | 'regions' | 'audit' | 'forms' | 'platform' | 'tv' | 'notifications' | 'account'

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
  const [alertCount, setAlertCount] = useState(0)
  const [, setWording] = useState('')

  // Wording of the company (a food distributor says shops and restaurants, not pharmacies and doctors).
  useEffect(() => {
    void api.industry(account.tenantCode).then((industry) => {
      setCurrentProfile(industry === 'food' ? 'food' : '')
      setWording(industry)
    })
  }, [api, account.tenantCode])

  // The unread count next to the Notifications tab: read when the page opens and again every minute. A failure is ignored (the tab
  // still works, the number just stays).
  const refreshUnread = useCallback(() => {
    admin.unreadCount().then(setUnread, () => {})
  }, [admin])
  // The number of new alerts next to the Alerts tab, for users who may read alerts.
  const canAlerts = can(me, ...Permissions.alerts)
  const refreshAlerts = useCallback(() => {
    if (canAlerts) admin.alertCount().then(setAlertCount, () => {})
  }, [admin, canAlerts])
  useEffect(() => {
    refreshAlerts()
    const timer = setInterval(refreshAlerts, 60_000)
    return () => clearInterval(timer)
  }, [refreshAlerts])
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
    { id: 'payments', label: t('web.tab.payments'), visible: can(me, ...Permissions.payments) },
    { id: 'samples', label: t('web.tab.samples'), visible: can(me, ...Permissions.samples) },
    { id: 'followups', label: t('web.tab.followups'), visible: can(me, ...Permissions.followUps) },
    { id: 'alerts', label: alertCount > 0 ? `${t('web.tab.alerts')} (${alertCount})` : t('web.tab.alerts'), visible: canAlerts },
    { id: 'route', label: t('web.tab.route'), visible: can(me, ...Permissions.routeHistory) },
    { id: 'reports', label: t('web.tab.reports'), visible: can(me, ...Permissions.reports) },
    { id: 'warehouse', label: t('web.tab.warehouse'), visible: can(me, ...Permissions.warehouseWork) },
    { id: 'regions', label: t('web.tab.regions'), visible: can(me, ...Permissions.regions) },
    { id: 'audit', label: t('web.tab.audit'), visible: can(me, ...Permissions.audit) },
    { id: 'tv', label: t('web.tab.tv'), visible: can(me, ...Permissions.reports) },
    { id: 'forms', label: t('web.tab.forms'), visible: can(me, ...Permissions.forms) },
    { id: 'platform', label: t('web.tab.platform'), visible: can(me, ...Permissions.platform) },
    { id: 'account', label: t('web.tab.account'), visible: true },
  ]
  const notificationsLabel = unread > 0 ? `${t('web.tab.notifications')} (${unread})` : t('web.tab.notifications')

  return (
    <Shell
      items={tabs}
      current={tab}
      onSelect={(id) => setTab(id as Tab)}
      notifications={{ id: 'notifications', label: notificationsLabel, count: unread }}
      email={account.email}
      company={account.tenantCode}
      onSignOut={() => void api.logout()}
    >
      {error && <p role="alert" className="p-6 text-sm text-red-700">{error}</p>}
      {tab === 'home' && <HomePage account={account} shortcuts={tabs.filter((x) => x.visible && x.id !== 'home')} onOpen={(id) => setTab(id as Tab)} />}
      {me && tab === 'account' && <AccountPage admin={admin} me={me} />}
      {me && tab === 'users' && <UsersPage admin={admin} me={me} />}
      {me && tab === 'devices' && <DevicesPage admin={admin} />}
      {me && tab === 'customers' && <CustomersPage admin={admin} me={me} />}
      {me && tab === 'map' && <LiveMapPage admin={admin} />}
      {me && tab === 'visits' && <VisitsPage admin={admin} me={me} />}
      {me && tab === 'places' && <PlacesPage admin={admin} />}
      {me && tab === 'catalog' && <CatalogPage admin={admin} me={me} />}
      {me && tab === 'orders' && <OrdersPage admin={admin} me={me} />}
      {me && tab === 'payments' && <PaymentsPage admin={admin} me={me} />}
      {me && tab === 'samples' && <SamplesPage admin={admin} me={me} />}
      {me && tab === 'followups' && <FollowUpsPage admin={admin} me={me} />}
      {me && tab === 'alerts' && <AlertsPage admin={admin} me={me} onChanged={refreshAlerts} />}
      {me && tab === 'route' && <RouteHistoryPage admin={admin} />}
      {me && tab === 'reports' && <ReportsPage admin={admin} />}
      {me && tab === 'warehouse' && <WarehousePage admin={admin} />}
      {me && tab === 'regions' && <RegionsPage admin={admin} me={me} />}
      {me && tab === 'audit' && <AuditPage admin={admin} />}
      {me && tab === 'tv' && <TvPage admin={admin} me={me} />}
      {me && tab === 'forms' && <FormsPage admin={admin} />}
      {me && tab === 'platform' && <PlatformPage admin={admin} />}
      {me && tab === 'notifications' && <NotificationsPage admin={admin} me={me} onChanged={refreshUnread} />}
    </Shell>
  )
}
