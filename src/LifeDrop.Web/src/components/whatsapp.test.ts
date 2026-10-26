import { whatsappUrl } from './whatsapp.ts'

describe('whatsapp links', () => {
  it.each([
    ['+9647722656566', 'https://wa.me/9647722656566'],
    ['0772 265 6566', 'https://wa.me/9647722656566'],
    ['07722656566', 'https://wa.me/9647722656566'],
    ['7722656566', 'https://wa.me/9647722656566'],
    ['00964 772 265 6566', 'https://wa.me/9647722656566'],
    ['+44 7911 123456', 'https://wa.me/447911123456'],
  ])('turns %s into a wa.me link', (phone, url) => expect(whatsappUrl(phone)).toBe(url))

  it.each([null, undefined, '', '  ', 'abc', '12', '0770'])('gives no link for %s', (phone) => expect(whatsappUrl(phone)).toBeNull())

  it('escapes a message', () => expect(whatsappUrl('07722656566', 'Hello & welcome')).toBe('https://wa.me/9647722656566?text=Hello%20%26%20welcome'))
})
