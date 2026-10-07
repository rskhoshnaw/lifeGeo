import { afterEach, expect, it } from 'vitest'
import { setCurrentProfile, t } from './i18n.ts'

afterEach(() => setCurrentProfile(''))

it('a food distributor sees shops and restaurants instead of pharmacies and doctors', () => {
  expect(t('app.type.pharmacy', {}, 'en')).toBe('Pharmacy')
  setCurrentProfile('food')
  expect(t('app.type.pharmacy', {}, 'en')).toBe('Shop / supermarket')
  expect(t('web.visitType.doctor', {}, 'en')).toBe('Restaurant / hotel')
  expect(t('app.type.wholesaler', {}, 'en')).toBe('Wholesaler')
})
