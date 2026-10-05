import { useCallback, useState } from 'react'
import { AdminApi, type Device, type DeviceStatus } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

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
  const devices = useLoad(useCallback(() => admin.devices(filter === 'all' ? undefined : filter), [admin, filter]))
  const action = useAction(devices.reload)

  function block(device: Device) {
    if (!window.confirm(t('web.devices.confirmBlock', { device: device.label, user: device.userEmail ?? device.userId }))) return
    void action.run(() => admin.blockDevice(device.id))
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
      {(devices.error ?? action.error) && <p role="alert" className="mb-3 text-sm text-red-700">{action.error ?? devices.error}</p>}
      {devices.data?.length === 0 && <p className="text-slate-600">{t('web.devices.none')}</p>}
      {devices.data && devices.data.length > 0 && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">{t('web.devices.user')}</th>
              <th>{t('web.devices.device')}</th>
              <th>{t('web.devices.app')}</th>
              <th>{t('web.devices.registered')}</th>
              <th>{t('web.devices.status')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {devices.data.map((device) => (
              <tr key={device.id} className="border-b">
                <td className="py-2">{device.userEmail ?? device.userId}</td>
                <td>
                  {device.label} ({device.platform})
                </td>
                <td>{device.appVersion}</td>
                <td>{new Date(device.registeredAt).toLocaleString('en-GB', { timeZone: 'Asia/Baghdad' })}</td>
                <td>{t(statusText[device.status])}</td>
                <td className="space-x-2 py-2 text-end">
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
