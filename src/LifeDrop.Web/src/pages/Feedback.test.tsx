import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { AdminApi, Feedback } from '../api/admin.ts'
import { FeedbackPage } from './FeedbackPage.tsx'

const item = (patch: Partial<Feedback> = {}): Feedback => ({
  id: 'f1', kind: 'complaint', body: 'The drivers are late', status: 'new', createdAt: '2026-11-10T08:00:00Z', updatedAt: '2026-11-10T08:00:00Z', userId: 'u1', userEmail: 'ann@x.com',
  customerId: 'c1', customerCode: 'D-1', customerName: 'Dr. Ali', photoIds: ['p1'], replies: [], ...patch,
})

function setup(list: Feedback[]) {
  const admin = {
    users: vi.fn().mockResolvedValue([{ id: 'u1', email: 'ann@x.com' }]),
    feedbackList: vi.fn().mockResolvedValue(list),
    feedbackPhotoUrl: vi.fn().mockResolvedValue('blob:photo'),
    replyFeedback: vi.fn().mockResolvedValue(undefined),
    setFeedbackStatus: vi.fn().mockResolvedValue(undefined),
  }
  render(<FeedbackPage admin={admin as unknown as AdminApi} />)
  return admin
}

describe('feedback page (plan step 5.30)', () => {
  it('lists the feedback with the customer and filters it by status, kind and user', async () => {
    const admin = setup([item()])
    expect(await screen.findByText('The drivers are late')).toBeInTheDocument()
    expect(screen.getByText('D-1 · Dr. Ali')).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('All statuses'), 'done')
    await userEvent.selectOptions(screen.getByLabelText('All kinds'), 'note')
    await userEvent.selectOptions(await screen.findByLabelText('All users'), 'u1')
    await waitFor(() => expect(admin.feedbackList).toHaveBeenLastCalledWith({ status: 'done', kind: 'note', userId: 'u1' }))
  })

  it('opens one feedback with its photo and answers it, then sets the status', async () => {
    const admin = setup([item({ replies: [{ id: 'r1', userId: 'm', userEmail: 'boss@x.com', body: 'We are on it', createdAt: '2026-11-10T09:00:00Z' }] })])
    await userEvent.click(await screen.findByRole('button', { name: /^Open/ }))
    expect(await screen.findByText('We are on it')).toBeInTheDocument()
    expect(await screen.findByAltText('Photo 1')).toBeInTheDocument()
    expect(admin.feedbackPhotoUrl).toHaveBeenCalledWith('f1', 'p1')

    const send = screen.getByRole('button', { name: 'Send answer' })
    expect(send).toBeDisabled()                                                      // nothing written yet
    await userEvent.type(screen.getByLabelText('Write an answer…'), '  Thanks  ')
    await userEvent.click(send)
    expect(admin.replyFeedback).toHaveBeenCalledWith('f1', 'Thanks')
    await userEvent.click(screen.getByRole('button', { name: 'Done' }))
    expect(admin.setFeedbackStatus).toHaveBeenCalledWith('f1', 'done')
  })
})
