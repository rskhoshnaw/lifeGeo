import type { ReactNode } from 'react'

export type Tone = 'neutral' | 'good' | 'warn' | 'bad' | 'info'

const tones: Record<Tone, string> = {
  neutral: 'bg-slate-100 text-slate-700',
  good: 'bg-green-100 text-green-800',
  warn: 'bg-amber-100 text-amber-800',
  bad: 'bg-red-100 text-red-700',
  info: 'bg-slate-200 text-slate-800',
}

/** A quiet status label: soft tint, a small dot, no outline. The text always carries the meaning; colour only helps scanning. */
export function Badge({ tone = 'neutral', children }: { tone?: Tone; children: ReactNode }) {
  return (
    <span className={`inline-flex items-center gap-1.5 rounded px-2 py-0.5 text-xs font-medium ${tones[tone]}`}>
      <span aria-hidden className="size-1.5 rounded-full bg-current opacity-70" />
      {children}
    </span>
  )
}
