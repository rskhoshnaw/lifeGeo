import { formatWording, parseWording } from './companyProfile.ts'

describe('the wording of a company as lines (plan step 4.48)', () => {
  it('reads key | language | text lines, keeping a | inside the text', () => {
    expect(parseWording('app.home.customers | ckb | دوکانەکانم\n# a note\n\napp.home.customers | en | My shops | and more\nweb.tab.orders|fa|سفارش‌ها')).toEqual({
      wording: {
        'app.home.customers': { ckb: 'دوکانەکانم', en: 'My shops | and more' },
        'web.tab.orders': { fa: 'سفارش‌ها' },
      },
    })
  })

  it('names the first bad line', () => {
    expect(parseWording('app.x | en | fine\nnonsense')).toEqual({ badLine: 2 })
    expect(parseWording('customers | en | key must start with app. or web.')).toEqual({ badLine: 1 })
    expect(parseWording('app.x | de | language not offered')).toEqual({ badLine: 1 })
    expect(parseWording('app.x | en |   ')).toEqual({ badLine: 1 })
  })

  it('writes back what it reads', () => {
    const wording = { 'app.a': { ckb: 'ا', en: 'A' } }
    expect(parseWording(formatWording(wording))).toEqual({ wording })
    expect(formatWording({})).toBe('')
  })
})
