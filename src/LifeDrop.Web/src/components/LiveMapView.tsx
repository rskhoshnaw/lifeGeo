import { useEffect, useRef, useState } from 'react'
import type { GeoJSONSource, Map as MapLibreMap, Marker } from 'maplibre-gl'
import type { LiveMapEntry, MapCustomer } from '../api/admin.ts'
import { circlePolygon } from '../api/geo.ts'
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
export function LiveMapView({ entries, customers = [] }: { entries: LiveMapEntry[]; customers?: MapCustomer[] }) {
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
        .setPopup(new lib.Popup().setText(`${e.email ?? e.userId}${e.deviceLabel ? ` · ${e.deviceLabel}` : ''}${e.isLive ? '' : t('web.map.notLive')}`))
        .addTo(map),
    )
    if (located.length > 0) {
      const bounds = new lib.LngLatBounds()
      located.forEach((e) => bounds.extend([e.longitude!, e.latitude!]))
      map.fitBounds(bounds, { padding: 60, maxZoom: 14, duration: 0 })
    }
  }, [loaded, entries])

  // Customers (plan step 2.11): grouped into clusters when far apart, the check-in circle of each one when zoomed in (zoom 14 and closer).
  useEffect(() => {
    if (!loaded) return
    const { map } = loaded
    const points = {
      type: 'FeatureCollection' as const,
      features: customers.map((c) => ({ type: 'Feature' as const, properties: { name: `${c.code} · ${c.name}` }, geometry: { type: 'Point' as const, coordinates: [c.longitude, c.latitude] } })),
    }
    const circles = {
      type: 'FeatureCollection' as const,
      features: customers.map((c) => ({ type: 'Feature' as const, properties: {}, geometry: { type: 'Polygon' as const, coordinates: circlePolygon(c.latitude, c.longitude, c.geofenceRadiusMeters) } })),
    }
    const apply = () => {
      const source = map.getSource('customers') as GeoJSONSource | undefined
      if (source) {
        source.setData(points)
        ;(map.getSource('geofences') as GeoJSONSource).setData(circles)
        return
      }
      map.addSource('customers', { type: 'geojson', data: points, cluster: true, clusterRadius: 50, clusterMaxZoom: 14 })
      map.addSource('geofences', { type: 'geojson', data: circles })
      map.addLayer({ id: 'geofences-fill', type: 'fill', source: 'geofences', minzoom: 14, paint: { 'fill-color': '#2563eb', 'fill-opacity': 0.12 } })
      map.addLayer({ id: 'geofences-line', type: 'line', source: 'geofences', minzoom: 14, paint: { 'line-color': '#2563eb', 'line-width': 1 } })
      map.addLayer({
        id: 'customer-clusters', type: 'circle', source: 'customers', filter: ['has', 'point_count'],
        paint: { 'circle-color': '#0f766e', 'circle-opacity': 0.8, 'circle-radius': ['step', ['get', 'point_count'], 14, 10, 20, 50, 28] },
      })
      map.addLayer({ id: 'customer-points', type: 'circle', source: 'customers', filter: ['!', ['has', 'point_count']], paint: { 'circle-color': '#2563eb', 'circle-radius': 5, 'circle-stroke-width': 1, 'circle-stroke-color': '#fff' } })
      map.on('click', 'customer-clusters', (e) => {
        const feature = e.features?.[0]
        if (feature && feature.geometry.type === 'Point') map.easeTo({ center: feature.geometry.coordinates as [number, number], zoom: map.getZoom() + 2 })
      })
      map.on('click', 'customer-points', (e) => {
        const feature = e.features?.[0]
        if (feature && feature.geometry.type === 'Point') new loaded.lib.Popup().setLngLat(feature.geometry.coordinates as [number, number]).setText(String(feature.properties?.name ?? '')).addTo(map)
      })
    }
    if (map.loaded()) apply()
    else map.once('load', apply)
  }, [loaded, customers])

  return <div ref={container} className="h-96 w-full rounded border" aria-label={t('web.map.title')} />
}
