import { ApiClient, LoginFailed, SessionExpired } from './client.ts'

const tokens = (n: number) => ({ accessToken: `access-${n}`, refreshToken: `refresh-${n}`, tokenType: 'Bearer', expiresAt: '', refreshExpiresAt: '' })
const json = (status: number, body?: unknown) => new Response(body === undefined ? null : JSON.stringify(body), { status })

function setup(handler: (url: string, init: RequestInit) => Response | Promise<Response>) {
  const calls: { url: string; init: RequestInit }[] = []
  const fetcher = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), init: init ?? {} })
    return handler(String(input), init ?? {})
  }) as typeof fetch
  sessionStorage.clear()
  return { api: new ApiClient('https://api.test/', fetcher, sessionStorage), calls }
}

const auth = (init: RequestInit) => new Headers(init.headers).get('Authorization')

describe('ApiClient', () => {
  it('signs in and keeps only the refresh token in session storage', async () => {
    const { api, calls } = setup(() => json(200, tokens(1)))
    const account = await api.login('T1', 'a@b.c', 'secret')
    expect(account).toEqual({ tenantCode: 'T1', email: 'a@b.c' })
    expect(calls[0].url).toBe('https://api.test/auth/login')
    expect(JSON.parse(String(calls[0].init.body))).toEqual({ tenantCode: 'T1', email: 'a@b.c', password: 'secret' })
    expect(sessionStorage.getItem('lifedrop.refresh')).toBe('refresh-1')
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('access-1')
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('secret')
    expect(api.account).toEqual(account)
  })

  it.each([
    [401, 'invalid_credentials'],
    [400, 'invalid_credentials'],
    [403, 'device_only'],
    [429, 'too_many_attempts'],
    [500, 'server'],
  ])('maps login status %i to %s', async (status, reason) => {
    const { api } = setup(() => json(status))
    await expect(api.login('T1', 'a@b.c', 'x')).rejects.toEqual(new LoginFailed(reason as never))
    expect(api.account).toBeNull()
  })

  it('reports a network failure on login', async () => {
    const { api } = setup(() => Promise.reject(new TypeError('offline')))
    await expect(api.login('T1', 'a@b.c', 'x')).rejects.toMatchObject({ reason: 'network' })
  })

  it('refreshes once on 401, rotates the refresh token and repeats the request', async () => {
    let n = 1
    const { api, calls } = setup((url, init) => {
      if (url.endsWith('/auth/login')) return json(200, tokens(1))
      if (url.endsWith('/auth/refresh')) return json(200, tokens(++n))
      return auth(init) === 'Bearer access-1' ? json(401) : json(200, { ok: true })
    })
    await api.login('T1', 'a@b.c', 'x')
    const [a, b] = await Promise.all([api.request('/teams'), api.request('/regions')])
    expect([a.status, b.status]).toEqual([200, 200])
    expect(calls.filter((c) => c.url.endsWith('/auth/refresh'))).toHaveLength(1)
    expect(JSON.parse(String(calls.find((c) => c.url.endsWith('/auth/refresh'))!.init.body))).toEqual({ refreshToken: 'refresh-1' })
    expect(sessionStorage.getItem('lifedrop.refresh')).toBe('refresh-2')
  })

  it('signs out when the refresh is refused', async () => {
    const { api } = setup((url) => (url.endsWith('/auth/login') ? json(200, tokens(1)) : json(401)))
    await api.login('T1', 'a@b.c', 'x')
    const signedOut = vi.fn()
    api.onSignedOut(signedOut)
    await expect(api.request('/teams')).rejects.toBeInstanceOf(SessionExpired)
    expect(signedOut).toHaveBeenCalledTimes(1)
    expect(api.account).toBeNull()
  })

  it('after a page reload uses the stored refresh token before the first request', async () => {
    const { api, calls } = setup((url) => (url.endsWith('/auth/refresh') ? json(200, tokens(2)) : json(200, {})))
    sessionStorage.setItem('lifedrop.refresh', 'refresh-1')
    sessionStorage.setItem('lifedrop.account', JSON.stringify({ tenantCode: 'T1', email: 'a@b.c' }))
    expect(api.account).toEqual({ tenantCode: 'T1', email: 'a@b.c' })
    await api.request('/teams')
    expect(calls.map((c) => c.url)).toEqual(['https://api.test/auth/refresh', 'https://api.test/teams'])
    expect(auth(calls[1].init)).toBe('Bearer access-2')
  })

  it('logs out on the server and clears the session', async () => {
    const { api, calls } = setup((url) => (url.endsWith('/auth/login') ? json(200, tokens(1)) : json(204)))
    await api.login('T1', 'a@b.c', 'x')
    await api.logout()
    expect(calls[1].url).toBe('https://api.test/auth/logout')
    expect(auth(calls[1].init)).toBe('Bearer access-1')
    expect(api.account).toBeNull()
    expect(sessionStorage.length).toBe(0)
  })
})
