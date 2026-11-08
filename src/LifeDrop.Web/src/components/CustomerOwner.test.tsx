import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { customerFilterQuery, type AdminApi, type UserSummary } from '../api/admin.ts'
import { CustomerFilterBar } from './CustomerFilterBar.tsx'
import { CustomerOwnerBar } from './CustomerOwnerBar.tsx'

const user = (id: string, email: string) => ({ id, email, roles: [], teamId: null, teamName: null, regionId: null, activeSessions: 0, isActive: true }) as UserSummary
const users = [user('u1', 'ann@x.com'), user('u2', 'bob@x.com')]

describe('customer filters and hand-over (plan step 5.28)', () => {
  it('sends only the filters that are set, and yes/no as true/false', () => {
    expect(customerFilterQuery()).toBe('')
    expect(customerFilterQuery({ ownerId: 'u1', createdFrom: '2026-11-01', hasLocation: 'no', dealer: 'yes', specialty: '' })).toBe('&ownerId=u1&createdFrom=2026-11-01&hasLocation=false&dealer=true')
  })

  it('the filter bar reports each change with the others kept, and clears them all', async () => {
    const onChange = vi.fn()
    const admin = { places: vi.fn().mockResolvedValue([]) } as unknown as AdminApi
    render(<CustomerFilterBar admin={admin} users={users} filters={{ type: 'Doctor' }} onChange={onChange} />)
    await userEvent.selectOptions(screen.getByLabelText('All owners'), 'u2')
    expect(onChange).toHaveBeenLastCalledWith({ type: 'Doctor', ownerId: 'u2' })
    await userEvent.selectOptions(screen.getByLabelText('Location: any'), 'no')
    expect(onChange).toHaveBeenLastCalledWith({ type: 'Doctor', hasLocation: 'no' })
    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(onChange).toHaveBeenLastCalledWith({})
  })

  it('gives the ticked customers to the chosen user and reports how many moved', async () => {
    const setCustomerOwner = vi.fn().mockResolvedValue({ updated: 2, skipped: 1 })
    const onDone = vi.fn()
    render(<CustomerOwnerBar admin={{ setCustomerOwner } as unknown as AdminApi} users={users} ticked={['c1', 'c2', 'c3']} onDone={onDone} />)
    const give = screen.getByRole('button', { name: 'Give the 3 ticked' })
    expect(give).toBeDisabled()                                                      // no new owner yet
    await userEvent.selectOptions(screen.getByLabelText('New owner'), 'u2')
    await userEvent.click(give)
    expect(setCustomerOwner).toHaveBeenCalledWith({ ownerUserId: 'u2', customerIds: ['c1', 'c2', 'c3'] })
    expect(await screen.findByRole('status')).toHaveTextContent('2 customers moved to bob@x.com. 1 not changed.')
    expect(onDone).toHaveBeenCalled()
  })

  it('gives everything one user owns only after the manager confirms', async () => {
    const setCustomerOwner = vi.fn().mockResolvedValue({ updated: 5, skipped: 0 })
    render(<CustomerOwnerBar admin={{ setCustomerOwner } as unknown as AdminApi} users={users} ticked={[]} onDone={() => {}} />)
    await userEvent.selectOptions(screen.getByLabelText('New owner'), 'u2')
    await userEvent.selectOptions(screen.getByLabelText('Everything owned by…'), 'u1')
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    await userEvent.click(screen.getByRole('button', { name: "Give all of that user's customers" }))
    expect(setCustomerOwner).not.toHaveBeenCalled()
    expect(confirm).toHaveBeenCalledWith('Give every customer of ann@x.com to bob@x.com?')
    await userEvent.click(screen.getByRole('button', { name: "Give all of that user's customers" }))
    expect(setCustomerOwner).toHaveBeenCalledWith({ ownerUserId: 'u2', fromUserId: 'u1' })
  })
})
