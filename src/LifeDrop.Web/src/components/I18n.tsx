import { useEffect, useState, type ReactNode } from 'react'
import { isRtl, LangContext, languages, setCurrentLang, storageKey, storedLang, t, useLang, type Lang } from '../i18n.ts'

// Holds the chosen language (kept in this browser), sets <html lang dir> and t()'s language.
export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Lang>(storedLang)
  setCurrentLang(lang)

  useEffect(() => {
    document.documentElement.lang = lang
    document.documentElement.dir = isRtl(lang) ? 'rtl' : 'ltr'
  }, [lang])

  function setLang(next: Lang) {
    try {
      localStorage.setItem(storageKey, next)
    } catch {
      // The choice still applies to this visit.
    }
    setLangState(next)
  }

  return <LangContext.Provider value={{ lang, setLang }}>{children}</LangContext.Provider>
}

export function LanguagePicker() {
  const { lang, setLang } = useLang()
  return (
    <select aria-label={t('web.language')} className="rounded border px-1 py-1 text-sm" value={lang} onChange={(e) => setLang(e.target.value as Lang)}>
      {languages.map((l) => (
        <option key={l.code} value={l.code} lang={l.code}>
          {l.name}
        </option>
      ))}
    </select>
  )
}
