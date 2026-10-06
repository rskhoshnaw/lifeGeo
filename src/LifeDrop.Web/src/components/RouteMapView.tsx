import { useEffect, useRef, useState } from 'react'
import type { GeoJSONSource, Map as MapLibreMap, Marker } from 'maplibre-gl'
import type { RoutePoint, RouteStop, RouteVisitEvent } from '../api/admin.ts'
import { t } from '../i18n.ts'

// Same OSM raster style as the live map (decision D12/D13).
const style = {
  version: 8 as const,
  sources: { osm: { type: 'raster' as const, tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'], tileSize: 256, attribution: '© OpenStreetMap contributors' } },
  layers: [{ id: 'osm', type: 'raster' as const, source: 'osm' }],
}

type MapLibre = typeof import('maplibre-gl')

// The route of one day (plan step 2.11): the line, the stops, the check-ins and check-outs, fake positions in red, and a marker at the point being played.
// Popups use plain text only.
export function RouteMapView({ points, stops, events, index }: { points: RoutePoint[]; stops: RouteStop[]; events: RouteVisitEvent[]; index: number }) {
  const container = useRef<HTMLDivElement>(null)
  const markers = useRef<Marker[]>([])
  const current = useRef<Marker | null>(null)
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

  // The line and the fixed markers, drawn again when the route changes.
  useEffect(() => {
    if (!loaded) return
    const { lib, map } = loaded
    markers.current.forEach((m) => m.remove())
    markers.current = []
    const apply = () => {
      const line = { type: 'Feature' as const, properties: {}, geometry: { type: 'LineString' as const, coordinates: points.map((p) => [p.longitude, p.latitude]) } }
      const mocked = { type: 'FeatureCollection' as const, features: points.filter((p) => p.isMocked).map((p) => ({ type: 'Feature' as const, properties: {}, geometry: { type: 'Point' as const, coordinates: [p.longitude, p.latitude] } })) }
      const lineSource = map.getSource('route') as GeoJSONSource | undefined
      if (lineSource) {
        lineSource.setData(line)
        ;(map.getSource('mocked') as GeoJSONSource).setData(mocked)
      } else {
        map.addSource('route', { type: 'geojson', data: line })
        map.addSource('mocked', { type: 'geojson', data: mocked })
        map.addLayer({ id: 'route-line', type: 'line', source: 'route', paint: { 'line-color': '#2563eb', 'line-width': 3 } })
        map.addLayer({ id: 'mocked-points', type: 'circle', source: 'mocked', paint: { 'circle-color': '#dc2626', 'circle-radius': 6 } })
      }
    }
    if (map.loaded()) apply()
    else map.once('load', apply)
    const time = (iso: string) => new Date(iso).toLocaleTimeString('en-GB', { timeZone: 'Asia/Baghdad', hour: '2-digit', minute: '2-digit' })
    stops.forEach((s) =>
      markers.current.push(new lib.Marker({ color: '#f59e0b' }).setLngLat([s.longitude, s.latitude]).setPopup(new lib.Popup().setText(`${time(s.from)}–${time(s.to)} ${s.customerName ?? ''}`)).addTo(map)),
    )
    events.forEach((e) =>
      markers.current.push(
        new lib.Marker({ color: e.accepted ? '#16a34a' : '#dc2626' })
          .setLngLat([e.longitude, e.latitude])
          .setPopup(new lib.Popup().setText(`${t(`web.route.event.${e.kind}`)} ${time(e.capturedAt)} ${e.customerName ?? ''}`))
          .addTo(map),
      ),
    )
    if (points.length > 0) {
      const bounds = new lib.LngLatBounds()
      points.forEach((p) => bounds.extend([p.longitude, p.latitude]))
      map.fitBounds(bounds, { padding: 60, maxZoom: 16, duration: 0 })
    }
  }, [loaded, points, stops, events])

  // The marker of the point being played.
  useEffect(() => {
    if (!loaded) return
    const point = points[index]
    current.current?.remove()
    current.current = point ? new loaded.lib.Marker({ color: '#111827', scale: 0.8 }).setLngLat([point.longitude, point.latitude]).addTo(loaded.map) : null
  }, [loaded, points, index])

  return <div ref={container} className="h-96 w-full rounded border" aria-label={t('web.route.title')} />
}
