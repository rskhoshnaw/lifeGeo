// Map helpers (plan step 2.11): the check-in circle of a customer and the playback of a route.

// A circle as a GeoJSON polygon ring (lng, lat), good enough for radii of tens to hundreds of metres.
export function circlePolygon(latitude: number, longitude: number, radiusMeters: number, steps = 48): [number, number][][] {
  const earth = 6_371_000
  const lat = (latitude * Math.PI) / 180
  const ring: [number, number][] = []
  for (let i = 0; i <= steps; i++) {
    const angle = (2 * Math.PI * i) / steps
    const dLat = (radiusMeters / earth) * Math.cos(angle)
    const dLng = (radiusMeters / (earth * Math.cos(lat))) * Math.sin(angle)
    ring.push([longitude + (dLng * 180) / Math.PI, latitude + (dLat * 180) / Math.PI])
  }
  return [ring]
}

// The next point of a playback; stays on the last point when the route is over.
export function nextIndex(index: number, length: number): number {
  return Math.min(Math.max(0, length - 1), index + 1)
}

export function formatDuration(seconds: number): string {
  const minutes = Math.round(seconds / 60)
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`
}
