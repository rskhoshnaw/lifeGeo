import { useState, type FormEvent } from 'react'
import { AdminApi, type Me } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction } from '../components/useLoad.ts'

const MinLength = 12

// "My account" (plan step 4.46): who is signed in and changing one's own password. The current password is needed; the user's other sessions end.
export function AccountPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const [done, setDone] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const action = useAction(() => undefined)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setDone(false)
    setProblem(null)
    if (next !== repeat) return setProblem(t('web.account.mismatch'))
    if (next.length < MinLength || next === current) return setProblem(t('web.account.tooShort', { min: MinLength }))
    const ok = await action.run(async () => (await admin.changeOwnPassword(current, next), true))
    if (ok) {
      setCurrent('')
      setNext('')
      setRepeat('')
      setDone(true)
    }
  }

  return (
    <section className="space-y-6 p-6">
      <h2 className="text-xl font-semibold">{t('web.account.title')}</h2>
      <p className="text-sm">
        <strong>{me.email}</strong> · {me.roles.join(', ') || '—'}
      </p>
      <form onSubmit={(e) => void submit(e)} className="max-w-md space-y-3 rounded border p-4 text-sm">
        <h3 className="font-medium">{t('web.account.passwordTitle')}</h3>
        {(problem ?? action.error) && <p role="alert" className="text-red-700">{problem ?? action.error}</p>}
        <label className="block">
          {t('web.account.current')}
          <input type="password" autoComplete="current-password" className="mt-1 block w-full rounded border px-2 py-1" value={current} onChange={(e) => setCurrent(e.target.value)} />
        </label>
        <label className="block">
          {t('web.account.new', { min: MinLength })}
          <input type="password" autoComplete="new-password" className="mt-1 block w-full rounded border px-2 py-1" value={next} maxLength={200} onChange={(e) => setNext(e.target.value)} />
        </label>
        <label className="block">
          {t('web.account.repeat')}
          <input type="password" autoComplete="new-password" className="mt-1 block w-full rounded border px-2 py-1" value={repeat} maxLength={200} onChange={(e) => setRepeat(e.target.value)} />
        </label>
        <button type="submit" className="rounded bg-brand-700 px-3 py-1 text-white disabled:opacity-50" disabled={action.busy || !current || !next}>
          {t('web.account.change')}
        </button>
        {done && <p role="status" className="text-green-700">{t('web.account.changed')}</p>}
      </form>
    </section>
  )
}

