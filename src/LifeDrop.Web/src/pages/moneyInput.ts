// Typing an amount of money (owner request 2026-11-27). In Iraq the dot and the comma also separate thousands, so "10.440" and "10,440" are
// ten thousand four hundred and forty dinars, not 10.44. IQD has no decimals: only whole numbers, and a separator followed by exactly three
// digits is a thousands mark. USD has two decimals. Digits of any script (Persian, Arabic-Indic) and spaces are accepted.
// The same rules are in the app (MoneyInput.cs, same test cases) and the server refuses what they refuse (MoneyRules.cs).
export type MoneyProblem = 'empty' | 'invalid' | 'decimals'
export type MoneyParse = { value: number; problem?: undefined } | { value?: undefined; problem: MoneyProblem }

function normalise(text: string): string {
  return [...text.trim()].map((c) => {
    if (c >= '٠' && c <= '٩') return String.fromCharCode(48 + c.charCodeAt(0) - 0x0660)
    if (c >= '۰' && c <= '۹') return String.fromCharCode(48 + c.charCodeAt(0) - 0x06f0)
    if (c === '٫') return '.'
    if (c === '٬' || c === '،') return ','
    if (/[\s  '’]/.test(c)) return ''
    return c
  }).join('')
}

const groups = /^\d{1,3}([.,]\d{3})+$/

export function parseMoney(text: string, currency: string): MoneyParse {
  const s = normalise(text)
  if (s === '') return { problem: 'empty' }
  if (/^\d+$/.test(s)) return { value: Number(s) }
  if (!/^\d[\d.,]*\d$/.test(s) || /[.,]{2}/.test(s)) return { problem: 'invalid' }
  if (currency !== 'USD') {
    // IQD: thousands marks only; anything else with a separator is a fraction, which dinars do not have.
    if (groups.test(s)) return { value: Number(s.replace(/[.,]/g, '')) }
    return /^\d+[.,]\d+$/.test(s) ? { problem: 'decimals' } : { problem: 'invalid' }
  }
  // USD: the last separator is the decimal mark when one or two digits follow it (or when both kinds are used); the others are thousands marks.
  const last = Math.max(s.lastIndexOf('.'), s.lastIndexOf(','))
  const whole = s.slice(0, last)
  const fraction = s.slice(last + 1)
  const both = s.includes('.') && s.includes(',')
  const thousands = fraction.length === 3 && !both && groups.test(s)
  if (thousands) return { value: Number(s.replace(/[.,]/g, '')) }
  if (fraction.length > 2) return { problem: 'decimals' }
  const wholeDigits = whole.replace(/[.,]/g, '')
  if (!(whole === wholeDigits || groups.test(whole))) return { problem: 'invalid' }
  return { value: Number(`${wholeDigits}.${fraction}`) }
}

// The text key of the message for a problem, shown next to the field.
export const moneyProblemKey = (problem: MoneyProblem) => (problem === 'decimals' ? 'web.money.decimals' : 'web.money.invalid') as 'web.money.decimals' | 'web.money.invalid'
