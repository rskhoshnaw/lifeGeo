import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AccountPage } from './AccountPage.tsx'
import { ApiError, type AdminApi, type Me } from '../api/admin.ts'

const me: Me = { id: 'u1', email: 'rep@t1', roles: ['SalesRep'], permissions: [] }

describe('my account (plan step 4.46)', () => {
  it('checks the new password before sending and sends current and new', async () => {
    const changeOwnPassword = vi.fn().mockResolvedValue(undefined)
    render(<AccountPage admin={{ changeOwnPassword } as unknown as AdminApi} me={me} />)
    await userEvent.type(screen.getByLabelText('Current password'), 'old-password-123')
    await userEvent.type(screen.getByLabelText(/^New password \(/), 'short')
    await userEvent.type(screen.getByLabelText('New password again'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(screen.getByRole('alert')).toHaveTextContent('at least 12 characters')
    expect(changeOwnPassword).not.toHaveBeenCalled()

    await userEvent.clear(screen.getByLabelText(/^New password \(/))
    await userEvent.type(screen.getByLabelText(/^New password \(/), 'brand-new-password-2')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(screen.getByRole('alert')).toHaveTextContent('not the same')

    await userEvent.clear(screen.getByLabelText('New password again'))
    await userEvent.type(screen.getByLabelText('New password again'), 'brand-new-password-2')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(changeOwnPassword).toHaveBeenCalledWith('old-password-123', 'brand-new-password-2')
    expect(await screen.findByRole('status')).toHaveTextContent('Password changed')
  })

  it('says when the current password is wrong', async () => {
    const changeOwnPassword = vi.fn().mockRejectedValue(new ApiError(400, 'WRONG_PASSWORD'))
    render(<AccountPage admin={{ changeOwnPassword } as unknown as AdminApi} me={me} />)
    await userEvent.type(screen.getByLabelText('Current password'), 'wrong-password-1')
    await userEvent.type(screen.getByLabelText(/^New password \(/), 'brand-new-password-2')
    await userEvent.type(screen.getByLabelText('New password again'), 'brand-new-password-2')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('current password is not right')
  })
})
