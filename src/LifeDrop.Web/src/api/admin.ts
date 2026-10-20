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
  isActive: boolean
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
  // Priority a manager gave the customer (step 1.26).
  priority: VisitPriority
  // Step 4.15: the person to talk to, a second phone and a note.
  contactName?: string | null
  phone2?: string | null
  notes?: string | null
  clinicName?: string | null
  specialty?: string | null
  cooperates?: boolean | null
  dealer?: boolean | null
  customerClass?: string | null
}

// A message for the signed-in user (plan step 1.23-c): somebody other than the owner changed a customer of the user's area.
export interface Notification {
  id: string
  // manager.message (step 4.16): entityId is the text the manager wrote.
  kind: 'customer.profile_changed' | 'customer.location_changed' | 'manager.message'
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

export interface CustomerDoctor {
  clinicName: string | null
  specialty: string | null
  cooperates: boolean | null
  dealer: boolean | null
  customerClass: string | null
}

export const doctorSpecialties = ['orthopedics', 'neuro_spine', 'pediatrics', 'women_obstetrics', 'surgery', 'kidney_urology', 'ent', 'other'] as const
export const doctorClasses = ['A+', 'A', 'B', 'C'] as const
// Customer types the panel and the app offer; a customer may carry another text (older data), which the editor keeps.
export const customerTypes = ['Pharmacy', 'MedicalAssistant', 'Doctor', 'Wholesaler', 'Other'] as const

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
  // The customer's priority from a manager (step 1.26), not the visit's own.
  customerPriority: VisitPriority
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

export interface PriorityRequest {
  priority: VisitPriority
  customerIds?: string[]
  placeId?: string
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
  customerPriority: VisitPriority
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

export interface Product {
  id: string
  code: string
  name: string
  nameCkb: string | null
  nameAr: string | null
  nameEn: string | null
  unit: string
  currency: 'IQD' | 'USD'
  basePrice: number
  status: 'active' | 'inactive'
}
export interface ProductInput {
  code?: string
  name: string
  nameCkb: string | null
  nameAr: string | null
  nameEn: string | null
  unit: string
  currency: 'IQD' | 'USD'
  basePrice: number
  status: 'active' | 'inactive'
}
export interface CityPrice {
  id: string
  productId: string
  cityId: string
  cityName: string
  price: number
  validFrom: string
  validTo: string | null
}
export interface CustomerDiscount {
  id: string
  customerId: string
  productId: string | null
  productCode: string | null
  kind: 'percent' | 'amount'
  value: number
  validFrom: string
  validTo: string | null
}
export interface EffectivePrice {
  productId: string
  code: string
  name: string
  unit: string
  currency: 'IQD' | 'USD'
  listPrice: number
  listSource: 'base' | 'city'
  discounts: { id: string; kind: 'percent' | 'amount'; value: number; productId: string | null }[]
  price: number
}

export interface Warehouse {
  id: string
  code: string
  name: string
  status: 'active' | 'inactive'
}
export interface OrderLine {
  lineNo: number
  productId: string
  productCode: string
  productName: string
  unit: string
  quantity: number
  listPrice: number
  unitPrice: number
  lineTotal: number
  discounts: { id: string; kind: 'percent' | 'amount'; value: number }[]
  preparedQuantity?: number | null
  shortageReason?: string | null
  deliveredQuantity?: number | null
  deliveryReason?: string | null
}
export interface OrderStep {
  status: string
  actorEmail: string | null
  note: string | null
  at: string
  distanceMeters?: number | null
}
export interface DeliveryInfo {
  receiverName: string | null
  deliveryNote: string | null
  failureReason: string | null
}
export interface StaffMember {
  userId: string
  email: string | null
}
export interface Driver {
  id: string
  email: string | null
}
export interface PaymentStep {
  status: string
  actorEmail: string | null
  note: string | null
  at: string
}
export interface Payment {
  id: string
  number: string
  customerId: string
  customerCode: string
  customerName: string
  orderId: string | null
  orderNumber: string | null
  method: 'cash' | 'fastpay' | 'fib' | 'other'
  currency: 'IQD' | 'USD'
  amount: number
  reference: string | null
  note: string | null
  status: 'collected' | 'confirmed' | 'voided' | 'superseded'
  receivedByEmail: string | null
  confirmedByEmail: string | null
  closedReason: string | null
  createdAt: string
  history?: PaymentStep[] | null
}
export interface NewPaymentInput {
  customerId: string
  orderId: string | null
  method: string
  currency: string
  amount: number
  reference: string | null
  note: string | null
  clientRef: string
}
export interface BalanceItem {
  currency: string
  delivered: number
  confirmed: number
  collected: number
  balance: number
}
export interface CustomerBalance {
  customerId: string
  items: BalanceItem[]
}
export interface VisitDetails {
  visitId: string
  visitType: string
  purpose: string
  personMet: string | null
  personRole: string | null
  needs: string | null
  products: { id: string; code: string; name: string }[]
  specialty: string | null
  dailyPatients: number | null
  monthlySales: number | null
  competitors: string | null
  monthlyVolume: number | null
  paymentTerms: string | null
  currency: string | null
  updatedAt: string
}
export interface FollowUp {
  id: string
  customerId: string
  customerCode: string
  customerName: string
  subject: string
  dueDate: string
  assigneeId: string
  assigneeEmail: string | null
  createdBy: string
  createdByEmail: string | null
  status: 'pending' | 'in_progress' | 'completed' | 'cancelled'
  isOverdue: boolean
  resultNote: string | null
  cancelReason: string | null
  createdAt: string
  completedAt: string | null
}
export interface Alert {
  id: string
  kind: 'followup_overdue' | 'visit_delayed' | 'visit_long_stay' | 'tracking_gap' | 'new_device' | 'gps_mocked' | 'impossible_speed' | 'clock_tamper' | 'outside_area' | 'poor_accuracy' | 'device_mismatch' | 'impossible_travel' | 'short_stay' | 'low_coverage'
  category: 'operational' | 'security'
  severity: 'info' | 'warning' | 'critical'
  subjectEmail: string | null
  customerCode: string | null
  customerName: string | null
  detail: string | null
  status: 'new' | 'seen' | 'handled'
  occurredAt: string
  handledByEmail: string | null
  handledNote: string | null
  evidence?: string | null
  handledOutcome?: 'valid' | 'suspicious' | 'fraud' | null
}
// Security alerts about behaviour: handling them needs the result of the review (step 2.10).
export const reviewedAlertKinds = ['gps_mocked', 'impossible_speed', 'clock_tamper', 'outside_area', 'poor_accuracy', 'device_mismatch', 'impossible_travel', 'short_stay']
// Route history of one user on one day (step 2.11, decision D6): points in time order, stops (5 minutes within 50 m), check-ins/outs and shifts.
export interface RoutePoint {
  latitude: number
  longitude: number
  accuracyMeters: number
  capturedAt: string
  isMocked: boolean
}
export interface RouteStop {
  from: string
  to: string
  seconds: number
  latitude: number
  longitude: number
  customerName: string | null
  customerDistanceMeters: number | null
}
export interface RouteVisitEvent {
  kind: 'check_in' | 'check_out'
  accepted: boolean
  rejectionReason: string | null
  capturedAt: string
  latitude: number
  longitude: number
  customerName: string | null
  radiusMeters: number | null
  isMocked: boolean
}
export interface RouteHistory {
  userId: string
  email: string | null
  date: string
  shifts: { shiftId: string; startedAt: string; endedAt: string | null; pointCount: number; pointsPurged: boolean }[]
  points: RoutePoint[]
  stops: RouteStop[]
  events: RouteVisitEvent[]
  truncated: boolean
}
export interface TrackableUser {
  id: string
  email: string | null
  teamName: string | null
}
// A located customer of the reader's area for the map: clustered, with the check-in circle (radius in metres).
export interface MapCustomer {
  id: string
  code: string
  name: string
  type: string
  latitude: number
  longitude: number
  geofenceRadiusMeters: number
}
// KPI report (step 2.12): counts per user and in total; a rate is null when its denominator is 0. docs/KPI.md has every definition.
export interface KpiCounts {
  visitsPlanned: number
  visitsCompleted: number
  visitsFailed: number
  delayBase: number
  delayedVisits: number
  newCustomers: number
  approvedNewCustomers: number
  orders: number
  ordersDelivered: number
  ordersDeliveryFailed: number
  orderTotals: { currency: string; total: number }[]
  followUpBase: number
  followUpsOverdue: number
}
export interface KpiRates {
  visitCompletion: number | null
  visitFailure: number | null
  delay: number | null
  orderDelivery: number | null
  followUpOverdue: number | null
}
export interface KpiRow {
  userId: string
  email: string | null
  teamName: string | null
  counts: KpiCounts
  rates: KpiRates
}
export interface KpiReport {
  from: string
  to: string
  total: KpiRow
  users: KpiRow[]
  alerts: { kind: string; category: string; total: number; handled: number; valid: number; suspicious: number; fraud: number }[]
}
export interface Sample {
  id: string
  number: string
  customerCode: string
  customerName: string
  productCode: string
  productName: string
  unit: string
  quantity: number
  recipientName: string | null
  batchNumber: string | null
  expiryDate: string | null
  note: string | null
  distributedByEmail: string | null
  status: 'given' | 'voided'
  voidReason: string | null
  createdAt: string
}
export interface Order {
  id: string
  number: string
  customerId: string
  customerCode: string
  customerName: string
  visitId: string | null
  warehouseId: string
  warehouseName: string
  createdBy: string
  createdByEmail: string | null
  status: 'placed' | 'preparing' | 'ready' | 'handed_over' | 'picked_up' | 'on_the_way' | 'delivered' | 'delivery_failed' | 'cancelled'
  currency: 'IQD' | 'USD'
  total: number
  note: string | null
  cancelReason: string | null
  createdAt: string
  items: OrderLine[]
  driverId?: string | null
  driverEmail?: string | null
  history?: OrderStep[] | null
  delivery?: DeliveryInfo | null
}
export interface CreatedOrder {
  id: string
  number: string
  currency: 'IQD' | 'USD'
  total: number
  replayed: boolean
}
export interface NewOrderInput {
  customerId: string
  warehouseId: string
  visitId?: string | null
  clientRef: string
  note: string | null
  items: { productId: string; quantity: number }[]
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

// Custom visit-form fields (step 3.2, part 2): forms.manage defines them.
export interface FormField {
  id: string
  key: string
  type: 'text' | 'number' | 'choice' | 'yes_no' | 'date'
  appliesTo: 'all' | 'pharmacy' | 'doctor' | 'wholesaler' | 'other'
  labelEn: string | null
  labelCkb: string | null
  labelAr: string | null
  labelFa: string | null
  options: string[]
  isRequired: boolean
  sortOrder: number
  isActive: boolean
}
export interface NewFormField {
  key: string
  type: FormField['type']
  appliesTo: FormField['appliesTo']
  labelEn: string
  labelCkb: string
  labelAr: string
  labelFa: string
  options: string[]
  isRequired: boolean
  sortOrder: number
}

// Saved and scheduled reports (step 3.2, part 1).
export interface ReportSchedule {
  frequency: 'daily' | 'weekly' | 'monthly'
  weekday: number | null
  dayOfMonth: number | null
  hour: number
  isActive: boolean
  nextRunAt: string | null
  lastRunAt: string | null
}
export interface SavedReport {
  id: string
  name: string
  range: string
  from: string | null
  to: string | null
  regionId: string | null
  teamId: string | null
  customerType: string | null
  schedule: ReportSchedule | null
  createdAt: string
}
export interface SaveReport {
  name: string
  range: string
  from?: string
  to?: string
  regionId?: string
  teamId?: string
  customerType?: string
  schedule?: { frequency: string; weekday?: number; dayOfMonth?: number; hour: number; isActive: boolean }
}
export interface ReportRun {
  id: string
  reportId: string
  trigger: 'manual' | 'schedule'
  status: 'ok' | 'failed'
  errorCode: string | null
  from: string
  to: string
  ranAt: string
}
export interface ReportRunDetail {
  run: ReportRun
  report: KpiReport | null
}

// Platform (step 3.1): the Super Admin of the platform company.
export interface Tenant {
  id: string
  code: string
  name: string
  isActive: boolean
  users: number
  activeUsers: number
}
export interface TenantAuditEntry {
  id: string
  actorId: string
  actorEmail: string | null
  action: string
  entityId: string
  occurredAt: string
}
export interface PlatformAuditEntry {
  id: string
  actorId: string
  actorEmail: string | null
  action: string
  targetTenantId: string | null
  targetTenantCode: string | null
  reason: string | null
  detail: string | null
  occurredAt: string
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
  catalog: ['catalog.read'],
  orders: ['orders.read', 'orders.create'],
  payments: ['payments.read', 'payments.record'],
  recordPayments: ['payments.record'],
  samples: ['samples.read', 'samples.record'],
  followUps: ['followups.read', 'followups.create'],
  alerts: ['alerts.read', 'alerts.security.read'],
  handleAlerts: ['alerts.handle'],
  createFollowUps: ['followups.create'],
  manageFollowUps: ['followups.manage'],
  manageSamples: ['samples.manage'],
  recordSamples: ['samples.record'],
  sendMessages: ['notifications.send'],
  managePayments: ['payments.manage'],
  createOrders: ['orders.create'],
  cancelOrders: ['orders.cancel'],
  manageWarehouses: ['warehouses.manage'],
  warehouseWork: ['warehouse.work'],
  manageCatalog: ['catalog.manage'],
  approveCustomers: ['customers.approve'],
  planVisits: ['visits.write'],
  cancelVisits: ['visits.cancel'],
  regions: ['regions.read'],
  manageRegions: ['regions.manage'],
  readTeams: ['teams.read'],
  manageTeams: ['teams.manage'],
  liveMap: ['tracking.read', 'tracking.summary'],
  routeHistory: ['tracking.read'],
  reports: ['reports.read'],
  forms: ['forms.manage'],
  platform: ['platform.tenants.manage'],
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

export interface ChannelStatus {
  enabled: boolean
  botUsername: string | null
  telegramLinked: boolean
}
export interface TelegramLink {
  code: string
  botUsername: string | null
  expiresAt: string
}

export class AdminApi {
  private readonly api: ApiClient
  constructor(api: ApiClient) {
    this.api = api
  }

