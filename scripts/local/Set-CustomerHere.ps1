# Moves a local customer to the place where you test with the phone (check-in needs to be within the radius).
# Example: .\scripts\local\Set-CustomerHere.ps1 -Lat 36.19123 -Lon 44.00987
# Optional: -Code D-001 (default P-001), -Radius 100 (check-in radius in metres, default: unchanged).
param(
    [Parameter(Mandatory)][double]$Lat,
    [Parameter(Mandatory)][double]$Lon,
    [string]$Code = 'P-001',
    [int]$Radius = 0
)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..'))
if ($Code -notmatch '^[A-Za-z0-9-]+$') { throw 'Code may only contain letters, digits and -' }
if ($Lat -lt -90 -or $Lat -gt 90 -or $Lon -lt -180 -or $Lon -gt 180) { throw 'Lat must be -90..90 and Lon -180..180 (did you swap them?)' }
$latText = $Lat.ToString([cultureinfo]::InvariantCulture)
$lonText = $Lon.ToString([cultureinfo]::InvariantCulture)
if ($Radius -ne 0 -and ($Radius -lt 5 -or $Radius -gt 1000)) { throw 'Radius must be 5..1000 metres' }
$radiusSql = if ($Radius -gt 0) { ", geofence_radius_m = $Radius" } else { '' }
$sql = "UPDATE customers SET geom = ST_GeogFromText('SRID=4326;POINT($lonText $latText)')$radiusSql WHERE code = '$Code';"
$sql | docker compose exec -T db psql -v ON_ERROR_STOP=1 -U postgres -d lifedrop_dev
if ($LASTEXITCODE -ne 0) { throw 'psql failed' }
