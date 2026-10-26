import { SessionStore } from './sessionStore.ts'

function memory(): Storage {
  const data = new Map<string, string>()
  return {
    get length() {
      return data.size
    },
    key: (i) => [...data.keys()][i] ?? null,
    getItem: (k) => data.get(k) ?? null,
    setItem: (k, v) => void data.set(k, v),
    removeItem: (k) => void data.delete(k),
    clear: () => data.clear(),
  } as Storage
}

describe('session store (stay signed in)', () => {
  it('keeps the sign-in only for the tab until "stay signed in" is chosen', () => {
    const local = memory()
    const session = memory()
    const store = new SessionStore(local, session)
    store.setItem('lifedrop.refresh', 'r1')
    expect(session.getItem('lifedrop.refresh')).toBe('r1')
    expect(local.getItem('lifedrop.refresh')).toBeNull()
  })

  it('moves the sign-in to the browser when it is chosen and finds it in a new tab', () => {
    const local = memory()
    const session = memory()
    const store = new SessionStore(local, session)
    store.setItem('lifedrop.refresh', 'r1')
    store.setPersistent(true)
    expect(local.getItem('lifedrop.refresh')).toBe('r1')
    expect(session.getItem('lifedrop.refresh')).toBeNull()
    store.setItem('lifedrop.refresh', 'r2')
    expect(local.getItem('lifedrop.refresh')).toBe('r2')
    expect(new SessionStore(local, memory()).getItem('lifedrop.refresh')).toBe('r2')
  })

  it('moves it back and signing out clears every copy', () => {
    const local = memory()
    const session = memory()
    const store = new SessionStore(local, session)
    store.setPersistent(true)
    store.setItem('lifedrop.refresh', 'r1')
    store.setPersistent(false)
    expect(session.getItem('lifedrop.refresh')).toBe('r1')
    expect(local.getItem('lifedrop.refresh')).toBeNull()
    store.setPersistent(true)
    store.removeItem('lifedrop.refresh')
    expect(local.getItem('lifedrop.refresh')).toBeNull()
    expect(session.getItem('lifedrop.refresh')).toBeNull()
  })

  it('works when the browser storage is blocked', () => {
    const blocked = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') }, removeItem: () => { throw new Error('blocked') }, key: () => null, clear: () => {}, length: 0 } as unknown as Storage
    const session = memory()
    const store = new SessionStore(blocked, session)
    store.setPersistent(true)
    store.setItem('lifedrop.refresh', 'r1')
    expect(store.getItem('lifedrop.refresh')).toBe('r1')
    store.removeItem('lifedrop.refresh')
    expect(store.getItem('lifedrop.refresh')).toBeNull()
  })
})
