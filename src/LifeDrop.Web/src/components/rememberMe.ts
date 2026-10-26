// "Remember me" on the sign-in page (plan steps 4.47 and 5.2b). When it is ticked (the default), the company code and the e-mail are kept in this browser and filled in
// next time, and the sign-in itself stays until the user signs out (see api/sessionStore.ts). The password is never stored by the panel: the browser's own password
// manager fills it (the fields carry the standard autocomplete names). Unticking forgets everything and is remembered, so the box stays unticked next time.
const Key = 'lifedrop.remembered'
const OffKey = 'lifedrop.remember.off'

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
    if (remember) {
      storage.setItem(Key, JSON.stringify(value))
      storage.removeItem(OffKey)
    } else {
      storage.removeItem(Key)
      storage.setItem(OffKey, '1')
    }
  } catch {
    // Storage may be blocked (private window); signing in works without it.
  }
}

// The state of the box when the page opens: ticked unless the user once unticked it.
export function rememberDefault(storage: Storage = localStorage): boolean {
  try {
    return storage.getItem(OffKey) !== '1'
  } catch {
    return true
  }
}
