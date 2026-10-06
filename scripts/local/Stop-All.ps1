# Stops the API (port 5256), the panel (port 5273) and the local database container. The database data stays.
$ErrorActionPreference = 'Continue'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..'))
foreach ($port in 5256, 5273) {
    $connections = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
    foreach ($connection in $connections) {
        Write-Host "Stopping the program on port $port (process $($connection.OwningProcess))"
        Stop-Process -Id $connection.OwningProcess -Force -ErrorAction SilentlyContinue
    }
}
docker compose stop
Write-Host 'Stopped. (Close the black API and panel windows if they are still open.)'
