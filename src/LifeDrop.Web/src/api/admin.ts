// Panel calls for users, devices and customers (plan step 1.17), visit planning, regions and teams (step 1.18),
// live map (step 1.19), audit viewer (step 1.20). The server checks every action again;
// permissions from /me only decide which buttons the panel shows.
import type { ApiClient } from './client.ts'
import { t } from '../i18n.ts'

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
  // Profile (plan step 1.23-b): contact details and the most precise place; placePath lists it and the ones above it.
  phone: string | null
  address: string | null
  placeId: string | null
  placePath: PlacePathItem[] | null
}

// A message for the signed-in user (plan step 1.23-c): somebody other than the owner changed a customer of the user's area.
export interface Notification {
  id: string
  kind: 'customer.profile_changed' | 'customer.location_changed'
  entityId: string
  actorId: string
  actorEmail: string | null
  customerCode: string | null
  customerName: string | null
  createdAt: string
  readAt: string | null
}

export interface PlacePathItem {
  id: string
  kind: PlaceKind
  name: string
}

// Places tree: city -> neighbourhood -> street -> alley; childCount counts the active places directly below.
export type PlaceKind = 'city' | 'neighbourhood' | 'street' | 'alley'

export interface Place {
  id: string
  parentId: string | null
  kind: PlaceKind
  name: string
  nameCkb: string | null
  nameAr: string | null
  nameEn: string | null
  status: 'active' | 'inactive'
  mergedInto: string | null
  childCount: number
}

// Names of one place: the main name and the optional Kurdish, Arabic and English names.
export interface PlaceNames {
  name: string
  nameCkb: string | null
  nameAr: string | null
  nameEn: string | null
}

// One row of an import file: the levels above are named, missing ones are created.
export interface ImportRow {
  kind: PlaceKind
  name: string
  city: string | null
  neighbourhood: string | null
  street: string | null
  nameCkb: string | null
  nameAr: string | null
  nameEn: string | null
  active: boolean
}

export interface ImportSummary {
  created: number
  skipped: number
}

export interface CustomerProfile {
  phone: string | null
  address: string | null
  placeId: string | null
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
  // Flexible planning (plan step 1.23): manual position, times the visit was moved to another day (with the last
  // reason) and whether its day/time fits the customer's accepted hours ("none" = the customer has none).
  sortOrder: number | null
  postponeCount: number
  postponeReason: string | null
  windowStatus: 'none' | 'within' | 'outside'
}

// Day (0 = Sunday .. 6 = Saturday) and local hours (HH:mm:ss) in which a customer accepts visits.
export interface VisitWindow {
  weekday: number
  opensAt: string
  closesAt: string
}

export interface NewVisit {
  assignedUserId: string
  customerId: string
  scheduleDate: string
  visitType: VisitType
  priority: VisitPriority
  plannedTime: string | null
}

export type PlanOrder = 'least_recent' | 'name' | 'code'
export interface PlanRequest {
  assignedUserId: string
  scheduleDate: string
  placeId?: string | null
  regionId?: string | null
  count?: number | null
  order?: PlanOrder
  respectWindows?: boolean
  visitType?: VisitType | null
  priority?: VisitPriority | null
  customerIds?: string[] | null
}
export interface PlanCandidate {
  customerId: string
  code: string
  name: string
  type: string
  place: string | null
  lastVisitDate: string | null
  windowStatus: string
  included: boolean
  skipReason: 'outside_hours' | 'over_limit' | null
}
export interface PlanPreview {
  matching: number
  included: number
  candidates: PlanCandidate[]
}
export interface PlanGenerated {
  created: number
  skipped: number
  visitIds: string[]
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

// One user on shift (plan step 1.19, decision D3). Without tracking.read for that user, precise is false and the
// coordinates are null; isLive is false when the last point is older than 5 minutes.
export interface LiveMapEntry {
  userId: string
  email: string | null
  teamName: string | null
  shiftStartedAt: string
  lastUpdateAt: string | null
  isLive: boolean
  precise: boolean
  latitude: number | null
  longitude: number | null
  accuracyMeters: number | null
  isMocked: boolean | null
  lastCustomerName: string | null
  lastVisitAt: string | null
}

export interface AuditEntry {
  id: string
  actorId: string
  actorEmail: string | null
  action: string
  entityId: string
  occurredAt: string
}

// Filters of the audit search; dates are Iraq local days (YYYY-MM-DD), inclusive.
export interface AuditFilter {
  action?: string
  actorId?: string
  entityId?: string
  fromDate?: string
  toDate?: string
}

export const auditPageSize = 50

export function auditQuery(filter: AuditFilter, after?: AuditEntry): string {
  const params = new URLSearchParams({ limit: String(auditPageSize) })
  if (filter.action) params.set('action', filter.action)
  if (filter.actorId) params.set('actorId', filter.actorId)
  if (filter.entityId) params.set('entityId', filter.entityId)
  if (filter.fromDate) params.set('from', `${filter.fromDate}T00:00:00+03:00`)
  if (filter.toDate) params.set('to', `${filter.toDate}T23:59:59.999+03:00`)
  if (after) {
    params.set('cursorOccurredAt', after.occurredAt)
    params.set('cursorId', after.id)
  }
  return `/audit-logs?${params.toString()}`
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
  editCustomers: ['customers.write'],
  places: ['places.manage'],
  approveCustomers: ['customers.approve'],
  planVisits: ['visits.write'],
  cancelVisits: ['visits.cancel'],
  regions: ['regions.read'],
  manageRegions: ['regions.manage'],
  readTeams: ['teams.read'],
  manageTeams: ['teams.manage'],
  liveMap: ['tracking.read', 'tracking.summary'],
  audit: ['audit.read'],
} as const

// Iraq local date (Asia/Baghdad) as YYYY-MM-DD, the server's "today" for visit plans.
export function iraqToday(now: Date = new Date()): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Baghdad' }).format(now)
}

