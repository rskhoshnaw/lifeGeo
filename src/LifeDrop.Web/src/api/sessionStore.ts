// Where the sign-in lives (plan step 5.2b, owner request): in this browser until the user signs out ("keep me signed in", localStorage), or only until the tab
// is closed (sessionStorage). Reading looks in both, writing goes to the chosen one, removing clears both, so a change of mind or a blocked storage never leaves a stale copy.
const ModeKey = 'lifedrop.persist'

export class SessionStore implements Storage {
  private readonly local: Storage | null
  private readonly session: Storage

  constructor(local: Storage | null, session: Storage) {
    this.local = local
    this.session = session
  }

  get persistent(): boolean {
    try {
      return this.local?.getItem(ModeKey) === '1'
    } catch {
      return false
    }
  }

  // Choose where the sign-in is kept from now on; what is already kept moves along.
  setPersistent(on: boolean): void {
    const from = on ? this.session : this.local
    const to = on ? this.local : this.session
    try {
      if (on) this.local?.setItem(ModeKey, '1')
      else this.local?.removeItem(ModeKey)
      if (!from || !to) return
      const keys: string[] = []
      for (let i = 0; i < from.length; i++) {
        const key = from.key(i)
        if (key && key !== ModeKey && key.startsWith('lifedrop.')) keys.push(key)
      }
      for (const key of keys) {
        const value = from.getItem(key)
        if (value !== null) {
          to.setItem(key, value)
          from.removeItem(key)
        }
      }
    } catch {
      // Storage may be blocked (private window); signing in works for this tab without it.
    }
  }

  private get active(): Storage {
    return this.persistent && this.local ? this.local : this.session
  }

  get length(): number {
    return this.active.length
  }

  key(index: number): string | null {
    return this.active.key(index)
  }

  getItem(key: string): string | null {
    try {
      return this.session.getItem(key) ?? this.local?.getItem(key) ?? null
    } catch {
      return this.session.getItem(key)
    }
  }

  setItem(key: string, value: string): void {
    this.active.setItem(key, value)
    try {
      const other = this.active === this.local ? this.session : this.local
      other?.removeItem(key)
    } catch {
      // ignore
    }
  }

  removeItem(key: string): void {
    this.session.removeItem(key)
    try {
      this.local?.removeItem(key)
    } catch {
      // ignore
    }
  }

  clear(): void {
    this.active.clear()
  }
}

export function browserSessionStore(): SessionStore {
  let local: Storage | null = null
  try {
    local = localStorage
  } catch {
    local = null
  }
  return new SessionStore(local, sessionStorage)
}
