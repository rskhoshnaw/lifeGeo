// OpenStreetMap raster tiles (decision D12/D13 stack: MapLibre + OSM). The public OSM tile server is for light use;
// a production tile provider can replace this URL without other changes.
export const style = {
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
