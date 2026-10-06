import { csvCell, kpiCsv } from './reportCsv.ts'
import type { KpiRow } from '../api/admin.ts'

const counts = { visitsPlanned: 6, visitsCompleted: 3, visitsFailed: 1, delayBase: 3, delayedVisits: 2, newCustomers: 2, approvedNewCustomers: 1, orders: 3, ordersDelivered: 1, ordersDeliveryFailed: 1,
  orderTotals: [{ currency: 'IQD', total: 3000 }], followUpBase: 3, followUpsOverdue: 1 }
const rates = { visitCompletion: 50, visitFailure: 16.7, delay: 66.7, orderDelivery: 50, followUpOverdue: null }
const row = (email: string): KpiRow => ({ userId: 'u1', email, teamName: 'Team, A', counts, rates })

describe('KPI CSV', () => {
  it('quotes commas and quotes, and keeps a formula from running', () => {
    expect(csvCell('a,b')).toBe('"a,b"')
    expect(csvCell('say "hi"')).toBe('"say ""hi"""')
    expect(csvCell('=HYPERLINK("x")')).toBe('"\'=HYPERLINK(""x"")"')
    expect(csvCell('-5 off')).toBe("'-5 off")
    expect(csvCell(-5)).toBe('-5')   // a number is a number
    expect(csvCell(null)).toBe('')
  })

  it('writes one row per user and a total with the numerators next to the rates; an empty rate is an empty cell', () => {
    const lines = kpiCsv([row('rep@t1'), row('@evil')], row('x')).trimEnd().split('\r\n')
    expect(lines[0]!.startsWith('\ufeffuser,team,visits planned')).toBe(true)
    expect(lines[1]).toBe('rep@t1,"Team, A",6,3,1,50,16.7,3,2,66.7,2,1,3,1,1,50,3000,0,3,1,')
    expect(lines[2]!.startsWith("'@evil,")).toBe(true)
    expect(lines).toHaveLength(4)
    expect(lines[3]!.startsWith('TOTAL,,6,3,1')).toBe(true)
  })
})
