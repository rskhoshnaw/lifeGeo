// Panel calls for users, devices and customers (plan step 1.17), visit planning, regions and teams (step 1.18). The server checks every action again;
// permissions from /me only decide which buttons the panel shows.
import type { ApiClient } from './client.ts'

export interface Me {
  id: string
  email: string | null
  roles: string[]
  permissions: string[]
}

export interface UserSummary {
  id: string
  email: string | null
  roles: string[]
  teamId: string | null
  teamName: string | null
  regionId: string | null
  activeSessions: number
}

export interface Role {
  id: string
  name: string
  dataScope: string
}

export type DeviceStatus = 'pending' | 'approved' | 'blocked'

export interface Device {
  id: string
  userId: string
  userEmail: string | null
  label: string
  platform: string
  appVersion: string
  status: DeviceStatus
  registeredAt: string
}

export interface Customer {
  id: string
  type: string
  code: string
  name: string
  status: string
  createdAt: string
  regionId: string | null
  approvalStatus: string
}

export type VisitType = 'pharmacy' | 'doctor' | 'wholesaler' | 'other'
export type VisitPriority = 'high' | 'normal' | 'low'

export interface PlannedVisit {
  id: string
  userId: string
  customerId: string
  status: string
  exceptionReason: string | null
  scheduleDate: string
  visitType: VisitType
  priority: VisitPriority
  plannedTime: string | null
  isUnplanned: boolean
  customerName: string | null
  customerCode: string | null
  assigneeEmail: string | null
}

export interface NewVisit {
  assignedUserId: string
  customerId: string
  scheduleDate: string
  visitType: VisitType
  priority: VisitPriority
  plannedTime: string | null
}

export interface Region {
  id: string
  name: string
}

export interface Team {
  id: string
  name: string
  regionId: string
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string | null
  constructor(status: number, code: string | null) {
    super(code ?? `HTTP ${status}`)
    this.status = status
    this.code = code
  }
}

export function can(me: Me | null, ...keys: string[]): boolean {
  return me !== null && keys.some((key) => me.permissions.includes(key))
}

// Keys match the server rules of each route (see docs/phase0-api-access-matrix.md).
export const Permissions = {
  users: ['sessions.kill', 'user.manageroles', 'system.admin'],
  killSessions: ['sessions.kill'],
  assignRoles: ['user.manageroles', 'system.admin'],
  devices: ['device.approve', 'devices.manage', 'system.admin'],
  customers: ['customers.read'],
  approveCustomers: ['customers.approve'],
  planVisits: ['visits.write'],
  cancelVisits: ['visits.cancel'],
  regions: ['regions.read'],
  manageRegions: ['regions.manage'],
  readTeams: ['teams.read'],
  manageTeams: ['teams.manage'],
} as const

// Iraq local date (Asia/Baghdad) as YYYY-MM-DD, the server's "today" for visit plans.
export function iraqToday(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Baghdad' }).format(now)
}

export function errorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return 'No connection to the server. Try again.'
  switch (error.status) {
    case 400:
      return 'The server refused the data. Check the fields and try again.'
    case 403:
      return 'You do not have permission for this action.'
    case 404:
      return 'Not found, or outside your area.'
    case 409:
      if (error.code === 'CUSTOMER_NOT_APPROVED') return 'This customer is not approved yet.'
      if (error.code === 'DUPLICATE_NAME') return 'This name is already used.'
      return 'This was already changed. Refresh the list.'
    case 429:
      return 'Too many requests. Wait a minute and try again.'
    default:
      return 'The server could not complete the request. Try again later.'
  }
}

export class AdminApi {
  private readonly api: ApiClient
  constructor(api: ApiClient) {
    this.api = api
  }

  me = () => this.get<Me>('/me')
  users = () => this.get<UserSummary[]>('/users')
  roles = () => this.get<Role[]>('/roles')
  devices = (status?: DeviceStatus) => this.get<Device[]>(status ? `/devices?status=${status}` : '/devices')
  approveDevice = (id: string) => this.post(`/devices/${id}/approve`)
  blockDevice = (id: string) => this.post(`/devices/${id}/block`)
  killSessions = (userId: string) => this.post(`/users/${userId}/sessions/revoke`)
  assignRole = (userId: string, roleId: string) => this.post(`/users/${userId}/roles/${roleId}`)
  customers = (after?: Customer) =>
    this.get<Customer[]>(
      after
        ? `/customers?limit=100&cursorCreatedAt=${encodeURIComponent(after.createdAt)}&cursorId=${after.id}`
        : '/customers?limit=100',
    )
  customerApprovals = () => this.get<Customer[]>('/customer-approvals')
  decideCustomer = (id: string, decision: 'approve' | 'reject', note: string) =>
    this.post(`/customers/${id}/approval`, { decision, note: note || null })

  visitPlan = (date: string, assignedUserId?: string) =>
    this.get<PlannedVisit[]>(`/visit-plan?scheduleDate=${date}${assignedUserId ? `&assignedUserId=${assignedUserId}` : ''}`)
  createVisit = (visit: NewVisit) => this.post('/visits', visit)
  reassignVisit = (id: string, assignedUserId: string) => this.send('PUT', `/visits/${id}/assignee`, { assignedUserId })
  cancelVisit = (id: string, reason: string) => this.post(`/visits/${id}/cancel`, { reason })
  regions = () => this.get<Region[]>('/regions')
  teams = () => this.get<Team[]>('/teams')
  createRegion = (name: string) => this.post('/regions', { name })
  createTeam = (name: string, regionId: string) => this.post('/teams', { name, regionId })
  moveTeam = (teamId: string, regionId: string) => this.send('PUT', `/teams/${teamId}/region`, { regionId })
  addMember = (teamId: string, userId: string) => this.send('PUT', `/teams/${teamId}/members/${userId}`)
  removeMember = (teamId: string, userId: string) => this.send('DELETE', `/teams/${teamId}/members/${userId}`)

  private async get<T>(path: string): Promise<T> {
    const response = await this.api.request(path)
    if (!response.ok) throw await toError(response)
    return (await response.json()) as T
  }

  private post(path: string, body?: unknown): Promise<void> {
    return this.send('POST', path, body)
  }

  private async send(method: string, path: string, body?: unknown): Promise<void> {
    const response = await this.api.request(path, {
      method,
      ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
    })
    if (!response.ok) throw await toError(response)
  }
}

async function toError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { code?: unknown }
    return new ApiError(response.status, typeof problem.code === 'string' ? problem.code : null)
  } catch {
    return new ApiError(response.status, null)
  }
}