  me = () => this.get<Me>('/me')
  users = (includeInactive = false) => this.get<UserSummary[]>(includeInactive ? '/users?includeInactive=true' : '/users')
  roles = () => this.get<Role[]>('/roles')
  devices = (status?: DeviceStatus) => this.get<Device[]>(status ? `/devices?status=${status}` : '/devices')
  approveDevice = (id: string) => this.post(`/devices/${id}/approve`)
  blockDevice = (id: string) => this.post(`/devices/${id}/block`)
  killSessions = (userId: string) => this.post(`/users/${userId}/sessions/revoke`)
  assignRole = (userId: string, roleId: string) => this.post(`/users/${userId}/roles/${roleId}`)
  createUser = (user: { email: string; password: string; roleId: string }) => this.post('/users', user)
  changeUserEmail = (userId: string, email: string) => this.send('PUT', `/users/${userId}/email`, { email })
  setUserPassword = (userId: string, password: string) => this.send('PUT', `/users/${userId}/password`, { password })
  deactivateUser = (userId: string) => this.post(`/users/${userId}/deactivate`)
  activateUser = (userId: string) => this.post(`/users/${userId}/activate`)
  customers = (after?: Customer, archived = false, search = '') =>
    this.get<Customer[]>(
      `/customers?limit=100${archived ? '&archived=true' : ''}${search ? `&q=${encodeURIComponent(search)}` : ''}` +
        (after ? `&cursorCreatedAt=${encodeURIComponent(after.createdAt)}&cursorId=${after.id}` : ''),
    )
  setCustomerDoctor = (id: string, doctor: CustomerDoctor) => this.send('PUT', `/customers/${id}/doctor`, doctor)
  setCustomerContact = (id: string, contact: { contactName: string | null; phone2: string | null; notes: string | null }) => this.send('PUT', `/customers/${id}/contact`, contact)
  editCustomer = (id: string, details: { type: string; code: string; name: string }) => this.send('PUT', `/customers/${id}`, details)
  archiveCustomer = (id: string) => this.post(`/customers/${id}/archive`)
  restoreCustomer = (id: string) => this.post(`/customers/${id}/restore`)
  customerApprovals = () => this.get<Customer[]>('/customer-approvals')
  decideCustomer = (id: string, decision: 'approve' | 'reject', note: string) =>
    this.post(`/customers/${id}/approval`, { decision, note: note || null })

