// Client for the LifeDrop API used by the web panel (plan step 1.16).
// The access token lives only in memory. The refresh token is kept in sessionStorage so a page reload in the same
// tab stays signed in; it is gone when the tab closes. Refresh tokens rotate on every use (server rule), and
// concurrent requests share a single refresh.

export interface TokenResponse {
  accessToken: string
  refreshToken: string
  tokenType: string
  expiresAt: string
  refreshExpiresAt: string
}

export type LoginError = 'invalid_credentials' | 'device_only' | 'too_many_attempts' | 'network' | 'server'

export class LoginFailed extends Error {
  readonly reason: LoginError
  constructor(reason: LoginError) {
    super(reason)
    this.reason = reason
  }
}

export class SessionExpired extends Error {
  constructor() {
    super('session_expired')
  }
}

const RefreshKey = 'lifedrop.refresh'
const AccountKey = 'lifedrop.account'

export interface Account {
  tenantCode: string
  email: string
}

export interface CompanyProfile {
  industry: string
  hiddenModules: string[]
  texts: Record<string, Partial<Record<'ckb' | 'ar' | 'fa' | 'en', string>>>
}

export class ApiClient {
  private readonly baseUrl: string
  private readonly fetcher: typeof fetch
  private readonly storage: Storage
  private accessToken: string | null = null
  private refreshing: Promise<boolean> | null = null
  private readonly listeners = new Set<() => void>()

  constructor(baseUrl: string, fetcher: typeof fetch = (...args) => fetch(...args), storage: Storage = sessionStorage) {
    this.baseUrl = baseUrl.replace(/\/+$/, '')
    this.fetcher = fetcher
    this.storage = storage
  }

  get account(): Account | null {
    const text = this.storage.getItem(AccountKey)
    if (!text || !this.storage.getItem(RefreshKey)) return null
    try {
      return JSON.parse(text) as Account
    } catch {
      return null
    }
  }

  // Called when the session ends (logout or a refresh the server refused).
  onSignedOut(listener: () => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  async login(tenantCode: string, email: string, password: string): Promise<Account> {
    let response: Response
    try {
      response = await this.fetcher(`${this.baseUrl}/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tenantCode, email, password }),
      })
    } catch {
      throw new LoginFailed('network')
    }
    if (response.status === 401 || response.status === 400) throw new LoginFailed('invalid_credentials')
    // Field roles must sign in from their approved phone (device binding); the panel has no device.
    if (response.status === 403) throw new LoginFailed('device_only')
    if (response.status === 429) throw new LoginFailed('too_many_attempts')
    if (!response.ok) throw new LoginFailed('server')
    const tokens = (await response.json()) as TokenResponse
    const account = { tenantCode, email }
    this.store(tokens)
    this.storage.setItem(AccountKey, JSON.stringify(account))
    return account
  }

  async logout(): Promise<void> {
    const token = this.accessToken
    if (token) {
      try {
        await this.fetcher(`${this.baseUrl}/auth/logout`, { method: 'POST', headers: { Authorization: `Bearer ${token}` } })
      } catch {
        // Signed out locally anyway; the session expires on the server.
      }
    }
    this.clear()
  }

  // Authorized request. On 401 the session is refreshed once and the request repeated; if the refresh fails
  // the user is signed out.
  // Address of a file served without a login (a product photo), for an <img src>.
  publicUrl(path: string): string {
    return `${this.baseUrl}${path}`
  }

  async request(path: string, init: RequestInit = {}): Promise<Response> {
    if (!this.accessToken && !(await this.refresh())) throw new SessionExpired()
    let response = await this.send(path, init)
    if (response.status !== 401) return response
    if (!(await this.refresh())) throw new SessionExpired()
    response = await this.send(path, init)
    if (response.status === 401) {
      this.clear()
      throw new SessionExpired()
    }
    return response
  }

  /** Industry, the parts the company switched off and its own wording, from the public company directory (plan step 4.48); any failure means the defaults. */
  async companyProfile(tenantCode: string): Promise<CompanyProfile> {
    const none: CompanyProfile = { industry: 'pharma', hiddenModules: [], texts: {} }
    try {
      const response = await this.fetcher(`${this.baseUrl}/company-config/${encodeURIComponent(tenantCode.toLowerCase())}`)
      if (!response.ok) return none
      const body = (await response.json()) as { industry?: string; hiddenModules?: string[]; texts?: CompanyProfile['texts'] }
      return { industry: body.industry === 'food' ? 'food' : 'pharma', hiddenModules: Array.isArray(body.hiddenModules) ? body.hiddenModules : [], texts: body.texts ?? {} }
    } catch {
      return none
    }
  }

  /** Wording profile of the company ("food" or "pharma"), read from the public company directory; any failure means the default wording. */
  async industry(tenantCode: string): Promise<string> {
    try {
      const response = await this.fetcher(`${this.baseUrl}/company-config/${encodeURIComponent(tenantCode.toLowerCase())}`)
      if (!response.ok) return 'pharma'
      const body = (await response.json()) as { industry?: string }
      return body.industry === 'food' ? 'food' : 'pharma'
    } catch {
      return 'pharma'
    }
  }

  private send(path: string, init: RequestInit): Promise<Response> {
    const headers = new Headers(init.headers)
    headers.set('Authorization', `Bearer ${this.accessToken}`)
    return this.fetcher(`${this.baseUrl}${path}`, { ...init, headers })
  }

  private refresh(): Promise<boolean> {
    this.refreshing ??= this.doRefresh().finally(() => {
      this.refreshing = null
    })
    return this.refreshing
  }

  private async doRefresh(): Promise<boolean> {
    const refreshToken = this.storage.getItem(RefreshKey)
    if (!refreshToken) return false
    let response: Response
    try {
      response = await this.fetcher(`${this.baseUrl}/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })
    } catch {
      return false // offline: keep the refresh token for the next try
    }
    if (!response.ok) {
      this.clear()
      return false
    }
    this.store((await response.json()) as TokenResponse)
    return true
  }

  private store(tokens: TokenResponse) {
    this.accessToken = tokens.accessToken
    this.storage.setItem(RefreshKey, tokens.refreshToken)
  }

  private clear() {
    const wasSignedIn = this.accessToken !== null || this.storage.getItem(RefreshKey) !== null
    this.accessToken = null
    this.storage.removeItem(RefreshKey)
    this.storage.removeItem(AccountKey)
    if (wasSignedIn) this.listeners.forEach((listener) => listener())
  }
}
