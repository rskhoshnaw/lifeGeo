# Runs the web panel at http://localhost:5273 against the local API (Start-Api.ps1).
# Port 5273 (not Vite's default 5173) because another project on this computer uses 5173.
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..\src\LifeDrop.Web'))
if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } }
$env:VITE_API_BASE_URL = 'http://localhost:5256'
npm run dev -- --port 5273 --strictPort
