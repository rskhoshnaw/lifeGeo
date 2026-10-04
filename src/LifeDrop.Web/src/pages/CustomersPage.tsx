import { useCallback, useState } from 'react'
import { AdminApi, can, errorMessage, Permissions, type Customer, type Me } from '../api/admin.ts'
import { useAction, useLoad } from '../components/useLoad.ts'

export function CustomersPage({ admin, me }: { admin: AdminApi; me: Me }) {
  return (
    <section className="space-y-8 p-6">
      {can(me, ...Permissions.approveCustomers) && <PendingCustomers admin={admin} />}
      {can(me, ...Permissions.customers) && <CustomerList admin={admin} />}
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
      setNoteError('Write a reason before rejecting.')
      return
    }
    setNoteError(null)
    void action.run(() => admin.decideCustomer(customer.id, decision, note))
  }

  const error = noteError ?? action.error ?? pending.error
  return (
    <div>
      <h2 className="mb-4 text-xl font-semibold">Customers waiting for approval</h2>
      {error && <p role="alert" className="mb-3 text-sm text-red-700">{error}</p>}
      {pending.data?.length === 0 && <p className="text-slate-600">Nothing waiting.</p>}
      {pending.data?.map((customer) => (
        <div key={customer.id} className="mb-2 flex flex-wrap items-center gap-2 border-b pb-2 text-sm">
          <span className="min-w-48">
            {customer.code} · {customer.name} ({customer.type})
          </span>
          <input
            aria-label={`Note for ${customer.code}`}
            className="flex-1 rounded border px-2 py-1"
            placeholder="Note (required to reject)"
            value={notes[customer.id] ?? ''}
            onChange={(e) => setNotes({ ...notes, [customer.id]: e.target.value })}
          />
          <button className="rounded border px-2 py-1" disabled={action.busy} onClick={() => decide(customer, 'approve')}>
            Approve
          </button>
          <button className="rounded border px-2 py-1 text-red-700" disabled={action.busy} onClick={() => decide(customer, 'reject')}>
            Reject
          </button>
        </div>
      ))}
    </div>
  )
}

function CustomerList({ admin }: { admin: AdminApi }) {
  const first = useLoad(useCallback(() => admin.customers(), [admin]))
  const [more, setMore] = useState<Customer[]>([])
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const items = [...(first.data ?? []), ...more]

  async function loadMore() {
    try {
      const page = await admin.customers(items[items.length - 1])
      setMore([...more, ...page])
      if (page.length < 100) setDone(true)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div>
      <h2 className="mb-4 text-xl font-semibold">Customers</h2>
      {(error ?? first.error) && <p role="alert" className="mb-3 text-sm text-red-700">{error ?? first.error}</p>}
      {first.data && (
        <table className="w-full text-left text-sm">
          <thead>
            <tr className="border-b">
              <th className="py-2">Code</th>
              <th>Name</th>
              <th>Type</th>
              <th>Approval</th>
            </tr>
          </thead>
          <tbody>
            {items.map((customer) => (
              <tr key={customer.id} className="border-b">
                <td className="py-2">{customer.code}</td>
                <td>{customer.name}</td>
                <td>{customer.type}</td>
                <td>{customer.approvalStatus}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {first.data && first.data.length === 100 && !done && (
        <button className="mt-3 rounded border px-3 py-1 text-sm" onClick={() => void loadMore()}>
          Load more
        </button>
      )}
    </div>
  )
}
