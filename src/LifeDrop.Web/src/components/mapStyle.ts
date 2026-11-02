import type { LayerSpecification, StyleSpecification } from 'maplibre-gl'
import { Protocol } from 'pmtiles'
import { layers, namedFlavor } from '@protomaps/basemaps'
import { currentLang } from '../i18n.ts'

// The map every screen uses (decision D12/D13 stack: MapLibre + OpenStreetMap data).
//
// Own map (plan step 5.16): when VITE_MAP_PMTILES_URL is set, the map is drawn from a vector tile file (PMTiles) on our own server (deploy/map/README.md): free, no limit on
// use, and the labels follow the language of the panel (Central Kurdish name:ckb, Arabic, Persian, English), which the raster tiles cannot do. Right-to-left labels need no
// plugin in this MapLibre version. Without the setting the screens keep the public OpenStreetMap raster tiles, whose usage policy allows only light use.
const glyphs = 'https://protomaps.github.io/basemaps-assets/fonts/{fontstack}/{range}.pbf'
const sprite = 'https://protomaps.github.io/basemaps-assets/sprites/v4/light'

const raster: StyleSpecification = {
  version: 8,
  sources: {
    osm: {
      type: 'raster',
      tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
      tileSize: 256,
      attribution: '© OpenStreetMap contributors',
    },
  },
  layers: [{ id: 'osm', type: 'raster', source: 'osm' }],
}

export const ownMapAddress = (): string => (import.meta.env.VITE_MAP_PMTILES_URL ?? '').trim()

// The style for the current language. Read when a map is created, so a change of language shows on the next map.
export function mapStyle(): StyleSpecification {
  const address = ownMapAddress()
  if (!address) return raster
  return {
    version: 8,
    glyphs: import.meta.env.VITE_MAP_GLYPHS_URL || glyphs,
    sprite: import.meta.env.VITE_MAP_SPRITE_URL || sprite,
    sources: { protomaps: { type: 'vector', url: `pmtiles://${address}`, attribution: '© OpenStreetMap contributors' } },
    layers: labelLayers(currentLang()),
  }
}

// Central Kurdish: the Protomaps build keeps the names of a fixed list of languages and Kurdish is not on it, so the English name would show. In Kurdistan the local name (the OpenStreetMap
// `name` tag) is Kurdish, in the rest of Iraq Arabic: the label is name:ckb when the tile has it, else the local name, else the English one. Other languages use the library's own rules.
function labelLayers(lang: string): LayerSpecification[] {
  if (lang !== 'ckb') return layers('protomaps', namedFlavor('light'), { lang })
  const field = ['coalesce', ['get', 'name:ckb'], ['get', 'name'], ['get', 'name:en']]
  return layers('protomaps', namedFlavor('light'), { lang: 'en' }).map((layer) =>
    layer.type === 'symbol' && layer.layout?.['text-field'] !== undefined && JSON.stringify(layer.layout['text-field']).includes('"name')
      ? { ...layer, layout: { ...layer.layout, 'text-field': field } }
      : layer,
  ) as LayerSpecification[]
}

let protocol: Protocol | null = null

// Teaches MapLibre the pmtiles:// address (once). Does nothing when the own map is not set.
export function registerMapProtocol(lib: { addProtocol: (name: string, handler: never) => void }) {
  if (!ownMapAddress() || protocol) return
  protocol = new Protocol()
  lib.addProtocol('pmtiles', protocol.tile as never)
}
