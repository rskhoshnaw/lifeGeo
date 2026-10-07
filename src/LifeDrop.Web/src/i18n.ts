import { createContext, useContext } from 'react'
import strings from '../../../i18n/strings.json'

// Languages of the panel and the app (plan step 1.21, PRD §13). Sorani Kurdish is the default; Kurdish, Arabic
// and Persian are right-to-left. Texts live in i18n/strings.json at the repository root, shared with the mobile app.
export type Lang = 'ckb' | 'ar' | 'fa' | 'en'
export type TextKey = keyof typeof strings

export const languages: { code: Lang; name: string }[] = [
  { code: 'ckb', name: 'کوردی' },
  { code: 'ar', name: 'العربية' },
  { code: 'fa', name: 'فارسی' },
  { code: 'en', name: 'English' },
]
export const defaultLang: Lang = 'ckb'
export const isRtl = (lang: Lang) => lang !== 'en'

// The active language for t(); set by I18nProvider before its children render.
let current: Lang = defaultLang

// Industry profile of the company (plan step 4.3): a text "key@food" replaces "key" for a food distributor.
let profile = ''

export function setCurrentProfile(next: string) {
  profile = next
}

export function t(key: TextKey, vars: Record<string, string | number> = {}, lang: Lang = current): string {
  const special = profile ? (strings as Record<string, unknown>)[`${key}@${profile}`] : undefined
  const entry = (special ?? strings[key]) as Record<Lang, string>
  return (entry[lang] || entry.en).replace(/\{(\w+)\}/g, (match, name: string) => (name in vars ? String(vars[name]) : match))
}

export function setCurrentLang(lang: Lang) {
  current = lang
}

export const storageKey = 'lifedrop.lang'

export function storedLang(): Lang {
  try {
    const value = localStorage.getItem(storageKey)
    return languages.some((l) => l.code === value) ? (value as Lang) : defaultLang
  } catch {
    return defaultLang
  }
}

export const LangContext = createContext<{ lang: Lang; setLang: (lang: Lang) => void }>({ lang: defaultLang, setLang: () => {} })

// Components that call this re-render, with everything they render, when the language changes.
export const useLang = () => useContext(LangContext)
