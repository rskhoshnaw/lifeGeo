import { useCallback, useState, type FormEvent } from 'react'
import { AdminApi, can, Permissions, type Me, type Region, type Team, type UserSummary } from '../api/admin.ts'
import { t } from '../i18n.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

// Regions and teams (plan step 1.18, decision D11): the sales manager defines regions, teams (one region each)
// and team members. Readers see the lists; changes are shown only with the manage permissions.
export function RegionsPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const regions = useLoad(useCallback(() => admin.regions(), [admin]))
  const canTeams = can(me, ...Permissions.readTeams)
  const teams = useLoad(useCallback(() => (canTeams ? admin.teams() : Promise.resolve<Team[]>([])), [admin, canTeams]))
  const canManageTeams = can(me, ...Permissions.manageTeams)
  // Members come from /users, which needs its own permission; without it the member lists stay hidden.
  const users = useLoad(useCallback(() => (canManageTeams ? admin.users().catch(() => [] as UserSummary[]) : Promise.resolve<UserSummary[]>([])), [admin, canManageTeams]))
  const [reloadRegions, reloadTeams, reloadUsers] = [regions.reload, teams.reload, users.reload]
  const reloadAll = useCallback(() => {
    reloadRegions()
    reloadTeams()
    reloadUsers()
  }, [reloadRegions, reloadTeams, reloadUsers])
  const action = useAction(reloadAll)
  const [regionName, setRegionName] = useState('')
  const [teamName, setTeamName] = useState('')
  const [teamRegion, setTeamRegion] = useState('')
  const [member, setMember] = useState<Record<string, string>>({})

  function addRegion(e: FormEvent) {
    e.preventDefault()
    const name = regionName.trim()
    if (name) void action.run(async () => (await admin.createRegion(name), setRegionName('')))
  }

  function addTeam(e: FormEvent) {
    e.preventDefault()
    const name = teamName.trim()
    if (name && teamRegion) void action.run(async () => (await admin.createTeam(name, teamRegion), setTeamName('')))
  }

  const regionLabel = (id: string) => regions.data?.find((r: Region) => r.id === id)?.name ?? id
  const error = action.error ?? regions.error ?? teams.error
  return (
    <section className="space-y-8 p-6">
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      <div>
        <h2 className="mb-4 text-xl font-semibold">{t('web.regions.title')}</h2>
        {regions.data?.length === 0 && <p className="mb-3 text-sm text-slate-600">{t('web.regions.none')}</p>}
        <ul className="mb-3 list-disc ps-6 text-sm">
          {regions.data?.map((region) => (
            <li key={region.id}>
              {region.name}
              {can(me, ...Permissions.manageRegions) && (
                <label className="ms-3 inline-flex items-center gap-1">
                  <input type="checkbox" checked={!!region.hidePrices} disabled={action.busy} onChange={(e) => void action.run(() => admin.setRegionHidePrices(region.id, e.target.checked))} />
                  {t('web.regions.hidePrices')}
                </label>
              )}
            </li>
          ))}
        </ul>
        {can(me, ...Permissions.manageRegions) && (
          <form onSubmit={addRegion} className="flex gap-2 text-sm">
            <input aria-label={t('web.regions.newName')} className="rounded border px-2 py-1" placeholder={t('web.regions.namePlaceholder')} value={regionName} onChange={(e) => setRegionName(e.target.value)} />
            <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !regionName.trim()}>
              {t('web.regions.add')}
            </button>
          </form>
        )}
      </div>
      {canTeams && (
        <div>
          <h2 className="mb-4 text-xl font-semibold">{t('web.teams.title')}</h2>
          {teams.data?.length === 0 && <p className="mb-3 text-sm text-slate-600">{t('web.teams.none')}</p>}
          {teams.data?.map((team) => {
            const members = (users.data ?? []).filter((u) => u.teamId === team.id)
            const others = (users.data ?? []).filter((u) => u.teamId !== team.id)
            return (
              <div key={team.id} className="mb-3 border-b pb-3 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="min-w-40 font-medium">{team.name}</span>
                  {canManageTeams && regions.data ? (
                    <select aria-label={t('web.teams.regionOf', { team: team.name })} className="rounded border px-1 py-1" value={team.regionId} disabled={action.busy} onChange={(e) => void action.run(() => admin.moveTeam(team.id, e.target.value))}>
                      {regions.data.map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.name}
                        </option>
                      ))}
                    </select>
                  ) : (
                    <span>{regionLabel(team.regionId)}</span>
                  )}
                </div>
                {canManageTeams && (
                  <div className="mt-2 flex flex-wrap items-center gap-2 ps-4">
                    {members.map((u) => (
                      <span key={u.id} className="rounded border px-2 py-0.5">
                        {u.email ?? u.id}{' '}
                        <button aria-label={t('web.teams.removeFrom', { user: u.email ?? u.id, team: team.name })} className="text-red-700" disabled={action.busy} onClick={() => void action.run(() => admin.removeMember(team.id, u.id))}>
                          ×
                        </button>
                      </span>
                    ))}
                    <select aria-label={t('web.teams.addMemberTo', { team: team.name })} className="rounded border px-1 py-1" value={member[team.id] ?? ''} onChange={(e) => setMember({ ...member, [team.id]: e.target.value })}>
                      <option value="">{t('web.teams.addMember')}</option>
                      {others.map((u) => (
                        <option key={u.id} value={u.id}>
                          {u.email ?? u.id}
                          {u.teamName ? t('web.teams.nowIn', { team: u.teamName }) : ''}
                        </option>
                      ))}
                    </select>
                    <button className="rounded border px-2 py-1" disabled={action.busy || !member[team.id]} onClick={() => void action.run(() => admin.addMember(team.id, member[team.id]!))}>
                      {t('web.action.add')}
                    </button>
                  </div>
                )}
              </div>
            )
          })}
          {canManageTeams && regions.data && (
            <form onSubmit={addTeam} className="flex gap-2 text-sm">
              <input aria-label={t('web.teams.newName')} className="rounded border px-2 py-1" placeholder={t('web.teams.namePlaceholder')} value={teamName} onChange={(e) => setTeamName(e.target.value)} />
              <select aria-label={t('web.teams.regionOfNew')} className="rounded border px-1 py-1" value={teamRegion} onChange={(e) => setTeamRegion(e.target.value)}>
                <option value="">{t('web.teams.chooseRegion')}</option>
                {regions.data.map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.name}
                  </option>
                ))}
              </select>
              <button type="submit" className="rounded border px-3 py-1" disabled={action.busy || !teamName.trim() || !teamRegion}>
                {t('web.teams.add')}
              </button>
            </form>
          )}
        </div>
      )}
    </section>
  )
}
