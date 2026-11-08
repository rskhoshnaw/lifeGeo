import { useCallback, useState } from 'react'
import { AdminApi, can, errorMessage, Permissions, type Customer, type CustomerFilters, type Me, type UserSummary } from '../api/admin.ts'
import { t, type TextKey } from '../i18n.ts'
import { Badge, type Tone } from '../components/Badge.tsx'

const approvalText = (status: string) =>
  ['approved', 'pending', 'rejected'].includes(status) ? t(`web.approval.${status}` as TextKey) : status
import { useAction, useLoad } from '../components/useLoad.ts'
import { DoctorImport } from '../components/DoctorImport.tsx'
import { CustomerProfileEditor } from '../components/CustomerProfileEditor.tsx'
import { CustomerPriorityBar } from '../components/CustomerPriorityBar.tsx'
import { CustomerFilterBar } from '../components/CustomerFilterBar.tsx'
import { CustomerOwnerBar } from '../components/CustomerOwnerBar.tsx'
import { CustomerDetailsEditor } from '../components/CustomerDetailsEditor.tsx'
import { CustomerHistoryPanel } from '../components/CustomerHistoryPanel.tsx'
import { CustomerCard } from '../components/CustomerCard.tsx'
import { VisitWindowsEditor } from '../components/VisitWindowsEditor.tsx'
import { SortTh, useSort } from '../components/sort.tsx'

export function CustomersPage({ admin, me }: { admin: AdminApi; me: Me }) {
  const [reload, setReload] = useState(0)
  return (
    <section className="space-y-8 p-6">
      {can(me, ...Permissions.approveCustomers) && <PendingCustomers admin={admin} />}
      {can(me, ...Permissions.editCustomers) && <DoctorImport admin={admin} onImported={() => setReload((n) => n + 1)} />}
      {can(me, ...Permissions.customers) && <CustomerList key={reload} admin={admin} canEdit={can(me, ...Permissions.editCustomers)} canMove={can(me, ...Permissions.moveCustomers)} canArchive={can(me, ...Permissions.approveCustomers)} canPrioritise={can(me, ...Permissions.planVisits)} canAssign={can(me, ...Permissions.approveCustomers)} />}
    </section>
  )
}

// Customers registered outside the rep's region (decision D11); the server limits the queue to the actor's area.
function PendingCustomers({ admin }: { admin: AdminApi }) {
  const pending = useLoad(useCallback(() => admin.customerApprovals(), [admin]))
  const action = useAction(pending.reload)
  const [notes, setNotes] = useState<Record<string, string>>({})
  const [noteError, setNoteError] = useState<string | null>(null)

  function decide(customer: Customer, decision: 'approve' | 'reject') {
    const note = (notes[customer.id] ?? '').trim()
    if (decision === 'reject' && !note) {
      setNoteError(t('web.customers.rejectNeedsNote'))
      return
    }
    setNoteError(null)
    void action.run(() => admin.decideCustomer(customer.id, decision, note))
  }

  const error = noteError ?? action.error ?? pending.error
  return (
    <div>
      <h2 className="mb-4 text-xl font-semibold">{t('web.customers.pendingTitle')}</h2>
      {error && <p role="alert" className="mb-3 text-sm text-red-700">{error}</p>}
      {pending.data?.length === 0 && <p className="text-slate-600">{t('web.customers.nothingWaiting')}</p>}
      {pending.data?.map((customer) => (
        <div key={customer.id} className="mb-2 flex flex-wrap items-center gap-2 border-b pb-2 text-sm">
          <span className="min-w-48">
            {customer.code} · {customer.name} ({customer.type})
          </span>
          <input
            aria-label={t('web.customers.noteFor', { code: customer.code })}
            className="flex-1 rounded border px-2 py-1"
            placeholder={t('web.customers.notePlaceholder')}
            value={notes[customer.id] ?? ''}
            onChange={(e) => setNotes({ ...notes, [customer.id]: e.target.value })}
          />
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => decide(customer, 'approve')}>
            {t('web.action.approve')}
          </button>
          <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} onClick={() => decide(customer, 'reject')}>
            {t('web.action.reject')}
          </button>
        </div>
      ))}
    </div>
  )
}

const day = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Baghdad', dateStyle: 'short' })

