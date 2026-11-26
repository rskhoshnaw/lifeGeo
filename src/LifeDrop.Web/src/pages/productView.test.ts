import { describe, expect, it } from 'vitest'
import { expiryStatus, filterProducts, unitLabel } from './productView.ts'

describe('expiryStatus', () => {
  const today = new Date(2026, 10, 23)
  it('is none without a day, expired before today, soon within 90 days, ok after', () => {
    expect(expiryStatus(null, today)).toBe('none')
    expect(expiryStatus('2026-11-22', today)).toBe('expired')
    expect(expiryStatus('2026-11-23', today)).toBe('soon')
    expect(expiryStatus('2027-02-21', today)).toBe('soon')
    expect(expiryStatus('2027-02-22', today)).toBe('ok')
  })
})

describe('filterProducts', () => {
  const items = [
    { code: 'A1', name: 'Paracetamol', scientificName: 'Acetaminophen', specialty: 'pediatrics', manufacturer: 'Pioneer', inStock: true },
    { code: 'B1', name: 'Amoxil', scientificName: null, specialty: 'ent', manufacturer: 'samara', inStock: false },
  ]
  const none = { text: '', specialty: '', manufacturer: '', stock: '' as const }
  it('filters by text (also the scientific name), specialty, manufacturer and stock together', () => {
    expect(filterProducts(items, none)).toHaveLength(2)
    expect(filterProducts(items, { ...none, text: 'acetamin' }).map((p) => p.code)).toEqual(['A1'])
    expect(filterProducts(items, { ...none, specialty: 'ent' }).map((p) => p.code)).toEqual(['B1'])
    expect(filterProducts(items, { ...none, manufacturer: 'PIONEER' }).map((p) => p.code)).toEqual(['A1'])
    expect(filterProducts(items, { ...none, stock: 'out' }).map((p) => p.code)).toEqual(['B1'])
    expect(filterProducts(items, { ...none, specialty: 'ent', stock: 'in' })).toHaveLength(0)
  })
})

describe('unitLabel', () => {
  it('shows a listed unit in the panel language and any other unit as it is', () => {
    expect(unitLabel('box')).toBe('Box')
    expect(unitLabel('ampoule')).toBe('Ampoule')
    expect(unitLabel('tablet-strip')).toBe('tablet-strip')   // an older product's own word
    expect(unitLabel(null)).toBe('')
  })
})
