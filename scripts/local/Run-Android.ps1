# Builds the app and installs it on the Android phone connected by USB (USB debugging on), Debug build.
# adb reverse lets the phone reach the local API at http://127.0.0.1:5256, the app's default address in Debug.
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..\..'))
adb reverse tcp:5256 tcp:5256
if ($LASTEXITCODE -ne 0) { throw 'adb reverse failed: is the phone connected and USB debugging allowed?' }
dotnet build src\LifeDrop.Mobile\LifeDrop.Mobile.csproj -t:Run -f net10.0-android
