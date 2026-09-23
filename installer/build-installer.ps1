# Publishes Handheld Optimiser as a self-contained build and wraps it in an Inno Setup installer.
# Output: artifacts\HandheldOptimiser-Setup-<version>.exe
#
# Requires Inno Setup 6: winget install --id JRSoftware.InnoSetup -e --scope user

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\HandheldOptimiser\HandheldOptimiser.csproj'
$launcherProject = Join-Path $root 'src\HandheldOptimiser.HomeLauncher\HandheldOptimiser.HomeLauncher.csproj'
$homeAppDir = Join-Path $PSScriptRoot 'HomeApp'
$publishDir = Join-Path $root 'artifacts\publish'
$toolsDir = Join-Path $root 'artifacts\tools'
$script = Join-Path $PSScriptRoot 'HandheldOptimiser.iss'

# MakeAppx comes from Microsoft's SDK build tools package on NuGet, so no Windows SDK install is needed.
$buildToolsVersion = '10.0.28000.2705'

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

# The full screen home app launcher shares the folder and the bundled runtime.
dotnet publish $launcherProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed ($LASTEXITCODE)" }

# Registration-only package for the full screen home app list. See installer\HomeApp\AppxManifest.xml.
$makeAppx = Get-ChildItem (Join-Path $toolsDir "sdkbt.$buildToolsVersion") -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName

if (-not $makeAppx) {
    Write-Host "Downloading Windows SDK build tools $buildToolsVersion" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force $toolsDir | Out-Null
    $nupkg = Join-Path $toolsDir "sdkbt.$buildToolsVersion.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$buildToolsVersion/microsoft.windows.sdk.buildtools.$buildToolsVersion.nupkg" -OutFile $nupkg
    Expand-Archive $nupkg (Join-Path $toolsDir "sdkbt.$buildToolsVersion") -Force
    $makeAppx = Get-ChildItem (Join-Path $toolsDir "sdkbt.$buildToolsVersion") -Recurse -Filter makeappx.exe |
        Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName
}

# Build from a staging copy so the generated resources.pri never lands in the source tree.
$staging = Join-Path $root 'artifacts\homeapp-staging'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Copy-Item $homeAppDir $staging -Recurse

# resources.pri lets Windows pick the right logo size; without it Settings shows a blank icon.
$makePri = Join-Path (Split-Path $makeAppx -Parent) 'makepri.exe'
& $makePri new /pr $staging /cf (Join-Path $staging 'priconfig.xml') /mn (Join-Path $staging 'AppxManifest.xml') /of (Join-Path $staging 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakePri failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force (Join-Path $publishDir 'HomeApp') | Out-Null
& $makeAppx pack /d $staging /p (Join-Path $publishDir 'HomeApp\HomeApp.msix') /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed ($LASTEXITCODE)" }

# Because the package points at the program folder (AllowExternalContent), Windows reads the capability
# file, the logos and resources.pri from there rather than from inside the package. Without the logos
# here, Settings shows a blank icon for the home app.
Copy-Item (Join-Path $homeAppDir 'CustomCapability.SCCD') $publishDir
Copy-Item (Join-Path $staging 'Assets') $publishDir -Recurse -Force
Copy-Item (Join-Path $staging 'resources.pri') $publishDir

& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $root "artifacts\HandheldOptimiser-Setup-$version.exe"
Write-Host "Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green

# Sign for the in-app updater, which refuses any installer without a valid <installer>.sig. The key is
# managed with tools\UpdateSigner and lives outside the repository; a build without it still works, but
# that release can only be installed by hand.
$signer = Join-Path $root 'tools\UpdateSigner\UpdateSigner.csproj'
$signingKey = Join-Path $env:APPDATA 'HandheldOptimiser-Signing\update-signing-key.bin'

if (Test-Path $signingKey) {
    dotnet build $signer -c Release -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "UpdateSigner build failed ($LASTEXITCODE)" }

    Remove-Item "$setup.sig" -ErrorAction SilentlyContinue
    dotnet run --project $signer -c Release --no-build -- sign $setup $version
    if ($LASTEXITCODE -ne 0) { throw "Signing failed ($LASTEXITCODE)" }

    # Checked against the key compiled into the app, so a mismatched key fails here, not on users' machines.
    $updateService = Get-Content (Join-Path $root 'src\HandheldOptimiser\Services\UpdateService.cs') -Raw
    if ($updateService -notmatch 'SigningPublicKey = "([^"]+)"') { throw 'SigningPublicKey not found in UpdateService.cs' }
    dotnet run --project $signer -c Release --no-build -- verify $setup --public-key $Matches[1]
    if ($LASTEXITCODE -ne 0) { throw 'The signature does not match the public key built into the app.' }

    Write-Host "Signature: $setup.sig (upload it to the release beside the installer)" -ForegroundColor Green
}
else {
    Write-Warning "No update signing key at $signingKey, so the installer is not signed. The in-app updater will not install this release."
}
