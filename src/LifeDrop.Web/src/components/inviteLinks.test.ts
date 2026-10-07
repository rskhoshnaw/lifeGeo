import { appLink, companyFromSearch, panelLink, qrSvg } from './inviteLinks.ts'

describe('company invitation links (step 4.7)', () => {
  it('builds the panel and app links from the company code only', () => {
    expect(panelLink('https://panel.example.com/', 'acme')).toBe('https://panel.example.com/?company=acme')
    expect(appLink('acme')).toBe('lifedrop://join/acme')
    expect(appLink('a b')).toBe('lifedrop://join/a%20b')   // never breaks the link
  })

  it('reads the company from a panel link and refuses what cannot be a code', () => {
    expect(companyFromSearch('?company=acme')).toBe('acme')
    expect(companyFromSearch('?x=1&company=ACME-2')).toBe('acme-2')   // codes are lower case
    expect(companyFromSearch('')).toBe('')
    expect(companyFromSearch('?company=')).toBe('')
    expect(companyFromSearch('?company=a')).toBe('')   // too short
    expect(companyFromSearch('?company=<script>')).toBe('')
    expect(companyFromSearch('?company=../admin')).toBe('')
  })

  it('draws a QR code as an SVG', () => {
    const svg = qrSvg('lifedrop://join/acme')
    expect(svg.startsWith('<svg')).toBe(true)
    expect(svg).not.toContain('<script')
    expect(qrSvg('lifedrop://join/acme')).toBe(svg)   // the same text always gives the same picture
    expect(qrSvg('lifedrop://join/other-company')).not.toBe(svg)
  })
})
