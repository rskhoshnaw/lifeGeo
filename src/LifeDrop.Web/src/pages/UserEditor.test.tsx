import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { UserEditor } from './UsersPage.tsx'
import type { AdminApi, Role, UserSummary } from '../api/admin.ts'

const roles: Role[] = [{ id: 'r1', name: 'SalesRep', dataScope: 'region' }, { id: 'r2', name: 'Driver', dataScope: 'own' }]
const user = { id: 'u1', email: 'rep@t1', roles: ['SalesRep', 'Driver'], teamId: null, teamName: null, regionId: null, activeSessions: 0, isActive: true, city: 'Erbil' } as UserSummary

describe('user editor (plan step 4.37)', () => {
  it('saves the city and takes one role away', async () => {
    const setUserCity = vi.fn().mockResolvedValue(undefined)
    const removeUserRole = vi.fn().mockResolvedValue(undefined)
    const onChanged = vi.fn()
    render(<UserEditor user={user} roles={roles} self={false} admin={{ setUserCity, removeUserRole } as unknown as AdminApi} onClose={() => {}} onChanged={onChanged} />)

    const city = screen.getByLabelText('City')
    expect(city).toHaveValue('Erbil')
    await userEvent.clear(city)
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(setUserCity).toHaveBeenCalledWith('u1', null)

    await userEvent.click(screen.getByRole('button', { name: 'Remove role Driver' }))
    expect(removeUserRole).toHaveBeenCalledWith('u1', 'r2')
    expect(onChanged).toHaveBeenCalled()
  })

  it('does not offer to remove a role from yourself', () => {
    render(<UserEditor user={user} roles={roles} self admin={{} as AdminApi} onClose={() => {}} onChanged={() => {}} />)
    expect(screen.queryByRole('button', { name: /Remove role/ })).toBeNull()
  })
})
