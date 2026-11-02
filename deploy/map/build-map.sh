#!/usr/bin/env bash
# Makes the own map of Iraq (plan step 5.16): cuts the region out of the daily Protomaps world build (OpenStreetMap data) into deploy/maps/iraq.pmtiles.
# Only the needed parts are downloaded (HTTP range requests), so the world file (about 120 GB) is not fetched. Run on the server, from anywhere:
#   ./deploy/map/build-map.sh
# Needs the `pmtiles` program (https://github.com/protomaps/go-pmtiles/releases) or Docker (then the image protomaps/go-pmtiles is used).
# Settings (environment): MAP_BBOX="west,south,east,north" (default: Iraq), MAP_MAXZOOM (default 15), MAP_OUT (default deploy/maps/iraq.pmtiles), MAP_SOURCE (a full address of a build).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${MAP_OUT:-$here/../maps/iraq.pmtiles}"
bbox="${MAP_BBOX:-38.7,28.9,48.9,37.5}"
maxzoom="${MAP_MAXZOOM:-15}"
mkdir -p "$(dirname "$out")"
out="$(cd "$(dirname "$out")" && pwd)/$(basename "$out")"

run_pmtiles() {
  if command -v pmtiles >/dev/null 2>&1; then
    pmtiles "$@"
  elif command -v docker >/dev/null 2>&1; then
    docker run --rm -v "$(dirname "$out"):/data" protomaps/go-pmtiles "$@"
  else
    echo "Install the pmtiles program or Docker first (see the top of this file)." >&2
    exit 1
  fi
}

# Docker sees the output folder as /data.
target="$out"
if ! command -v pmtiles >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then target="/data/$(basename "$out")"; fi

if [ -n "${MAP_SOURCE:-}" ]; then
  sources=("$MAP_SOURCE")
else
  # The world build is made every day; yesterday's is surely there, then the days before it.
  sources=()
  for back in 1 2 3 4 5 6; do sources+=("https://build.protomaps.com/$(date -u -d "-$back day" +%Y%m%d).pmtiles"); done
fi

tmp="$out.part"
rm -f "$tmp"
for source in "${sources[@]}"; do
  echo "Trying $source"
  if run_pmtiles extract "$source" "${target}.part" --bbox="$bbox" --maxzoom="$maxzoom"; then
    mv -f "$tmp" "$out"
    echo "Done: $out ($(du -h "$out" | cut -f1)). The panel needs VITE_MAP_PMTILES_URL=https://<API_DOMAIN>/maps/$(basename "$out")"
    exit 0
  fi
  rm -f "$tmp"
done
echo "No world build could be read. Check the internet of this server, or set MAP_SOURCE." >&2
exit 1
