import qrcode from 'qrcode-generator'

// Company invitation links (plan step 4.7). One company code gives a link for the panel and a link for the phone app; both carry only the public company code,
// never a password or a token. The app link is the one in the QR code.
export const appScheme = 'lifedrop://join/'

export const panelLink = (origin: string, code: string) => `${origin.replace(/\/+$/, '')}/?company=${encodeURIComponent(code)}`
export const appLink = (code: string) => `${appScheme}${encodeURIComponent(code)}`

// The company named by a panel link (?company=acme), or '' when the link names none or something that cannot be a company code.
export function companyFromSearch(search: string): string {
  const value = new URLSearchParams(search).get('company')?.trim().toLowerCase() ?? ''
  return /^[a-z0-9][a-z0-9_-]{1,39}$/.test(value) ? value : ''
}

// A QR code as an SVG string (error correction M, size chosen from the text).
export function qrSvg(text: string): string {
  const qr = qrcode(0, 'M')
  qr.addData(text)
  qr.make()
  return qr.createSvgTag({ scalable: true, margin: 2 })
}
