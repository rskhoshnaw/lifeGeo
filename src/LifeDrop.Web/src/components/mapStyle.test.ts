import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => {
  vi.unstubAllEnvs()
  vi.resetModules()
})

async function load(address: string | null, lang: 'ckb' | 'en' | 'ar' = 'ckb') {
  vi.resetModules()
  if (address !== null) vi.stubEnv('VITE_MAP_PMTILES_URL', address)
  const i18n = await import('../i18n.ts')
  i18n.setCurrentLang(lang)
  return import('./mapStyle.ts')
}

describe('map style', () => {
  it('keeps the public raster tiles when no own map is set', async () => {
    const m = await load('')
    const style = m.mapStyle()
    expect(Object.keys(style.sources)).toEqual(['osm'])
    expect(style.layers).toHaveLength(1)
  })

  it('draws the own PMTiles map, with labels in the language of the panel', async () => {
    const m = await load('https://api.test/maps/iraq.pmtiles', 'ckb')
    const style = m.mapStyle()
    expect(style.sources.protomaps).toMatchObject({ type: 'vector', url: 'pmtiles://https://api.test/maps/iraq.pmtiles' })
    expect(style.glyphs).toContain('{fontstack}')
    const text = JSON.stringify(style.layers)
    expect(text).toContain('name:ckb')
  })

  it('follows another language too', async () => {
    const m = await load('https://api.test/maps/iraq.pmtiles', 'ar')
    expect(JSON.stringify(m.mapStyle().layers)).toContain('name:ar')
  })

  it('registers the pmtiles address once, and only with an own map', async () => {
    const off = await load('')
    const none = vi.fn()
    off.registerMapProtocol({ addProtocol: none })
    expect(none).not.toHaveBeenCalled()

    const on = await load('https://api.test/maps/iraq.pmtiles')
    const add = vi.fn()
    on.registerMapProtocol({ addProtocol: add })
    on.registerMapProtocol({ addProtocol: add })
    expect(add).toHaveBeenCalledTimes(1)
    expect(add.mock.calls[0]![0]).toBe('pmtiles')
  })
})
