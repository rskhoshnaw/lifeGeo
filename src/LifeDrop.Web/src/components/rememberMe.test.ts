import { readRemembered, rememberDefault, saveRemembered } from './rememberMe.ts'

describe('remember me on the sign-in page (plan step 4.47)', () => {
  it('keeps the company code and e-mail only when ticked, and forgets them when not', () => {
    const storage = window.localStorage
    storage.clear()
    expect(readRemembered(storage)).toBeNull()
    saveRemembered(true, { tenantCode: 'acme', email: 'rep@acme.test' }, storage)
    expect(readRemembered(storage)).toEqual({ tenantCode: 'acme', email: 'rep@acme.test' })
    expect(storage.getItem('lifedrop.remembered')).not.toContain('password')
    saveRemembered(false, { tenantCode: 'acme', email: 'rep@acme.test' }, storage)
    expect(readRemembered(storage)).toBeNull()
  })

  it('ignores damaged or blocked storage', () => {
    window.localStorage.setItem('lifedrop.remembered', '{not json')
    expect(readRemembered()).toBeNull()
    window.localStorage.setItem('lifedrop.remembered', '{"tenantCode":1}')
    expect(readRemembered()).toBeNull()
    const blocked = { getItem: () => { throw new Error('blocked') }, setItem: () => { throw new Error('blocked') }, removeItem: () => { throw new Error('blocked') } } as unknown as Storage
    expect(readRemembered(blocked)).toBeNull()
    expect(() => saveRemembered(true, { tenantCode: 'a', email: 'b' }, blocked)).not.toThrow()
    window.localStorage.clear()
  })

  it('is ticked by default and stays unticked once the user unticks it', () => {
    const storage = window.localStorage
    storage.clear()
    expect(rememberDefault(storage)).toBe(true)
    saveRemembered(false, { tenantCode: 'a', email: 'b' }, storage)
    expect(rememberDefault(storage)).toBe(false)
    saveRemembered(true, { tenantCode: 'a', email: 'b' }, storage)
    expect(rememberDefault(storage)).toBe(true)
    storage.clear()
  })
})
