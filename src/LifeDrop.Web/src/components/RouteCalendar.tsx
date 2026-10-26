import { t, type TextKey } from '../i18n.ts'
import type { RouteDay } from '../api/admin.ts'

// The month of the route history (plan step 5.4): the days with a route are coloured, darker with more points; a day with a shift but no points left is lightly marked.
// The week starts on Saturday, as in Iraq. Days after today cannot be chosen.
const weekdays = [6, 0, 1, 2, 3, 4, 5]
const monthName = new Intl.DateTimeFormat('en-GB', { timeZone: 'UTC', month: 'long', year: 'numeric' })

export function dayLevel(day: RouteDay | undefined): 0 | 1 | 2 | 3 | 4 {
  if (!day) return 0
  if (day.points <= 0) return 1
  if (day.points < 60) return 2
  if (day.points < 180) return 3
  return 4
}

const levelClass = ['', 'bg-amber-100', 'bg-brand-100', 'bg-brand-300', 'bg-brand-600 text-white']

export function RouteCalendar({ month, days, selected, today, onPick, onMonth }: { month: string; days: RouteDay[]; selected: string; today: string; onPick: (date: string) => void; onMonth: (month: string) => void }) {
  const [year, monthNumber] = month.split('-').map(Number)
  const first = new Date(Date.UTC(year, monthNumber - 1, 1))
  const length = new Date(Date.UTC(year, monthNumber, 0)).getUTCDate()
  const offset = (first.getUTCDay() + 1) % 7   // Saturday = 0
  const byDate = new Map(days.map((d) => [d.date, d]))
  const shift = (by: number) => {
    const moved = new Date(Date.UTC(year, monthNumber - 1 + by, 1))
    return `${moved.getUTCFullYear()}-${String(moved.getUTCMonth() + 1).padStart(2, '0')}-01`
  }
  const currentMonth = `${today.slice(0, 7)}-01`
  const cells: (number | null)[] = [...Array(offset).fill(null), ...Array.from({ length }, (_, i) => i + 1)]
  return (
    <div className="w-72 rounded border p-2 text-sm">
      <div className="mb-1 flex items-center justify-between">
        <button type="button" className="rounded border px-2" aria-label={t('web.route.prevMonth')} onClick={() => onMonth(shift(-1))}>
          ‹
        </button>
        <strong>{monthName.format(first)}</strong>
        <button type="button" className="rounded border px-2" aria-label={t('web.route.nextMonth')} disabled={month >= currentMonth} onClick={() => onMonth(shift(1))}>
          ›
        </button>
      </div>
      <div className="grid grid-cols-7 gap-1 text-center">
        {weekdays.map((d) => (
          <span key={d} className="text-xs text-slate-500">
            {t(`web.weekday.${d}` as TextKey).slice(0, 3)}
          </span>
        ))}
        {cells.map((day, i) => {
          if (day === null) return <span key={`blank-${i}`} />
          const date = `${month.slice(0, 7)}-${String(day).padStart(2, '0')}`
          const level = dayLevel(byDate.get(date))
          return (
            <button
              key={date}
              type="button"
              aria-label={date}
              data-level={level}
              disabled={date > today}
              onClick={() => onPick(date)}
              className={`rounded py-1 disabled:opacity-30 ${levelClass[level]} ${date === selected ? 'ring-2 ring-slate-900' : ''}`}
            >
              {day}
            </button>
          )
        })}
      </div>
      <p className="mt-1 text-xs text-slate-500">{t('web.route.legend')}</p>
    </div>
  )
}
