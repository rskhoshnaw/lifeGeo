import {
  Banknote, Building2, CalendarCheck, ChartColumn, ClipboardList, FlaskConical, House, Layers, ListChecks, Megaphone, Map, MapPinned, Package, Route, ScrollText,
  ShoppingCart, Smartphone, Store, Target, TriangleAlert, Truck, Tv, User, Users, Warehouse, type LucideIcon,
} from 'lucide-react'
import type { TextKey } from '../i18n.ts'

export type NavGroup = 'overview' | 'field' | 'sales' | 'insight' | 'organisation'

export interface NavItem {
  id: string
  label: string
  visible: boolean
}

// Where each screen lives in the menu: the groups follow how people work (look at the day, work in the field, sell and ship, check, set up).
export const placement: Record<string, { group: NavGroup; icon: LucideIcon }> = {
  home: { group: 'overview', icon: House },
  tv: { group: 'overview', icon: Tv },
  account: { group: 'overview', icon: User },
  map: { group: 'field', icon: Map },
  visits: { group: 'field', icon: CalendarCheck },
  route: { group: 'field', icon: Route },
  customers: { group: 'field', icon: Store },
  places: { group: 'field', icon: MapPinned },
  followups: { group: 'field', icon: ListChecks },
  samples: { group: 'field', icon: FlaskConical },
  catalog: { group: 'sales', icon: Package },
  orders: { group: 'sales', icon: ShoppingCart },
  warehouse: { group: 'sales', icon: Warehouse },
  wholesaler: { group: 'sales', icon: Truck },
  marketing: { group: 'sales', icon: Megaphone },
  payments: { group: 'sales', icon: Banknote },
  reports: { group: 'insight', icon: ChartColumn },
  targets: { group: 'insight', icon: Target },
  alerts: { group: 'insight', icon: TriangleAlert },
  audit: { group: 'insight', icon: ScrollText },
  users: { group: 'organisation', icon: Users },
  devices: { group: 'organisation', icon: Smartphone },
  regions: { group: 'organisation', icon: Layers },
  forms: { group: 'organisation', icon: ClipboardList },
  platform: { group: 'organisation', icon: Building2 },
}
export const groups: { id: NavGroup; label: TextKey }[] = [
  { id: 'overview', label: 'web.nav.overview' },
  { id: 'field', label: 'web.nav.field' },
  { id: 'sales', label: 'web.nav.sales' },
  { id: 'insight', label: 'web.nav.insight' },
  { id: 'organisation', label: 'web.nav.organisation' },
]

export function iconFor(id: string): LucideIcon {
  return placement[id]?.icon ?? House
}

