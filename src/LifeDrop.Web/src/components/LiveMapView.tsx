import { useEffect, useRef, useState } from 'react'
import type { Map as MapLibreMap, Marker } from 'maplibre-gl'
import type { LiveMapEntry } from '../api/admin.ts'
import { t } from '../i18n.ts'

// OpenStreetMap raster tiles (decision D12/D13 stack: MapLibre + OSM). The public OSM tile server is for light use;
// a production tile provider can replace this URL without other changes.
const style = {
  version: 8 as const,
  sources: {
    osm: {
      type: 'raster' as const,
      tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
      tileSize: 256,
      attribution: '© OpenStreetMap contributors',
    },
  },
  layers: [{ id: 'osm', type: 'raster' as const, source: 'osm' }],
}

type MapLibre = typeof import('maplibre-gl')

// Markers for entries with a position: green when live, grey when the last point is older than 5 minutes.
// Popups use plain text only (no HTML from the server).
export function LiveMapView({ entries }: { entries: LiveMapEntry[] }) {
  const container = useRef<HTMLDivElement>(null)
  const markers = useRef<Marker[]>([])
  const [loaded, setLoaded] = useState<{ lib: MapLibre; map: MapLibreMap } | null>(null)

  useEffect(() => {
    let disposed = false
    let map: MapLibreMap | null = null
    void Promise.all([import('maplibre-gl'), import('maplibre-gl/dist/maplibre-gl.css')]).then(([lib]) => {
      if (disposed || !container.current) return
      map = new lib.Map({ container: container.current, style, center: [44.0, 36.19], zoom: 6 })
      setLoaded({ lib, map })
    })
    return () => {
      disposed = true
      map?.remove()
    }
  }, [])

  useEffect(() => {
    if (!loaded) return
    const { lib, map } = loaded
    markers.current.forEach((m) => m.remove())
    const located = entries.filter((e) => e.latitude !== null && e.longitude !== null)
    markers.current = located.map((e) =>
      new lib.Marker({ color: e.isLive ? '#16a34a' : '#94a3b8' })
        .setLngLat([e.longitude!, e.latitude!])
        .setPopup(new lib.Popup().setText(`${e.email ?? e.userId}${e.isLive ? '' : t('web.map.notLive')}`))
        .addTo(map),
    )
    if (located.length > 0) {
      const bounds = new lib.LngLatBounds()
      located.forEach((e) => bounds.extend([e.longitude!, e.latitude!]))
      map.fitBounds(bounds, { padding: 60, maxZoom: 14, duration: 0 })
    }
  }, [loaded, entries])

  return <div ref={container} className="h-96 w-full rounded border" aria-label={t('web.map.title')} />
}
