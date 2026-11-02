/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string
  // Own map (plan step 5.16): address of the .pmtiles file (for example https://api.example.com/maps/iraq.pmtiles). Without it the maps use the public OpenStreetMap raster tiles.
  readonly VITE_MAP_PMTILES_URL?: string
  readonly VITE_MAP_GLYPHS_URL?: string
  readonly VITE_MAP_SPRITE_URL?: string
}
