import { circlePolygon, formatDuration, nextIndex } from './geo.ts'

const distance = (lat1: number, lng1: number, lat2: number, lng2: number) => {
  const r = 6_371_000
  const rad = (d: number) => (d * Math.PI) / 180
  const a = Math.sin(rad(lat2 - lat1) / 2) ** 2 + Math.cos(rad(lat1)) * Math.cos(rad(lat2)) * Math.sin(rad(lng2 - lng1) / 2) ** 2
  return 2 * r * Math.asin(Math.sqrt(a))
}

describe('map helpers', () => {
  it('draws a closed circle whose points are the radius away from the centre', () => {
    const [ring] = circlePolygon(36.19, 44.0, 50)
    expect(ring![0]).toEqual(ring![ring!.length - 1])
    for (const [lng, lat] of ring!) {
      const meters = distance(36.19, 44.0, lat, lng)
      expect(meters).toBeGreaterThan(49.5)
      expect(meters).toBeLessThan(50.5)
    }
  })

  it('moves a playback one point at a time and stops at the end', () => {
    expect(nextIndex(0, 3)).toBe(1)
    expect(nextIndex(2, 3)).toBe(2)
    expect(nextIndex(0, 0)).toBe(0)
  })

  it('writes durations in minutes and hours', () => {
    expect(formatDuration(330)).toBe('6 min')
    expect(formatDuration(3900)).toBe('1 h 5 min')
  })
})
