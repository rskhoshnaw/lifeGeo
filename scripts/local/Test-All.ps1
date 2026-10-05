# Runs the same checks as CI before a push: database tests, .NET unit tests, web panel lint/typecheck/tests/build,
# and with -Android the Android build. Needs: docker compose up -d.
param([switch]$Android)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root

function Step([string]$Name, [scriptblock]$Command) {
    Write-Host "`n=== $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $Name" -ForegroundColor Red; exit 1 }
}

$env:TEST_DATABASE_URL = 'Host=localhost;Port=5432;Database=lifedrop_test_ci;Username=test_user;Password=test_pass;Include Error Detail=true'
$env:TEST_SUPERUSER_DATABASE_URL = 'Host=localhost;Port=5432;Database=lifedrop_test_ci;Username=postgres;Password=temp_root;Include Error Detail=true'

Step 'Database tests' { dotnet test tests\LifeDrop.Database.Tests }
Step '.NET unit tests' { dotnet test LifeDrop.CI.slnf }
Push-Location src\LifeDrop.Web
try {
    if (-not (Test-Path node_modules)) { Step 'npm ci' { npm ci } }
    Step 'Panel lint' { npm run lint }
    Step 'Panel typecheck' { npm run typecheck }
    Step 'Panel tests' { npm test }
    Step 'Panel build' { npm run build }
} finally { Pop-Location }
if ($Android) { Step 'Android build' { dotnet build src\LifeDrop.Mobile\LifeDrop.Mobile.csproj -f net10.0-android } }
Write-Host "`nAll checks passed." -ForegroundColor Green
