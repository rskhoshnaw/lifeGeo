import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { CustomerDetailsEditor } from './CustomerDetailsEditor.tsx'
import type { AdminApi, Customer } from '../api/admin.ts'

const customer = { id: 'c1', type: 'Doctor', code: 'D-1', name: 'Dr. Ali', clinicName: 'Azadi Complex', specialty: 'pediatrics', cooperates: true, dealer: null, customerClass: 'B' } as Customer

describe('customer details editor (plan step 4.27)', () => {
  it('shows the doctor fields only for a doctor and saves them with the type, code and name', async () => {
    const editCustomer = vi.fn().mockResolvedValue(undefined)
    const setCustomerDoctor = vi.fn().mockResolvedValue(undefined)
    const onSaved = vi.fn()
    render(<CustomerDetailsEditor admin={{ editCustomer, setCustomerDoctor } as unknown as AdminApi} customer={customer} onClose={() => {}} onSaved={onSaved} />)

    expect(screen.getByLabelText('Clinic / medical complex')).toHaveValue('Azadi Complex')
    await userEvent.selectOptions(screen.getByLabelText('Specialty'), 'orthopedics')
    await userEvent.selectOptions(screen.getByLabelText('Dealer'), 'yes')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(editCustomer).toHaveBeenCalledWith('c1', { type: 'Doctor', code: 'D-1', name: 'Dr. Ali' })
    expect(setCustomerDoctor).toHaveBeenCalledWith('c1', { clinicName: 'Azadi Complex', specialty: 'orthopedics', cooperates: true, dealer: true, customerClass: 'B' })
    expect(onSaved).toHaveBeenCalled()
  })

  it('hides the doctor fields and sends no doctor details for a medical assistant', async () => {
    const editCustomer = vi.fn().mockResolvedValue(undefined)
    const setCustomerDoctor = vi.fn()
    render(<CustomerDetailsEditor admin={{ editCustomer, setCustomerDoctor } as unknown as AdminApi} customer={{ ...customer, type: 'MedicalAssistant' }} onClose={() => {}} onSaved={() => {}} />)
    expect(screen.queryByLabelText('Specialty')).toBeNull()
    expect(screen.getByRole('option', { name: 'Medical assistant' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(editCustomer).toHaveBeenCalledWith('c1', { type: 'MedicalAssistant', code: 'D-1', name: 'Dr. Ali' })
    expect(setCustomerDoctor).not.toHaveBeenCalled()
  })
})
