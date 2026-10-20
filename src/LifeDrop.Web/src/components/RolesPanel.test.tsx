import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RoleForm } from './RolesPanel.tsx'
import type { PermissionInfo, Role } from '../api/admin.ts'

const permissions: PermissionInfo[] = [
  { key: 'orders.read', group: 'orders' },
  { key: 'payments.read', group: 'payments' },
]

describe('role form (plan step 4.38)', () => {
  it('sends the name, reach and the ticked permissions', async () => {
    const onSave = vi.fn()
    render(<RoleForm role={null} permissions={permissions} busy={false} onSave={onSave} onClose={() => {}} />)
    await userEvent.type(screen.getByLabelText('Role name'), ' Finance Assistant ')
    await userEvent.selectOptions(screen.getByLabelText('Data reach'), 'tenant')
    await userEvent.click(screen.getByLabelText('payments.read'))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(onSave).toHaveBeenCalledWith({ name: 'Finance Assistant', dataScope: 'tenant', requiresDeviceBinding: false, permissions: ['payments.read'] })
  })

  it('keeps the name of a default role fixed and starts from its permissions', async () => {
    const onSave = vi.fn()
    const role: Role = { id: 'r1', name: 'SalesRep', dataScope: 'region', permissions: ['orders.read'], userCount: 2, isDefault: true, requiresDeviceBinding: true }
    render(<RoleForm role={role} permissions={permissions} busy={false} onSave={onSave} onClose={() => {}} />)
    expect(screen.getByLabelText('Role name')).toBeDisabled()
    expect(screen.getByLabelText('orders.read')).toBeChecked()
    await userEvent.click(screen.getByLabelText('orders.read'))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(onSave).toHaveBeenCalledWith({ name: 'SalesRep', dataScope: 'region', requiresDeviceBinding: true, permissions: [] })
  })
})
