# Adds the TDP Engine Game Bar widget for the signed-in player. Run by the installer in three steps,
# because trusting the signing certificate needs administrator while the package must be added as the
# player, whose Game Bar it belongs to:
#   Trust    (elevated)  trusts the self-signed "CN=Handheld Optimiser" certificate. Exit 0 when it was
#                        added here, 2 when it was already trusted (and so must stay).
#   Add      (player)    adds the widget package with the frameworks it needs.
#   Untrust  (elevated)  removes the certificate again, so nothing stays trusted on the machine.
# Windows checks the certificate only while a package is added, so the widget keeps working after.
param(
    [Parameter(Mandatory)] [ValidateSet('Trust', 'Add', 'Untrust')] [string] $Step
)

$ErrorActionPreference = 'Stop'

# Only modules from Windows PowerShell's own folder, never from anywhere the player can write to.
$env:PSModulePath = $PSHOME + '\Modules'

$folder = $PSScriptRoot
$certificatePath = Join-Path (Split-Path $folder -Parent) 'HomeApp\HomeApp.cer'
$subject = 'CN=Handheld Optimiser'

function Open-TrustedPeople {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'LocalMachine')
    $store.Open('ReadWrite')
    return $store
}

try {
    $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
    if ($certificate.Subject -ne $subject) {
        Write-Output "The certificate is for $($certificate.Subject), not $subject. Refusing to trust it."
        exit 1
    }

    switch ($Step) {
        'Trust' {
            $store = Open-TrustedPeople
            try {
                if ($store.Certificates.Find('FindByThumbprint', $certificate.Thumbprint, $false).Count -gt 0) { exit 2 }
                $store.Add($certificate)
                exit 0
            }
            finally { $store.Close() }
        }
        'Add' {
            $dependencies = @(Get-ChildItem (Join-Path $folder 'Dependencies') -Filter *.appx -ErrorAction SilentlyContinue |
                ForEach-Object FullName)
            $package = Join-Path $folder 'GameBar.msix'

            # An upgrade in place keeps the widget in Game Bar; removing it first makes Game Bar forget it.
            # Each local build has a higher version (see build-installer.ps1). The same version again or an
            # older one is refused, and only then does the old copy go first.
            try {
                Add-AppxPackage -Path $package -DependencyPath $dependencies -ForceApplicationShutdown
            }
            catch {
                Get-AppxPackage -Name 'HandheldOptimiser.GameBar' | Remove-AppxPackage
                Add-AppxPackage -Path $package -DependencyPath $dependencies -ForceApplicationShutdown
            }

            # Game Bar only reads the OEM widget and manifest placement when it starts, so a running copy
            # is closed and the next press of the button starts it fresh.
            Get-Process -Name 'GameBar' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            exit 0
        }
        'Untrust' {
            $store = Open-TrustedPeople
            try {
                foreach ($match in $store.Certificates.Find('FindByThumbprint', $certificate.Thumbprint, $false)) {
                    $store.Remove($match)
                }
                exit 0
            }
            finally { $store.Close() }
        }
    }
}
catch {
    Write-Output $_.Exception.Message
    exit 1
}