export function errorMessage(error: unknown): string {
  if (!(error instanceof ApiError)) return t('web.error.network')
  switch (error.status) {
    case 400:
      return t('web.error.badData')
    case 403:
      return t('web.error.forbidden')
    case 404:
      return t('web.error.notFound')
    case 409:
      if (error.code === 'CUSTOMER_NOT_APPROVED') return t('web.error.customerNotApproved')
      if (error.code === 'DUPLICATE_NAME') return t('web.error.duplicateName')
      return t('web.error.conflict')
    case 429:
      return t('web.error.tooMany')
    default:
      return t('web.error.server')
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
  // Plan from places (step 1.24): the list first, then the visits for the customers that were chosen.
  previewPlan = (request: PlanRequest) => this.postJson<PlanPreview>('/visit-plan/preview', request)
  generatePlan = (request: PlanRequest) => this.postJson<PlanGenerated>('/visit-plan/generate', request)
  createVisit = (visit: NewVisit) => this.post('/visits', visit)
  reassignVisit = (id: string, assignedUserId: string) => this.send('PUT', `/visits/${id}/assignee`, { assignedUserId })
  cancelVisit = (id: string, reason: string) => this.post(`/visits/${id}/cancel`, { reason })
  // The whole day of one assignee in the wanted order, and moving one visit to another day (step 1.23).
  reorderPlan = (scheduleDate: string, visitIds: string[], assignedUserId: string) =>
    this.send('PUT', '/visit-plan/order', { scheduleDate, visitIds, assignedUserId })
  postponeVisit = (id: string, scheduleDate: string, reason: string) =>
    this.post(`/visits/${id}/postpone`, { scheduleDate, reason: reason || null })
  visitWindows = (customerId: string) => this.get<VisitWindow[]>(`/customers/${customerId}/visit-windows`)
  setVisitWindows = (customerId: string, windows: VisitWindow[]) => this.send('PUT', `/customers/${customerId}/visit-windows`, { windows })
  // Places tree and customer profile (step 1.23-b). parentId null lists the cities.
  places = (parentId: string | null, includeInactive = false, search = '') =>
    this.get<Place[]>(
      `/places?limit=2000${parentId ? `&parentId=${parentId}` : '&kind=city'}${search ? `&q=${encodeURIComponent(search)}` : ''}${includeInactive ? '&includeInactive=true' : ''}`,
    )
  createPlace = (parentId: string | null, kind: PlaceKind, names: PlaceNames) => this.post('/places', { parentId, kind, ...names })
  updatePlace = (id: string, names: PlaceNames, status: 'active' | 'inactive', parentId: string | null = null) =>
    this.send('PUT', `/places/${id}`, { ...names, status, parentId })
  mergePlace = (id: string, intoPlaceId: string) => this.post(`/places/${id}/merge`, { intoPlaceId })
  importPlaces = async (rows: ImportRow[]): Promise<ImportSummary> => {
    const response = await this.api.request('/places/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ rows }) })
    if (!response.ok) throw await toError(response)
    return (await response.json()) as ImportSummary
  }
  // The whole tree as text (CSV or JSON) for other projects.
  exportPlaces = async (format: 'csv' | 'json', includeInactive = false): Promise<string> => {
    const response = await this.api.request(`/places/export?format=${format}${includeInactive ? '&includeInactive=true' : ''}`)
    if (!response.ok) throw await toError(response)
    return response.text()
  }
  setCustomerProfile = (customerId: string, profile: CustomerProfile) => this.send('PUT', `/customers/${customerId}/profile`, profile)
  notifications = (unreadOnly = false) => this.get<Notification[]>(`/notifications?limit=200${unreadOnly ? '&unread=true' : ''}`)
  unreadCount = async () => (await this.get<{ count: number }>('/notifications/unread-count')).count
  markRead = (id: string) => this.post(`/notifications/${id}/read`)
  markAllRead = () => this.post('/notifications/read-all')
  liveMap = () => this.get<LiveMapEntry[]>('/live-map')
  auditLogs = (filter: AuditFilter, after?: AuditEntry) => this.get<AuditEntry[]>(auditQuery(filter, after))
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

  private async postJson<T>(path: string, body: unknown): Promise<T> {
    const response = await this.api.request(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
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
