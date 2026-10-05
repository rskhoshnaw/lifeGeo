import '@testing-library/jest-dom/vitest'
import { setCurrentLang } from './i18n.ts'

// Tests read the English texts; the default language (Sorani Kurdish) is covered in i18n.test.tsx.
beforeEach(() => {
  localStorage.setItem('lifedrop.lang', 'en')
  setCurrentLang('en')
})
