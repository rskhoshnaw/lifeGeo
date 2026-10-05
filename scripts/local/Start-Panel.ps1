# Runs the web panel at http://localhost:5173 against the local API (Start-Api.ps1).
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..\src\LifeDrop.Web'))
if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' } }
$env:VITE_API_BASE_URL = 'http://localhost:5256'
npm run dev
