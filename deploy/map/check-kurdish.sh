#!/usr/bin/env bash
# Checks whether the own map has Central Kurdish names (name:ckb) near Erbil, Sulaymaniyah and Duhok (plan step 5.16).
# Usage: ./deploy/map/check-kurdish.sh [path/to/iraq.pmtiles]     (needs the pmtiles program or Docker, and python3)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
file="${1:-$here/../maps/iraq.pmtiles}"
[ -f "$file" ] || { echo "No file: $file" >&2; exit 1; }
dir="$(cd "$(dirname "$file")" && pwd)"; base="$(basename "$file")"

pm() {
  if command -v pmtiles >/dev/null 2>&1; then pmtiles "$@"; else docker run --rm -v "$dir:/data" protomaps/go-pmtiles "$@"; fi
}
path="$file"; if ! command -v pmtiles >/dev/null 2>&1; then path="/data/$base"; fi

zoom=10
for place in "Erbil 44.0092 36.1912" "Sulaymaniyah 45.4329 35.5613" "Duhok 42.9482 36.8679"; do
  set -- $place
  read -r x y < <(python3 - "$3" "$2" "$zoom" <<'PY'
import math, sys
lat, lon, z = float(sys.argv[1]), float(sys.argv[2]), int(sys.argv[3])
n = 2 ** z
print(int((lon + 180) / 360 * n), int((1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * n))
PY
)
  count=$(pm tile "$path" "$zoom" "$x" "$y" 2>/dev/null | { gunzip -c 2>/dev/null || cat; } | grep -a -c 'name:ckb' || true)
  echo "$1 (tile $zoom/$x/$y): lines with name:ckb = $count"
done
echo "name:ckb = 0 everywhere is normal for the Protomaps build (it keeps the names of a fixed list of languages, Kurdish is not on it). The panel then shows the local name (the OpenStreetMap name tag): Kurdish in Kurdistan, Arabic elsewhere."
echo "Languages that are in the map (Erbil tile):"
pm tile "$path" "$zoom" 637 401 2>/dev/null | { gunzip -c 2>/dev/null || cat; } | grep -a -o 'name:[A-Za-z_-]*' | sort | uniq -c | sort -rn | head -40 || true
