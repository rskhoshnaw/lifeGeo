$ErrorActionPreference = 'Stop'
$container = 'lifedrop-role-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$env:POSTGRES_PASSWORD = [guid]::NewGuid().ToString('N')

try {
    docker run --rm -d --name $container -e POSTGRES_PASSWORD `
        -e POSTGRES_DB=lifedrop_test_role -p '127.0.0.1::5432' postgres:16-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start PostgreSQL test container.' }

    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        docker exec $container pg_isready -U postgres -d lifedrop_test_role | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'PostgreSQL did not become ready.' }

    $portLine = docker port $container '5432/tcp'
    $port = [int](($portLine | Select-Object -First 1) -split ':')[-1]
    $env:TEST_DATABASE_URL = "Host=127.0.0.1;Port=$port;Database=lifedrop_test_role;Username=postgres;Password=$env:POSTGRES_PASSWORD"
    $root = Split-Path -Parent $PSScriptRoot
    dotnet test (Join-Path $root 'tests/LifeDrop.Database.Tests/LifeDrop.Database.Tests.csproj') -c Release -m:1 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Role-assignment integration test failed.' }
}
finally {
    Remove-Item Env:TEST_DATABASE_URL -ErrorAction SilentlyContinue
    Remove-Item Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue
    docker rm -f $container 2>$null | Out-Null
}
