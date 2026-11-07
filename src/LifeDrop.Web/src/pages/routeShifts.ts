import type { RouteHistory, RoutePoint, RouteStop, RouteVisitEvent } from '../api/admin.ts'

// A day can have several shifts (the visitor ended the shift and started again). They are shown together, each line in its own colour, or one by one (plan step 5.24).
export type RouteShift = RouteHistory['shifts'][number]

export const shiftColors = ['#2563eb', '#7c3aed', '#0891b2', '#db2777', '#92400e', '#475569']
export const shiftColor = (index: number) => shiftColors[index % shiftColors.length]

const inRange = (iso: string, shift: RouteShift) => {
  const at = Date.parse(iso)
  return at >= Date.parse(shift.startedAt) && (shift.endedAt === null || at <= Date.parse(shift.endedAt) + 1000)
}

// Points and stops carry their shift; an older server sends none, then the time of the shift decides. A visit event is always placed by time.
const pointIn = (p: RoutePoint, s: RouteShift) => (p.shiftId ? p.shiftId === s.shiftId : inRange(p.capturedAt, s))
const stopIn = (p: RouteStop, s: RouteShift) => (p.shiftId ? p.shiftId === s.shiftId : inRange(p.from, s))
const eventIn = (e: RouteVisitEvent, s: RouteShift) => inRange(e.capturedAt, s)

export interface RouteView {
  points: RoutePoint[]
  stops: RouteStop[]
  events: RouteVisitEvent[]
  groups: { id: string; label: number; color: string; points: RoutePoint[] }[]
}

// selected: the id of one shift, or '' for all of them together. The groups are the lines to draw, one per shift with points.
export function routeView(route: RouteHistory, selected: string): RouteView {
  const chosen = route.shifts.map((shift, i) => ({ shift, number: i + 1, color: shiftColor(i) })).filter((c) => !selected || c.shift.shiftId === selected)
  if (route.shifts.length <= 1 && !selected) {
    return { points: route.points, stops: route.stops, events: route.events, groups: [{ id: route.shifts[0]?.shiftId ?? 'day', label: 1, color: shiftColor(0), points: route.points }] }
  }
  const groups = chosen.map((c) => ({ id: c.shift.shiftId, label: c.number, color: c.color, points: route.points.filter((p) => pointIn(p, c.shift)) })).filter((g) => g.points.length > 0)
  return {
    points: groups.flatMap((g) => g.points),
    stops: route.stops.filter((s) => chosen.some((c) => stopIn(s, c.shift))),
    events: route.events.filter((e) => chosen.some((c) => eventIn(e, c.shift))),
    groups,
  }
}
