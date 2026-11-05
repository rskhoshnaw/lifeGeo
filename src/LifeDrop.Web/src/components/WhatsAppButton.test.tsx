import { render, screen } from '@testing-library/react'
import { WhatsAppButton } from './WhatsAppButton.tsx'

describe('WhatsApp button (plan step 5.22)', () => {
  it('opens a wa.me chat with the Iraqi number written in international form', () => {
    render(<WhatsAppButton phone="0770 123 4567" who="rep@t1" />)
    const link = screen.getByRole('link', { name: 'Open a WhatsApp chat with rep@t1' })
    expect(link).toHaveAttribute('href', 'https://wa.me/9647701234567')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
  })

  it('shows nothing without a usable number', () => {
    const { container } = render(<WhatsAppButton phone="abc" who="rep@t1" />)
    expect(container).toBeEmptyDOMElement()
    const none = render(<WhatsAppButton phone={null} who="x" />)
    expect(none.container).toBeEmptyDOMElement()
  })
})
