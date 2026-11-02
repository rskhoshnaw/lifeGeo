import { useEffect, useRef, useState } from 'react'
import type { Map as MapLibreMap, Marker } from 'maplibre-gl'
import { t } from '../i18n.ts'
import { style } from './mapStyle.ts'

type MapLibre = typeof import('maplibre-gl')

// One customer on a map (plan step 5.13): a marker at the saved position, zoom buttons (+ and −), and, for a user who may move customers, a click on the map puts the marker
// there (the caller saves it). A customer without a position starts at the middle of Iraq until a spot is clicked.
export function CustomerLocationMap({ position, canMove, onPick }: { position: { latitude: number; longitude: number } | null; canMove: boolean; onPick: (latitude: number, longitude: number) => void }) {
  const container = useRef<HTMLDivElement>(null)
  const marker = useRef<Marker | null>(null)
  const pick = useRef(onPick)
  const movable = useRef(canMove)
  const [loaded, setLoaded] = useState<{ lib: MapLibre; map: MapLibreMap } | null>(null)
  useEffect(() => {
    pick.current = onPick
    movable.current = canMove
  })

  useEffect(() => {
    let disposed = false
    let map: MapLibreMap | null = null
    void Promise.all([import('maplibre-gl'), import('maplibre-gl/dist/maplibre-gl.css')]).then(([lib]) => {
      if (disposed || !container.current) return
      map = new lib.Map({ container: container.current, style, center: [44.0, 36.19], zoom: 6 })
      map.addControl(new lib.NavigationControl({ showCompass: false }), 'top-right')
      map.on('click', (e) => {
        if (movable.current) pick.current(e.lngLat.lat, e.lngLat.lng)
      })
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
    marker.current?.remove()
    marker.current = null
    if (!position) return
    marker.current = new lib.Marker({ color: '#2563eb' }).setLngLat([position.longitude, position.latitude]).addTo(map)
    map.flyTo({ center: [position.longitude, position.latitude], zoom: Math.max(map.getZoom(), 16), duration: 0 })
  }, [loaded, position])

  return <div ref={container} className="h-72 w-full rounded border" aria-label={t('web.customers.mapLabel')} />
}
