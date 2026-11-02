#!/usr/bin/env bash
# Copies the map fonts and icons to our own server (plan step 5.16), so the map no longer asks protomaps.github.io for them.
# The fonts (Noto Sans, open licence) and the icon sheet come from the public repository protomaps/basemaps-assets; they are copied once into deploy/maps/assets.
# Run on the server:  ./deploy/map/fetch-assets.sh     then set the two panel variables printed at the end (deploy/map/README.md).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dest="$here/../maps/assets"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

git clone --depth 1 https://github.com/protomaps/basemaps-assets "$tmp/assets"
mkdir -p "$dest/fonts" "$dest/sprites/v4"
for font in "Noto Sans Regular" "Noto Sans Medium" "Noto Sans Italic"; do
  [ -d "$tmp/assets/fonts/$font" ] || { echo "Font folder not found: $font (the repository changed; tell the developer)" >&2; exit 1; }
  rm -rf "${dest:?}/fonts/$font"
  cp -r "$tmp/assets/fonts/$font" "$dest/fonts/"
done
ls "$tmp"/assets/sprites/v4/light* >/dev/null 2>&1 || { echo "Icon sheet not found (the repository changed; tell the developer)" >&2; exit 1; }
cp "$tmp"/assets/sprites/v4/light* "$dest/sprites/v4/"

source <(grep -E '^API_DOMAIN=' "$here/../.env" 2>/dev/null || true)
domain="${API_DOMAIN:-<API_DOMAIN>}"
echo "Done: $dest ($(du -sh "$dest" | cut -f1))."
echo "Set these two variables in Cloudflare (Build variables) and deploy the panel again:"
echo "VITE_MAP_GLYPHS_URL=https://$domain/maps/assets/fonts/{fontstack}/{range}.pbf"
echo "VITE_MAP_SPRITE_URL=https://$domain/maps/assets/sprites/v4/light"
