// "Remember me" on the sign-in page (plan step 4.47, owner decision: the safe kind). When it is ticked, the company code and the e-mail are kept in this browser and
// filled in next time; the password is never kept and is asked every time, and no session survives closing the tab. Off by default.
const Key = 'lifedrop.remembered'

export interface Remembered {
  tenantCode: string
  email: string
}

export function readRemembered(storage: Storage = localStorage): Remembered | null {
  try {
    const value = JSON.parse(storage.getItem(Key) ?? 'null') as Partial<Remembered> | null
    return value && typeof value.tenantCode === 'string' && typeof value.email === 'string' ? { tenantCode: value.tenantCode, email: value.email } : null
  } catch {
    return null
  }
}

// Ticked: keep the two values. Not ticked: forget whatever was kept.
export function saveRemembered(remember: boolean, value: Remembered, storage: Storage = localStorage): void {
  try {
    if (remember) storage.setItem(Key, JSON.stringify(value))
    else storage.removeItem(Key)
  } catch {
    // Storage may be blocked (private window); signing in works without it.
  }
}
