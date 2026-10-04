import type { Account } from '../api/client.ts'

// Placeholder until the management screens (plan steps 1.17-1.20).
export function HomePage({ account }: { account: Account }) {
  return (
    <section className="p-6">
      <h2 className="text-xl font-semibold">Welcome</h2>
      <p className="mt-2 text-slate-600">
        Signed in as {account.email} ({account.tenantCode}). Management screens come in the next steps.
      </p>
    </section>
  )
}
