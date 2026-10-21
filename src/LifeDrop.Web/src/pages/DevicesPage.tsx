import { useCallback, useState } from 'react'
import { AdminApi, type Device, type DeviceStatus } from '../api/admin.ts'
import { Badge } from '../components/Badge.tsx'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'
import { SortTh, useSort } from '../components/sort.tsx'

const filters: { value: DeviceStatus | 'all'; label: TextKey }[] = [
  { value: 'pending', label: 'web.devices.pending' },
  { value: 'approved', label: 'web.devices.approved' },
  { value: 'blocked', label: 'web.devices.blocked' },
  { value: 'all', label: 'web.devices.all' },
]
const statusText: Record<DeviceStatus, TextKey> = { pending: 'web.devices.pending', approved: 'web.devices.approved', blocked: 'web.devices.blocked' }

// Only shown to users with a device permission; the server checks approve/block again.
export function DevicesPage({ admin }: { admin: AdminApi }) {
  const [filter, setFilter] = useState<DeviceStatus | 'all'>('pending')
  const [platform, setPlatform] = useState('')
  const [search, setSearch] = useState('')
  const devices = useLoad(useCallback(() => admin.devices(filter === 'all' ? undefined : filter, platform || undefined, search), [admin, filter, platform, search]))
  const action = useAction(devices.reload)
  const sorted = useSort(devices.data, { user: (d) => d.userEmail ?? d.userId, device: (d) => d.adminLabel || d.label, app: (d) => d.appVersion, registered: (d) => d.registeredAt, status: (d) => d.status })

  function block(device: Device) {
    if (!window.confirm(t('web.devices.confirmBlock', { device: device.label, user: device.userEmail ?? device.userId }))) return
    void action.run(() => admin.blockDevice(device.id))
  }

  function rename(device: Device) {
    const label = window.prompt(t('web.devices.labelPrompt', { device: device.label }), device.adminLabel ?? '')
    if (label !== null) void action.run(() => admin.setDeviceLabel(device.id, label.trim() || null))
  }

  function remove(device: Device) {
    if (window.confirm(t('web.devices.confirmDelete', { device: device.adminLabel ?? device.label, user: device.userEmail ?? device.userId }))) void action.run(() => admin.deleteDevice(device.id))
  }

  return (
    <section className="p-6">
      <h2 className="mb-4 text-xl font-semibold">{t('web.devices.title')}</h2>
      <label className="mb-4 block text-sm">
        {t('web.devices.show')}{' '}
        <select className="rounded border px-1 py-1" value={filter} onChange={(e) => setFilter(e.target.value as DeviceStatus | 'all')}>
          {filters.map((f) => (
            <option key={f.value} value={f.value}>
              {t(f.label)}
            </option>
          ))}
        </select>
      </label>
      <div className="mb-4 flex flex-wrap items-center gap-3 text-sm">
        <select aria-label={t('web.devices.platform')} className="rounded border px-1 py-1" value={platform} onChange={(e) => setPlatform(e.target.value)}>
          <option value="">{t('web.devices.allPlatforms')}</option>
          <option value="android">Android</option>
          <option value="ios">iPhone</option>
        </select>
        <input aria-label={t('web.devices.search')} placeholder={t('web.devices.search')} className="rounded border px-2 py-1" maxLength={100} value={search} onChange={(e) => setSearch(e.target.value)} />
      </div>
      {(devices.error ?? action.error) && <p role="alert" className="mb-3 text-sm text-red-700">{action.error ?? devices.error}</p>}
      {devices.data?.length === 0 && <p className="text-slate-600">{t('web.devices.none')}</p>}
      {devices.data && devices.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <SortTh id="user" className="py-2" sort={sorted.sort} onSort={sorted.toggle}>{t('web.devices.user')}</SortTh>
              <SortTh id="device" sort={sorted.sort} onSort={sorted.toggle}>{t('web.devices.device')}</SortTh>
              <SortTh id="app" sort={sorted.sort} onSort={sorted.toggle}>{t('web.devices.app')}</SortTh>
              <SortTh id="registered" sort={sorted.sort} onSort={sorted.toggle}>{t('web.devices.registered')}</SortTh>
              <SortTh id="status" sort={sorted.sort} onSort={sorted.toggle}>{t('web.devices.status')}</SortTh>
              <th />
            </tr>
          </thead>
          <tbody>
            {(sorted.rows ?? devices.data).map((device) => (
              <tr key={device.id} className="border-b">
                <td className="py-2">{device.userEmail ?? device.userId}</td>
                <td>
                  {device.adminLabel ? <strong>{device.adminLabel}</strong> : null} {device.label} ({device.platform === 'ios' ? 'iPhone' : 'Android'})
                  {device.model ? ` · ${device.model}` : ''}
                </td>
                <td>
                  {device.appVersion}
                  {device.osVersion ? ` · ${device.osVersion}` : ''}
                </td>
                <td>{new Date(device.registeredAt).toLocaleString('en-GB', { timeZone: 'Asia/Baghdad' })}</td>
                <td><Badge tone={({ approved: 'good', pending: 'warn', blocked: 'bad' } as const)[device.status]}>{t(statusText[device.status])}</Badge></td>
                <td className="space-x-2 py-2 text-end">
                  <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => rename(device)}>
                    {t('web.devices.rename')}
                  </button>
                  <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} onClick={() => remove(device)}>
                    {t('web.devices.delete')}
                  </button>
                  {device.status === 'pending' && (
                    <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => void action.run(() => admin.approveDevice(device.id))}>
                      {t('web.action.approve')}
                    </button>
                  )}
                  {device.status !== 'blocked' && (
                    <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} onClick={() => block(device)}>
                      {t('web.action.block')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
