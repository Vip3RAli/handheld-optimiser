# Publishes Handheld Optimiser as a self-contained build and wraps it in an Inno Setup installer.
# Output: artifacts\HandheldOptimiser-Setup-<version>.exe
#
# Requires Inno Setup 6: winget install --id JRSoftware.InnoSetup -e --scope user

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\HandheldOptimiser\HandheldOptimiser.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$script = Join-Path $PSScriptRoot 'HandheldOptimiser.iss'

# The csproj is the single source of truth for the version.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install --id JRSoftware.InnoSetup -e --scope user' }

Write-Host "Building Handheld Optimiser $version" -ForegroundColor Cyan

# Start clean so files deleted from the project do not linger in the installer.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# Self-contained: a fresh handheld may not have the .NET 8 Desktop Runtime.
# ReadyToRun: precompiled code starts noticeably faster on a handheld CPU.
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $root "artifacts\HandheldOptimiser-Setup-$version.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
