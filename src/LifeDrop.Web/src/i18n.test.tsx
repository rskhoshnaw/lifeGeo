import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from './App.tsx'
import { ApiClient } from './api/client.ts'
import { languages, t } from './i18n.ts'
import strings from '../../../i18n/strings.json'

const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort()

describe('texts (plan step 1.21)', () => {
  it('every key has every language, with the same placeholders', () => {
    for (const [key, entry] of Object.entries(strings as Record<string, Record<string, string>>)) {
      for (const { code } of languages) {
        expect(entry[code]?.trim(), `${key}.${code}`).toBeTruthy()
        expect(placeholders(entry[code]!), `${key}.${code}`).toEqual(placeholders(entry.en!))
      }
    }
  })

  it('fills placeholders and keeps unknown ones', () => {
    expect(t('web.teams.removeFrom', { user: 'a@t1', team: 'A' }, 'en')).toBe('Remove a@t1 from A')
    expect(t('web.teams.removeFrom', { user: 'a@t1' }, 'ckb')).toBe('لابردنی a@t1 لە {team}')
  })

  it('opens in Sorani Kurdish, right to left, and remembers another choice', async () => {
    localStorage.removeItem('lifedrop.lang')
    sessionStorage.clear()
    render(<App api={new ApiClient('https://api.test', (async () => new Response(null, { status: 401 })) as typeof fetch, sessionStorage)} />)
    expect(await screen.findByRole('button', { name: 'چوونەژوورەوە' })).toBeInTheDocument()
    expect(document.documentElement).toHaveAttribute('dir', 'rtl')
    expect(document.documentElement).toHaveAttribute('lang', 'ckb')

    await userEvent.setup().selectOptions(screen.getByRole('combobox', { name: 'زمان' }), 'en')
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByText('Company code')).toBeInTheDocument()
    expect(document.documentElement).toHaveAttribute('dir', 'ltr')
    expect(localStorage.getItem('lifedrop.lang')).toBe('en')
  })
})