  visitPlan = (date: string, assignedUserId?: string) =>
    this.get<PlannedVisit[]>(`/visit-plan?scheduleDate=${date}${assignedUserId ? `&assignedUserId=${assignedUserId}` : ''}`)
  // Plan from places (step 1.24): the list first, then the visits for the customers that were chosen.
  previewPlan = (request: PlanRequest) => this.postJson<PlanPreview>('/visit-plan/preview', request)
  generatePlan = (request: PlanRequest) => this.postJson<PlanGenerated>('/visit-plan/generate', request)
  // Priority a manager gives customers: by ids or every customer under a place (step 1.26). Returns how many changed.
  setCustomerPriority = async (request: PriorityRequest): Promise<{ updated: number }> => {
    const response = await this.api.request('/customers/priority', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) })
    if (!response.ok) throw await toError(response)
    return (await response.json()) as { updated: number }
  }
  // Products and prices (step 2.1).
  products = (includeInactive: boolean, search = '') =>
    this.get<Product[]>(`/products?${includeInactive ? 'includeInactive=true&' : ''}${search ? `q=${encodeURIComponent(search)}` : ''}`)
  createProduct = (product: ProductInput) => this.post('/products', product)
  updateProduct = (id: string, product: ProductInput) => this.send('PUT', `/products/${id}`, product)
  cityPrices = (productId: string) => this.get<CityPrice[]>(`/products/${productId}/city-prices`)
  createCityPrice = (productId: string, rule: { cityId: string; price: number; validFrom: string | null; validTo: string | null }) =>
    this.post(`/products/${productId}/city-prices`, rule)
  endCityPrice = (id: string, validTo: string) => this.post(`/city-prices/${id}/end`, { validTo })
  discounts = (customerId: string) => this.get<CustomerDiscount[]>(`/customers/${customerId}/discounts`)
  createDiscount = (customerId: string, discount: { productId: string | null; kind: 'percent' | 'amount'; value: number; validFrom: string | null; validTo: string | null }) =>
    this.post(`/customers/${customerId}/discounts`, discount)
  endDiscount = (id: string, validTo: string) => this.post(`/discounts/${id}/end`, { validTo })
  customerPrices = (customerId: string, date: string) => this.get<EffectivePrice[]>(`/customers/${customerId}/prices?date=${date}`)
  // Warehouses and orders (step 2.2). The server prices every order line; no price is ever sent.
  warehouses = (includeInactive = false) => this.get<Warehouse[]>(`/warehouses${includeInactive ? '?includeInactive=true' : ''}`)
  createWarehouse = (code: string, name: string) => this.post('/warehouses', { code, name })
  updateWarehouse = (id: string, name: string, status: 'active' | 'inactive') => this.send('PUT', `/warehouses/${id}`, { name, status })
  orders = (before?: Order) =>
    this.get<Order[]>(before ? `/orders?limit=50&before=${encodeURIComponent(before.createdAt)}&beforeId=${before.id}` : '/orders?limit=50')
  order = (id: string) => this.get<Order>(`/orders/${id}`)
  createOrder = (order: NewOrderInput) => this.postJson<CreatedOrder>('/orders', order)
  cancelOrder = (id: string, reason: string) => this.post(`/orders/${id}/cancel`, { reason })
  // Payments (step 2.5): a visitor's payment is "collected" until the finance manager confirms it.
  payments = (status: string, before?: Payment) =>
    this.get<Payment[]>(`/payments?limit=50${status ? `&status=${status}` : ''}${before ? `&before=${encodeURIComponent(before.createdAt)}&beforeId=${before.id}` : ''}`)
  payment = (id: string) => this.get<Payment>(`/payments/${id}`)
  recordPayment = (payment: NewPaymentInput) => this.postJson<{ id: string; number: string; status: string; replayed: boolean }>('/payments', payment)
  confirmPayment = (id: string) => this.post(`/payments/${id}/confirm`)
  voidPayment = (id: string, reason: string) => this.post(`/payments/${id}/void`, { reason })
  correctPayment = (id: string, fix: { orderId: string | null; method: string; currency: string; amount: number; reference: string | null; note: string | null; reason: string }) =>
    this.post(`/payments/${id}/correct`, fix)
  customerBalance = (customerId: string) => this.get<CustomerBalance>(`/customers/${customerId}/balance`)
  // The specialised visit form of a visit (step 2.7); 404 when the rep has not filled one.
  visitDetails = (visitId: string) => this.get<VisitDetails>(`/visits/${visitId}/report/details`)
  // Follow-ups (step 2.8): a reminder for a customer with a due day and one responsible user.
  followUps = (status: string, overdueOnly: boolean) =>
    this.get<FollowUp[]>(`/followups?limit=200${status ? `&status=${status}` : ''}${overdueOnly ? '&overdue=true' : ''}`)
  createFollowUp = (followUp: { customerId: string; subject: string; dueDate: string; assigneeId: string | null; clientRef: string }) => this.post('/followups', followUp)
  editFollowUp = (id: string, subject: string, dueDate: string) => this.send('PUT', `/followups/${id}`, { subject, dueDate })
  cancelFollowUp = (id: string, reason: string) => this.post(`/followups/${id}/cancel`, { reason })
  reassignFollowUp = (id: string, assigneeId: string) => this.post(`/followups/${id}/reassign`, { assigneeId })
  // Alerts (step 2.9): operational and security alerts; new -> seen -> handled with a note. scan() looks for new ones now.
  alerts = (category: string, status: string) => this.get<Alert[]>(`/alerts?limit=200${category ? `&category=${category}` : ''}${status ? `&status=${status}` : ''}`)
  alertCount = async () => (await this.get<{ new: number }>('/alerts/count')).new
  seeAlert = (id: string) => this.post(`/alerts/${id}/seen`)
  handleAlert = (id: string, note: string, outcome: string | null = null) => this.post(`/alerts/${id}/handle`, { note, outcome })
  scanAlerts = () => this.postJson<{ created: number }>('/alerts/scan', {})
  // Pharma samples (step 2.6): reps record them in the app; managers read and void with a reason.
  samples = (before?: Sample) =>
    this.get<Sample[]>(before ? `/samples?limit=50&before=${encodeURIComponent(before.createdAt)}&beforeId=${before.id}` : '/samples?limit=50')
  recordSample = (sample: { customerId: string; productId: string; quantity: number; recipientName: string | null; batchNumber: string | null; expiryDate: string | null; note: string | null; clientRef: string }) =>
    this.post('/samples', sample)
  voidSample = (id: string, reason: string) => this.post(`/samples/${id}/void`, { reason })
  // Warehouse staff and warehouse work (step 2.3).
  warehouseStaff = (warehouseId: string) => this.get<StaffMember[]>(`/warehouses/${warehouseId}/staff`)
  addWarehouseStaff = (warehouseId: string, userId: string) => this.post(`/warehouses/${warehouseId}/staff`, { userId })
  removeWarehouseStaff = (warehouseId: string, userId: string) => this.send('DELETE', `/warehouses/${warehouseId}/staff/${userId}`)
  warehouseOrders = (status: string, before?: Order) =>
    this.get<Order[]>(`/warehouse/orders?limit=50${status ? `&status=${status}` : ''}${before ? `&before=${encodeURIComponent(before.createdAt)}&beforeId=${before.id}` : ''}`)
  warehouseOrder = (id: string) => this.get<Order>(`/warehouse/orders/${id}`)
  startPreparing = (id: string) => this.post(`/warehouse/orders/${id}/start`)
  markReady = (id: string, lines: { lineNo: number; quantity: number; reason?: string | null }[]) => this.post(`/warehouse/orders/${id}/ready`, { lines })
  handOver = (id: string, driverId: string) => this.post(`/warehouse/orders/${id}/handover`, { driverId })
  drivers = () => this.get<Driver[]>('/warehouse/drivers')
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
  // A manager's message to staff (step 4.16): the app push and Telegram deliver it. Returns how many people it reached.
  sendMessage = (message: { message: string; userIds?: string[]; role?: string; regionId?: string; teamId?: string; everyone?: boolean }) =>
    this.postJson<{ recipients: number }>('/notifications/send', message)
  channels = () => this.get<ChannelStatus>('/channels')
  startTelegramLink = () => this.postJson<TelegramLink>('/channels/telegram/link', {})
  unlinkTelegram = () => this.post('/channels/telegram/unlink')
  unreadCount = async () => (await this.get<{ count: number }>('/notifications/unread-count')).count
  markRead = (id: string) => this.post(`/notifications/${id}/read`)
  markAllRead = () => this.post('/notifications/read-all')
  liveMap = () => this.get<LiveMapEntry[]>('/live-map')
  // KPI report (step 2.12): reports.read, only the users inside the reader's area.
  kpis = (from: string, to: string, regionId: string, teamId: string, customerType: string) =>
    this.get<KpiReport>(`/reports/kpis?from=${from}&to=${to}${regionId ? `&regionId=${regionId}` : ''}${teamId ? `&teamId=${teamId}` : ''}${customerType ? `&customerType=${encodeURIComponent(customerType)}` : ''}`)
  // Saved and scheduled reports (step 3.2, part 1): reports.read, only the caller's own.
  savedReports = () => this.get<SavedReport[]>('/saved-reports')
  saveReport = (report: SaveReport) => this.post('/saved-reports', report)
  deleteSavedReport = (id: string) => this.send('DELETE', `/saved-reports/${id}`)
  updateSavedReport = (id: string, report: SaveReport) => this.send('PUT', `/saved-reports/${id}`, report)
  runSavedReport = (id: string) => this.postJson<ReportRunDetail>(`/saved-reports/${id}/run`, {})
  savedReportRuns = (id: string) => this.get<ReportRun[]>(`/saved-reports/${id}/runs?limit=10`)
  savedReportRun = (id: string, runId: string) => this.get<ReportRunDetail>(`/saved-reports/${id}/runs/${runId}`)
  // Custom visit-form fields (step 3.2, part 2): forms.manage.
  formFields = (includeInactive: boolean) => this.get<FormField[]>(`/form-fields${includeInactive ? '?includeInactive=true' : ''}`)
  createFormField = (field: NewFormField) => this.post('/form-fields', field)
  updateFormField = (id: string, change: { isRequired?: boolean; isActive?: boolean }) => this.send('PUT', `/form-fields/${id}`, change)
  // Platform (step 3.1): only users of the platform company with platform.tenants.manage / access.
  platformTenants = () => this.get<Tenant[]>('/platform/tenants')
  createTenant = (tenant: { code: string; name: string; managerEmail: string; managerPassword: string }) => this.post('/platform/tenants', tenant)
  updateTenant = (id: string, change: { name?: string; isActive?: boolean; reason: string }) => this.send('PUT', `/platform/tenants/${id}`, change)
  tenantAudit = (id: string, reason: string, action: string) =>
    this.get<TenantAuditEntry[]>(`/platform/tenants/${id}/audit-logs?reason=${encodeURIComponent(reason)}${action ? `&action=${encodeURIComponent(action)}` : ''}`)
  platformAudit = () => this.get<PlatformAuditEntry[]>('/platform/audit-logs')
  // Route history (step 2.11): needs tracking.read; every read is audited by the server.
  trackingUsers = () => this.get<TrackableUser[]>('/tracking/users')
  routeHistory = (userId: string, date: string) => this.get<RouteHistory>(`/tracking/history?userId=${userId}&date=${date}`)
  mapCustomers = () => this.get<MapCustomer[]>('/customers/map')
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
