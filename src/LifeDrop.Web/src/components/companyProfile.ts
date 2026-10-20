// The company's own wording as one line per text: "<text key> | <language> | <text>" (plan step 4.48). Lines that are empty or start with # are ignored.
export type Wording = Record<string, Partial<Record<'ckb' | 'ar' | 'fa' | 'en', string>>>
const languages = ['ckb', 'ar', 'fa', 'en'] as const

export function formatWording(wording: Wording): string {
  return Object.entries(wording)
    .flatMap(([key, byLanguage]) => languages.filter((l) => byLanguage[l]).map((l) => `${key} | ${l} | ${byLanguage[l]}`))
    .join('\n')
}

// Returns the wording, or the 1-based number of the first line that is not "key | language | text".
export function parseWording(text: string): { wording: Wording } | { badLine: number } {
  const wording: Wording = {}
  const lines = text.split('\n')
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!.trim()
    if (!line || line.startsWith('#')) continue
    const first = line.indexOf('|')
    const second = first < 0 ? -1 : line.indexOf('|', first + 1)
    if (second < 0) return { badLine: i + 1 }
    const key = line.slice(0, first).trim()
    const language = line.slice(first + 1, second).trim() as (typeof languages)[number]
    const value = line.slice(second + 1).trim()
    if (!/^(app|web)\.[A-Za-z0-9_.@-]{1,100}$/.test(key) || !languages.includes(language) || !value) return { badLine: i + 1 }
    wording[key] = { ...wording[key], [language]: value }
  }
  return { wording }
}
