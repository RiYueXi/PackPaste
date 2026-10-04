$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build-home'
$env:APPDATA = Join-Path $PSScriptRoot '.build-home\AppData'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (!(Test-Path artifacts\app\PackPaste.exe) -or !(Test-Path PackPaste.Setup\payload.zip)) { throw 'Run build.ps1 first.' }
dotnet restore PackPaste.Tests\PackPaste.Tests.csproj --configfile NuGet.Config
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet run --project PackPaste.Tests\PackPaste.Tests.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
