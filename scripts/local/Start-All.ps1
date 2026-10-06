# One command for everything (docs/LOCAL-SETUP.md): Docker database, API, web panel and, when asked, the app on the phone.
#   -Pull      first get the newest code of the branch main (only when nothing is changed locally)
#   -Reset     always rebuild the local database (otherwise only when migrations / seed data changed since the last time)
#   -Android   also install and start the app on the phone connected by USB (USB debugging on)
#   -NoBrowser do not open the panel in the browser
# Safe to run again: what is already running is not started twice.
param(
    [switch]$Pull,
    [switch]$Reset,
    [switch]$Android,
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalDb.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Step([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host ''; Write-Host "STOP: $Text" -ForegroundColor Red; Read-Host 'Press Enter to close'; exit 1 }
function Quote([string]$Path) { return '"' + $Path + '"' }
function Test-Port([int]$Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $Port); return $true } catch { return $false } finally { $client.Close() }
}
function Wait-Port([int]$Port, [int]$Seconds, [string]$Name) {
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) {
        if (Test-Port $Port) { return }
        Start-Sleep -Seconds 2
    }
    Fail "$Name did not start within $Seconds seconds. Look at its window for the error."
}

# --- 0. the newest code (optional)
if ($Pull) {
    Step 'Getting the newest code'
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $changes = (git status --porcelain)
    if ($branch -ne 'main') { Write-Host "Skipped: you are on '$branch', not 'main'." -ForegroundColor Yellow }
    elseif ($changes) { Write-Host 'Skipped: there are local changes (commit or discard them first).' -ForegroundColor Yellow }
    else { git pull --ff-only origin main; if ($LASTEXITCODE -ne 0) { Fail 'git pull failed.' } }
}

# --- 1. Docker
Step 'Docker'
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail 'Docker is not installed. Install Docker Desktop (docs/LOCAL-SETUP.md, part 1).' }
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    $desktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path $desktop)) { Fail 'Docker Desktop is not running. Open it and wait until it says Running.' }
    Write-Host 'Starting Docker Desktop (this can take a minute)...'
    Start-Process $desktop
    $until = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 5
        docker info *> $null
    } while ($LASTEXITCODE -ne 0 -and (Get-Date) -lt $until)
    if ($LASTEXITCODE -ne 0) { Fail 'Docker Desktop did not become ready in 3 minutes.' }
}

# --- 2. database
Step 'Database'
docker compose up -d
if ($LASTEXITCODE -ne 0) { Fail 'docker compose up failed.' }
Wait-Port ([int]$LocalDbPort) 90 'The database'
# The container accepts connections a moment before PostgreSQL is ready to work.
$until = (Get-Date).AddSeconds(60)
do {
    docker compose exec -T db pg_isready -U postgres *> $null
    if ($LASTEXITCODE -ne 0) { Start-Sleep -Seconds 2 }
} while ($LASTEXITCODE -ne 0 -and (Get-Date) -lt $until)
if ($LASTEXITCODE -ne 0) { Fail 'PostgreSQL is not ready.' }

# The database is rebuilt when the migrations, grants or sample data changed since the last build (or the database is missing).
$stateDir = Join-Path $env:LOCALAPPDATA 'LifeDrop'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$stateFile = Join-Path $stateDir 'db-state.txt'
$files = @(Get-ChildItem 'db\migrations\*.sql' | Sort-Object Name) + @(Get-Item 'db\grants\lifedrop_app_grants.sql', 'db\local\seed_dev.sql')
$hash = (($files | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join '')
$current = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($hash)))
$exists = ('SELECT 1 FROM pg_database WHERE datname = ''lifedrop_dev''' | docker compose exec -T db psql -q -t -A -U postgres -d postgres) -join ''
$saved = if (Test-Path $stateFile) { (Get-Content $stateFile -Raw).Trim() } else { '' }
if ($Reset -or $exists.Trim() -ne '1' -or $saved -ne $current) {
    if ($Reset) { Write-Host 'Rebuilding the local database (asked).' }
    elseif ($exists.Trim() -ne '1') { Write-Host 'The local database does not exist yet: building it.' }
    else { Write-Host 'Migrations or sample data changed: rebuilding the local database (local data is replaced).' }
    & (Join-Path $PSScriptRoot 'Reset-LocalDb.ps1')
    Set-Content -Path $stateFile -Value $current
} else {
    Write-Host 'The local database is up to date.'
}

# --- 3. API and panel, each in its own window
Step 'API'
if (Test-Port 5256) { Write-Host 'Already running on port 5256.' }
else {
    Start-Process powershell -ArgumentList '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', (Quote (Join-Path $PSScriptRoot 'Start-Api.ps1')) -WorkingDirectory $root
    Write-Host 'Starting (the first start builds the project; wait)...'
    Wait-Port 5256 300 'The API'
}
Step 'Panel'
if (Test-Port 5273) { Write-Host 'Already running on port 5273.' }
else {
    Start-Process powershell -ArgumentList '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', (Quote (Join-Path $PSScriptRoot 'Start-Panel.ps1')) -WorkingDirectory $root
    Wait-Port 5273 180 'The panel'
}

# --- 4. the phone (optional)
if ($Android) {
    Step 'Phone'
    if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { Write-Host 'adb was not found (docs/LOCAL-SETUP.md, common problems).' -ForegroundColor Yellow }
    else {
        $devices = @(adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' })
        if ($devices.Count -eq 0) { Write-Host 'No phone found: connect it by USB and allow USB debugging, then run this again with -Android.' -ForegroundColor Yellow }
        else {
            Start-Process powershell -ArgumentList '-NoExit', '-ExecutionPolicy', 'Bypass', '-File', (Quote (Join-Path $PSScriptRoot 'Run-Android.ps1')) -WorkingDirectory $root
            Write-Host 'Installing the app on the phone in a new window (the first build takes several minutes).'
        }
    }
}

# --- 5. done
Step 'Ready'
Write-Host 'Panel:    http://localhost:5273   (company dev, manager@dev.local, password LifeDrop-Local-1)'
Write-Host 'API:      http://localhost:5256'
Write-Host 'Stop all: scripts\local\Stop-All.ps1 (or LifeDrop-Stop.bat)'
if (-not $NoBrowser) { Start-Process 'http://localhost:5273' }
