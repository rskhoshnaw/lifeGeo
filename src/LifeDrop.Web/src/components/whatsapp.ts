// The wa.me link that opens a WhatsApp chat with a phone number. Iraqi numbers are typed as 07xx xxx xxxx; WhatsApp wants the
// country code and digits only. The same rule is in the app (WhatsAppLink.cs).
const countryCode = '964'

export function whatsappDigits(phone: string | null | undefined): string | null {
  if (!phone?.trim()) return null
  let digits = phone.replace(/\D/g, '')
  const international = phone.trimStart().startsWith('+') || digits.startsWith('00')
  if (digits.startsWith('00')) digits = digits.slice(2)
  else if (!international && digits.startsWith('0')) digits = countryCode + digits.slice(1)
  else if (!international && digits.length === 10 && digits.startsWith('7')) digits = countryCode + digits
  return digits.length >= 8 && digits.length <= 15 ? digits : null
}

export function whatsappUrl(phone: string | null | undefined, text?: string): string | null {
  const digits = whatsappDigits(phone)
  if (!digits) return null
  return text?.trim() ? `https://wa.me/${digits}?text=${encodeURIComponent(text)}` : `https://wa.me/${digits}`
}
