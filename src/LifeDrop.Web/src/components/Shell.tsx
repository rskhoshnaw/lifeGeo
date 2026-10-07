import { useEffect, useState, type ReactNode } from 'react'
import { Bell, LogOut, Menu, X } from 'lucide-react'
import { LanguagePicker } from './I18n.tsx'
import { t } from '../i18n.ts'

import { groups, placement, type NavItem } from './nav.ts'

// The drop with a position dot: the one mark of the product (a delivery and a place).
export function Logo({ className = 'size-7' }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" className={className} aria-hidden="true">
      <path d="M12 2.2c3.7 4.3 6.2 7.3 6.2 10.6a6.2 6.2 0 0 1-12.4 0c0-3.3 2.5-6.3 6.2-10.6Z" fill="currentColor" />
      <circle cx="12" cy="13" r="2.5" fill="var(--color-slate-50)" />
    </svg>
  )
}

// The frame of the signed-in panel: a grouped menu on the side (a drawer on phones), a quiet top bar with notifications, language and the user, and the page.
export function Shell({ items, current, onSelect, notifications, email, company, onSignOut, children }: {
  items: NavItem[]
  current: string
  onSelect: (id: string) => void
  notifications: { id: string; label: string; count: number }
  email: string
  company: string
  onSignOut: () => void
  children: ReactNode
}) {
  const [open, setOpen] = useState(false)
  const visible = items.filter((i) => i.visible)

  // The drawer closes with Escape.
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  const choose = (id: string) => {
    onSelect(id)
    setOpen(false)
  }

  return (
    <div className="min-h-screen lg:grid lg:grid-cols-[15.5rem_minmax(0,1fr)]">
      {open && <div className="fixed inset-0 z-30 bg-slate-900/40 lg:hidden" onClick={() => setOpen(false)} aria-hidden="true" />}
      <aside
        className={`fixed inset-y-0 start-0 z-40 flex w-[15.5rem] flex-col border-e bg-white transition-transform duration-200 lg:sticky lg:top-0 lg:h-screen ${
          open ? 'translate-x-0' : 'max-lg:ltr:-translate-x-full max-lg:rtl:translate-x-full'
        }`}
      >
        <div className="flex h-14 shrink-0 items-center gap-2.5 border-b px-4 text-brand-700">
          <Logo />
          <span className="text-[1.05rem] font-bold tracking-tight text-slate-900">LifeDrop</span>
          <button className="ms-auto rounded p-1 text-slate-500 hover:bg-slate-100 lg:hidden" aria-label={t('web.nav.close')} onClick={() => setOpen(false)}>
            <X className="size-5" aria-hidden="true" />
          </button>
        </div>
        <nav className="min-h-0 flex-1 space-y-5 overflow-y-auto px-3 py-4" aria-label="LifeDrop">
          {groups.map((g) => {
            const list = visible.filter((i) => placement[i.id]?.group === g.id)
            if (list.length === 0) return null
            return (
              <div key={g.id}>
                <p className="eyebrow mb-1.5 px-2.5">{t(g.label)}</p>
                <ul className="space-y-0.5">
                  {list.map((i) => {
                    const Icon = placement[i.id]!.icon
                    return (
                      <li key={i.id}>
                        <button className="nav-item" aria-current={i.id === current ? 'page' : undefined} onClick={() => choose(i.id)}>
                          <Icon className="size-[1.1rem] shrink-0" strokeWidth={1.75} aria-hidden="true" />
                          <span className="min-w-0 truncate">{i.label}</span>
                        </button>
                      </li>
                    )
                  })}
                </ul>
              </div>
            )
          })}
        </nav>
        <div className="shrink-0 border-t px-4 py-3 text-xs text-slate-500">
          <p className="truncate font-medium text-slate-700">{email}</p>
          <p className="truncate">{company}</p>
        </div>
      </aside>

      <div className="min-w-0">
        <header className="sticky top-0 z-20 flex h-14 items-center gap-2 border-b bg-slate-50/90 px-4 backdrop-blur lg:px-6">
          <button className="rounded p-1.5 text-slate-700 hover:bg-slate-100 lg:hidden" aria-label={t('web.nav.open')} onClick={() => setOpen(true)}>
            <Menu className="size-5" aria-hidden="true" />
          </button>
          <div className="ms-auto flex items-center gap-2">
            <button
              className={`relative rounded-md p-2 hover:bg-slate-100 ${current === notifications.id ? 'bg-brand-50 text-brand-800' : 'text-slate-600'}`}
              aria-label={notifications.label}
              aria-current={current === notifications.id ? 'page' : undefined}
              onClick={() => choose(notifications.id)}
            >
              <Bell className="size-5" strokeWidth={1.75} aria-hidden="true" />
              {notifications.count > 0 && (
                <span aria-hidden="true" className="absolute end-0.5 top-0.5 grid min-w-4 place-items-center rounded-full bg-clay-500 px-1 text-[0.65rem] font-bold leading-4 text-white">
                  {notifications.count > 99 ? '99+' : notifications.count}
                </span>
              )}
            </button>
            <LanguagePicker />
            <button className="flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-sm text-slate-700 hover:bg-slate-100" aria-label={t('web.signOut')} onClick={onSignOut}>
              <LogOut className="size-[1.05rem]" strokeWidth={1.75} aria-hidden="true" />
              <span className="hidden sm:inline">{t('web.signOut')}</span>
            </button>
          </div>
        </header>
        <main>{children}</main>
      </div>
    </div>
  )
}
