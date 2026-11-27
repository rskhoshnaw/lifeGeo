import { describe, expect, it } from 'vitest'
import app from '../App.tsx?raw'
import { placement } from './nav.ts'

// A screen without a place in the menu is built but nobody finds it (owner report 2026-11-27: warehouse management). Every tab of App.tsx needs a place.
describe('menu placement', () => {
  it('has a place for every tab except the notification bell', () => {
    const tabs = (/^type Tab = (.*)$/m.exec(app)![1]!.match(/'(\w+)'/g) ?? []).map((x: string) => x.slice(1, -1))
    expect(tabs.length).toBeGreaterThan(20)
    expect(tabs.filter((id: string) => id !== 'notifications' && !placement[id])).toEqual([])
  })
})
