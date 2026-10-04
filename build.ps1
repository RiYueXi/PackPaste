param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build-home'
$env:APPDATA = Join-Path $PSScriptRoot '.build-home\AppData'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Run-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" } }
Run-Dotnet restore PackPaste\PackPaste.csproj -r win-x64 '-p:PublishSingleFile=true' --configfile NuGet.Config
Run-Dotnet publish PackPaste\PackPaste.csproj -c $Configuration -r win-x64 --self-contained false '-p:PublishSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' --no-restore -o artifacts\app
Copy-Item LICENSE artifacts\app\LICENSE -Force
Compress-Archive -Path artifacts\app\* -DestinationPath PackPaste.Setup\payload.zip -Force
Run-Dotnet restore PackPaste.Setup\PackPaste.Setup.csproj -r win-x64 '-p:PublishSingleFile=true' --configfile NuGet.Config
Run-Dotnet publish PackPaste.Setup\PackPaste.Setup.csproj -c $Configuration -r win-x64 --self-contained false '-p:PublishSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false' --no-restore -o artifacts\setup
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item artifacts\setup\PackPaste.Setup.exe dist\PackPaste-1.0.1-win-x64-Setup.exe -Force
$hash = Get-FileHash dist\PackPaste-1.0.1-win-x64-Setup.exe -Algorithm SHA256
($hash.Hash + '  PackPaste-1.0.1-win-x64-Setup.exe') | Set-Content -Encoding ascii dist\SHA256SUMS.txt
Copy-Item README.md dist\使用说明.md -Force
Copy-Item LICENSE dist\LICENSE -Force
$hash | Format-List