// The list as a CSV file (UTF-8 with a mark, so Excel reads Kurdish and Arabic names); it holds the rows that are loaded.
function downloadCsv(rows: Customer[]) {
  const cell = (value: string | null | undefined) => `"${(value ?? '').replace(/"/g, '""')}"`
  const lines = [['code', 'name', 'type', 'phone', 'owner', 'added_by', 'added_on', 'location'].join(',')]
  for (const c of rows)
    lines.push([c.code, c.name, c.type, c.phone, c.ownerEmail, c.createdByEmail, day.format(new Date(c.createdAt)), c.status].map(cell).join(','))
  const url = URL.createObjectURL(new Blob(['\ufeff' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = 'customers.csv'
  link.click()
  URL.revokeObjectURL(url)
}

function CustomerList({ admin, canEdit, canMove, canArchive, canPrioritise, canAssign }: { admin: AdminApi; canEdit: boolean; canMove: boolean; canArchive: boolean; canPrioritise: boolean; canAssign: boolean }) {
  const [archived, setArchived] = useState(false)
  const [search, setSearch] = useState('')
  const [filters, setFilters] = useState<CustomerFilters>({})
  const term = search.trim().length >= 2 ? search.trim() : ''
  const [showFilters, setShowFilters] = useState(false)
  const activeFilters = Object.values(filters).filter(Boolean).length
  const users = useLoad(useCallback(() => admin.users().catch(() => [] as UserSummary[]), [admin]))
  const first = useLoad(useCallback(() => admin.customers(undefined, archived, term, filters), [admin, archived, term, filters]))
  const action = useAction(() => reload())
  const [editFor, setEditFor] = useState<Customer | null>(null)
  const [openFor, setOpenFor] = useState<Customer | null>(null)
  const [more, setMore] = useState<Customer[]>([])
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hoursFor, setHoursFor] = useState<Customer | null>(null)
  const [profileFor, setProfileFor] = useState<Customer | null>(null)
  const [historyFor, setHistoryFor] = useState<Customer | null>(null)
  const [ticked, setTicked] = useState<string[]>([])
  const items = [...(first.data ?? []), ...more]
  const sorted = useSort(items, { code: (c) => c.code, name: (c) => c.name, type: (c) => c.type, owner: (c) => c.ownerEmail ?? '', addedBy: (c) => c.createdByEmail ?? '', addedOn: (c) => c.createdAt, approval: (c) => c.approvalStatus, priority: (c) => t(`web.priority.${c.priority}`) })

  function reload() {
    setEditFor(null)
    setMore([])
    setDone(false)
    first.reload()
  }

  function archive(customer: Customer) {
    if (!window.confirm(t('web.customers.confirmArchive', { customer: `${customer.code} · ${customer.name}` }))) return
    void action.run(() => admin.archiveCustomer(customer.id))
  }

  async function loadMore() {
    try {
      const page = await admin.customers(items[items.length - 1], archived, term, filters)
      setMore([...more, ...page])
      if (page.length < 100) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div>
      <h2 className="mb-4 text-xl font-semibold">{t('web.customers.title')}</h2>
      {(error ?? first.error ?? action.error) && <p role="alert" className="mb-3 text-sm text-red-700">{error ?? first.error ?? action.error}</p>}
      <input
        aria-label={t('web.customers.search')}
        placeholder={t('web.customers.search')}
        className="mb-3 block rounded border px-2 py-1 text-sm"
        value={search}
        onChange={(e) => {
          setSearch(e.target.value)
          setMore([])
          setDone(false)
        }}
      />
      {canArchive && (
        <label className="mb-3 flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={archived}
            onChange={(e) => {
              setArchived(e.target.checked)
              setMore([])
              setDone(false)
            }}
          />
          {t('web.customers.showArchived')}
        </label>
      )}
      <button className="mb-3 rounded border px-2 py-1 text-sm" aria-expanded={showFilters} onClick={() => setShowFilters(!showFilters)}>
        {t('web.customers.filtersTitle')}{activeFilters > 0 ? ` (${activeFilters})` : ''}
      </button>
      {showFilters && (
        <CustomerFilterBar
          admin={admin}
          users={users.data ?? []}
          filters={filters}
          onChange={(next) => {
            setFilters(next)
            setMore([])
            setDone(false)
          }}
        />
      )}
      {canAssign && !archived && (
        <CustomerOwnerBar
          admin={admin}
          users={users.data ?? []}
          ticked={ticked}
          onDone={() => {
            setTicked([])
            setMore([])
            setDone(false)
            first.reload()
          }}
        />
      )}
      {canPrioritise && (
        <CustomerPriorityBar
          admin={admin}
          ticked={ticked}
          onDone={() => {
            setTicked([])
            setMore([])
            setDone(false)
            first.reload()
          }}
        />
      )}
      {first.data && (
        <div className="mb-2 flex items-center gap-3 text-sm">
          <span>{t('web.customers.shown', { count: items.length })}</span>
          <button className="rounded border px-2 py-1" onClick={() => downloadCsv(sorted.rows ?? items)}>{t('web.customers.exportCsv')}</button>
        </div>
      )}
      {first.data && (
        <table className="w-full text-start text-sm">
          <thead>
            <tr className="border-b">
              {(canPrioritise || canAssign) && <th />}
              <SortTh id="code" className="py-2" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.code')}</SortTh>
              <SortTh id="name" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.name')}</SortTh>
              <SortTh id="type" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.type')}</SortTh>
              <SortTh id="owner" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.owner')}</SortTh>
              <SortTh id="addedBy" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.addedBy')}</SortTh>
              <SortTh id="addedOn" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.addedOn')}</SortTh>
              <SortTh id="approval" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.approval')}</SortTh>
              <SortTh id="priority" sort={sorted.sort} onSort={sorted.toggle}>{t('web.customers.priority')}</SortTh>
              <th />
            </tr>
          </thead>
          <tbody>
            {(sorted.rows ?? items).map((customer) => (
              <tr key={customer.id} className="border-b">
                {(canPrioritise || canAssign) && (
                  <td>
                    <input
                      type="checkbox"
                      aria-label={t('web.customers.tick', { code: customer.code })}
                      checked={ticked.includes(customer.id)}
                      onChange={(e) => setTicked(e.target.checked ? [...ticked, customer.id] : ticked.filter((id) => id !== customer.id))}
                    />
                  </td>
                )}
                <td className="py-2">{customer.code}</td>
                <td>
                  <button className="text-start text-brand-700 underline" aria-label={t('web.customers.open', { customer: customer.name })} onClick={() => setOpenFor(customer)}>{customer.name}</button>
                </td>
                <td>{customer.type}</td>
                <td>{customer.ownerEmail ?? '—'}</td>
                <td>{customer.createdByEmail ?? '—'}</td>
                <td>{day.format(new Date(customer.createdAt))}</td>
                <td><Badge tone={({ approved: 'good', pending: 'warn', rejected: 'bad' } as Record<string, Tone>)[customer.approvalStatus] ?? 'neutral'}>{approvalText(customer.approvalStatus)}</Badge></td>
                <td>{t(`web.priority.${customer.priority}`)}</td>
                <td className="space-x-2 text-end">
                  {canEdit && !archived && (
                    <button className="rounded border px-2 py-1" aria-label={`${t('web.customers.edit')} ${customer.code}`} onClick={() => setEditFor(customer)}>
                      {t('web.customers.edit')}
                    </button>
                  )}
                  {canArchive && (
                    <button
                      className="rounded border px-2 py-1"
                      aria-label={`${archived ? t('web.customers.restore') : t('web.customers.archive')} ${customer.code}`}
                      disabled={action.busy}
                      onClick={() => (archived ? void action.run(() => admin.restoreCustomer(customer.id)) : archive(customer))}
                    >
                      {archived ? t('web.customers.restore') : t('web.customers.archive')}
                    </button>
                  )}
                  <button className="rounded border px-2 py-1" aria-label={`${t('web.customers.profile')} ${customer.code}`} onClick={() => setProfileFor(customer)}>
                    {t('web.customers.profile')}
                  </button>
                  <button className="rounded border px-2 py-1" aria-label={`${t('web.customers.history')} ${customer.code}`} onClick={() => setHistoryFor(customer)}>
                    {t('web.customers.history')}
                  </button>
                  <button className="rounded border px-2 py-1" onClick={() => setHoursFor(customer)}>
                    {t('web.customers.visitHours')}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {openFor && (
        <CustomerCard
          key={openFor.id}
          admin={admin}
          customer={openFor}
          canEdit={canEdit && !archived}
          canMove={canMove && !archived}
          onEdit={() => setEditFor(openFor)}
          onProfile={() => setProfileFor(openFor)}
          onHours={() => setHoursFor(openFor)}
          onHistory={() => setHistoryFor(openFor)}
          onClose={() => setOpenFor(null)}
        />
      )}
      {editFor && <CustomerDetailsEditor key={editFor.id} admin={admin} customer={editFor} onClose={() => setEditFor(null)} onSaved={reload} />}
      {historyFor && <CustomerHistoryPanel key={historyFor.id} admin={admin} customer={historyFor} onClose={() => setHistoryFor(null)} />}
      {profileFor && <CustomerProfileEditor key={profileFor.id} admin={admin} customer={profileFor} canEdit={canEdit} onClose={() => setProfileFor(null)} />}
      {hoursFor && <VisitWindowsEditor key={hoursFor.id} admin={admin} customer={hoursFor} canEdit={canEdit} onClose={() => setHoursFor(null)} />}
      {first.data && first.data.length === 100 && !done && (
        <button className="mt-3 rounded border px-3 py-1 text-sm" onClick={() => void loadMore()}>
          {t('web.action.loadMore')}
        </button>
      )}
    </div>
  )
}
