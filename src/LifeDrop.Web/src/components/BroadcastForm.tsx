import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, type Role, type Region, type Team, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from './useLoad.ts'

type Who = 'everyone' | 'filters' | 'people'

// A manager's message to staff (plan step 4.16): to everybody, to the people who match a role, a region and/or a team, or to chosen people. The server reaches only
// active people inside the sender's area and delivers the message through the app push and Telegram; it answers with how many people it reached.
export function BroadcastForm({ admin }: { admin: AdminApi }) {
  const roles = useLoad(useCallback(() => admin.roles().catch(() => [] as Role[]), [admin]))
  const regions = useLoad(useCallback(() => admin.regions().catch(() => [] as Region[]), [admin]))
  const teams = useLoad(useCallback(() => admin.teams().catch(() => [] as Team[]), [admin]))
  const users = useLoad(useCallback(() => admin.users().catch(() => [] as UserSummary[]), [admin]))
  const [who, setWho] = useState<Who>('everyone')
  const [role, setRole] = useState('')
  const [region, setRegion] = useState('')
  const [team, setTeam] = useState('')
  const [people, setPeople] = useState<string[]>([])
  const [text, setText] = useState('')
  const [sent, setSent] = useState<number | null>(null)
  const action = useAction()
  const ready = text.trim() !== '' && (who === 'everyone' || (who === 'filters' && (role !== '' || region !== '' || team !== '')) || (who === 'people' && people.length > 0))

  async function send(e: FormEvent) {
    e.preventDefault()
    setSent(null)
    const message = text.trim()
    const result = await action.run(() =>
      admin.sendMessage(
        who === 'everyone'
          ? { message, everyone: true }
          : who === 'people'
            ? { message, userIds: people }
            : { message, ...(role ? { role } : {}), ...(region ? { regionId: region } : {}), ...(team ? { teamId: team } : {}) },
      ),
    )
    if (result) {
      setSent(result.recipients)
      if (result.recipients > 0) setText('')
    }
  }

  return (
    <form onSubmit={(e) => void send(e)} className="space-y-2 rounded border p-3">
      <h3 className="font-semibold">{t('web.broadcast.title')}</h3>
      {action.error && <p role="alert" className="text-red-700">{action.error}</p>}
      {sent !== null && <p role="status" className={sent > 0 ? 'text-green-700' : 'text-amber-700'}>{sent > 0 ? t('web.broadcast.sent', { count: sent }) : t('web.broadcast.nobody')}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <select aria-label={t('web.broadcast.to')} className="rounded border px-2 py-1" value={who} onChange={(e) => setWho(e.target.value as Who)}>
          <option value="everyone">{t('web.broadcast.everyone')}</option>
          <option value="filters">{t('web.broadcast.byGroup')}</option>
          <option value="people">{t('web.broadcast.people')}</option>
        </select>
        {who === 'filters' && (
          <>
            <select aria-label={t('web.broadcast.role')} className="rounded border px-2 py-1" value={role} onChange={(e) => setRole(e.target.value)}>
              <option value="">{t('web.broadcast.anyRole')}</option>
              {(roles.data ?? []).map((r) => (
                <option key={r.id} value={r.name}>
                  {r.name}
                </option>
              ))}
            </select>
            <select aria-label={t('web.broadcast.region')} className="rounded border px-2 py-1" value={region} onChange={(e) => setRegion(e.target.value)}>
              <option value="">{t('web.broadcast.anyRegion')}</option>
              {(regions.data ?? []).map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </select>
            <select aria-label={t('web.broadcast.team')} className="rounded border px-2 py-1" value={team} onChange={(e) => setTeam(e.target.value)}>
              <option value="">{t('web.broadcast.anyTeam')}</option>
              {(teams.data ?? []).map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </select>
          </>
        )}
      </div>
      {who === 'people' && (
        <div className="flex max-h-40 flex-wrap gap-x-4 gap-y-1 overflow-y-auto rounded border p-2">
          {(users.data ?? []).map((u) => (
            <label key={u.id} className="flex items-center gap-1">
              <input type="checkbox" checked={people.includes(u.id)} onChange={(e) => setPeople(e.target.checked ? [...people, u.id] : people.filter((id) => id !== u.id))} />
              {u.email ?? u.id}
            </label>
          ))}
        </div>
      )}
      <textarea aria-label={t('web.broadcast.message')} placeholder={t('web.broadcast.message')} className="block w-full rounded border px-2 py-1" rows={3} maxLength={500} value={text} onChange={(e) => setText(e.target.value)} />
      <button type="submit" className="rounded border px-3 py-1" disabled={!ready || action.busy}>
        {t('web.broadcast.send')}
      </button>
    </form>
  )
}
