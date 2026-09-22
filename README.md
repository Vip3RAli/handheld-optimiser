# handheld-optimiser
Handheld Optimiser

## Building the installer

Requires the .NET 8 SDK and Inno Setup 6:

```powershell
winget install --id JRSoftware.InnoSetup -e --scope user
.\installer\build-installer.ps1
```

This publishes a self-contained build (no .NET runtime needed on the target device) and writes
`artifacts\HandheldOptimiser-Setup-<version>.exe`. The version comes from `<Version>` in
`src\HandheldOptimiser\HandheldOptimiser.csproj`; bump it there before building a new release.
