# Runs the API against the local lifedrop_dev database as the restricted lifedrop_app role.
# Listens on every network interface, port 5256, so a phone on the same Wi-Fi can reach it too.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LocalDb.ps1')
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..'))
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__LifeDrop = "Host=localhost;Port=$LocalDbPort;Database=lifedrop_dev;Username=lifedrop_app;Password=lifedrop_app_local"
$env:Jwt__Issuer = 'lifedrop-local'
$env:Jwt__Audience = 'lifedrop-local'
$env:Jwt__SigningKey = 'local-development-signing-key-not-for-production-0001'
$env:Cors__AllowedOrigins__0 = 'http://localhost:5273'
dotnet run --project src\LifeDrop.API --urls 'http://0.0.0.0:5256'
