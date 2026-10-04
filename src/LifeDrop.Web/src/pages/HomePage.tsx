import type { Account } from '../api/client.ts'

// Landing page after sign-in; management screens are in the header tabs.
export function HomePage({ account }: { account: Account }) {
  return (
    <section className="p-6">
      <h2 className="text-xl font-semibold">Welcome</h2>
      <p className="mt-2 text-slate-600">
        Signed in as {account.email} ({account.tenantCode}).
      </p>
    </section>
  )
}
