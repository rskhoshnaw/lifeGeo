# Exports neighbourhood and street names of cities from OpenStreetMap into one CSV file that you can open in
# Excel, correct and complete. The file is the starting point of the street/neighbourhood reference list
# (plan step 1.23-b). Needs internet; run it on your own computer (PowerShell 7).
#
#   .\scripts\local\Export-CityPlaces.ps1                                  # the seven default cities
#   .\scripts\local\Export-CityPlaces.ps1 -City Erbil,Zakho -OutFile erbil-zakho.csv
#
# Columns: city, kind (neighbourhood|street), name_ckb (Sorani Kurdish), name_ar, name_en, name_local (the main
# OSM name), osm (OpenStreetMap id), active (yes/no: set no to hide a row). OpenStreetMap is incomplete in some
# cities (small streets without a name are missing), so read the file before using it.
param(
    [string[]]$City = @('Erbil', 'Sulaymaniyah', 'Duhok', 'Zakho', 'Kirkuk', 'Ranya', 'Mosul'),
    [string]$OutFile = 'places.csv'
)
$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'LifeDrop-place-export/1.0 (internal company tool)' }
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$streetTypes = 'motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|pedestrian'

# Retries a web call a few times: the public servers are sometimes busy.
function Invoke-Web([scriptblock]$Call) {
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try { return & $Call }
        catch {
            if ($attempt -eq 4) { throw }
            Write-Host "  server busy, trying again in $($attempt * 10) seconds..."
            Start-Sleep -Seconds ($attempt * 10)
        }
    }
}

$rows = @()
foreach ($name in $City) {
    Write-Host "$name ..."

    # 1. The city's bounding box from Nominatim (order of boundingbox: south, north, west, east).
    $query = [uri]::EscapeDataString("$name, Iraq")
    $found = Invoke-Web { Invoke-RestMethod -Headers $headers -Uri "https://nominatim.openstreetmap.org/search?q=$query&format=jsonv2&limit=1&countrycodes=iq" }
    if (-not $found -or $found.Count -eq 0) { Write-Warning "  '$name' not found, skipped."; continue }
    $box = $found[0].boundingbox
    $south = [double]::Parse($box[0], $invariant); $north = [double]::Parse($box[1], $invariant)
    $west = [double]::Parse($box[2], $invariant); $east = [double]::Parse($box[3], $invariant)
    $area = '(' + (($south, $west, $north, $east | ForEach-Object { $_.ToString($invariant) }) -join ',') + ')'

    # 2. Named neighbourhoods and named streets inside the box (tags only, no geometry).
    $overpass = @"
[out:json][timeout:150];
(
  nwr["place"~"^(suburb|neighbourhood|quarter)$"]["name"]$area;
  way["highway"~"^($streetTypes)$"]["name"]$area;
);
out tags;
"@
    $result = Invoke-Web { Invoke-RestMethod -Method Post -Headers $headers -Uri 'https://overpass-api.de/api/interpreter' -Body @{ data = $overpass } -TimeoutSec 200 }

    # 3. One row per distinct name and kind (a street is split into many pieces in OpenStreetMap).
    $seen = @{}
    $count = 0
    foreach ($element in $result.elements) {
        $tags = $element.tags
        if (-not $tags -or -not $tags.name) { continue }
        $kind = if ($tags.place) { 'neighbourhood' } else { 'street' }
        $key = "$kind|" + $tags.name.Trim().ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $ckb = if ($tags.'name:ckb') { $tags.'name:ckb' } else { $tags.'name:ku' }
        $rows += [pscustomobject]@{
            city = $name; kind = $kind; name_ckb = $ckb; name_ar = $tags.'name:ar'; name_en = $tags.'name:en'
            name_local = $tags.name; osm = "$($element.type)/$($element.id)"; active = 'yes'
        }
        $count++
    }
    Write-Host "  $count names"
    Start-Sleep -Seconds 3   # be polite to the public servers
}

$rows | Sort-Object city, kind, name_local | Export-Csv -Path $OutFile -NoTypeInformation -Encoding utf8BOM
Write-Host "Wrote $($rows.Count) rows to $OutFile. Open it in Excel, correct the names and set active=no for rows you do not want."
