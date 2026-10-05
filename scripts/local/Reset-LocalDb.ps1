# Recreates the local development database lifedrop_dev: every migration in order, the API grants and the
# sample data (db/local/seed_dev.sql). Safe to run again after pulling new migrations; it deletes local dev data.
# Needs: docker compose up -d (docs/LOCAL-SETUP.md).
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 pipes text to native commands as ASCII by default; SQL files contain UTF-8.
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

function Invoke-Psql([string]$Database, [string]$Sql) {
    $Sql | docker compose exec -T db psql -v ON_ERROR_STOP=1 -q -U postgres -d $Database
    if ($LASTEXITCODE -ne 0) { throw "psql failed on $Database" }
}

Write-Host 'Recreating lifedrop_dev...'
Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS lifedrop_dev WITH (FORCE); CREATE DATABASE lifedrop_dev;"
Invoke-Psql 'lifedrop_dev' "CREATE EXTENSION IF NOT EXISTS postgis; GRANT USAGE ON SCHEMA public TO lifedrop_app;"

foreach ($file in Get-ChildItem 'db\migrations\*.sql' | Sort-Object Name) {
    Write-Host "  $($file.Name)"
    Invoke-Psql 'lifedrop_dev' (Get-Content $file.FullName -Raw -Encoding UTF8)
}
Write-Host '  grants'
Invoke-Psql 'lifedrop_dev' (Get-Content 'db\grants\lifedrop_app_grants.sql' -Raw -Encoding UTF8)
Write-Host '  sample data'
Invoke-Psql 'lifedrop_dev' (Get-Content 'db\local\seed_dev.sql' -Raw -Encoding UTF8)
Write-Host 'Done. Company code: dev, users manager@dev.local, supervisor@dev.local, finance@dev.local, rep1@dev.local, rep2@dev.local; password LifeDrop-Local-1'
