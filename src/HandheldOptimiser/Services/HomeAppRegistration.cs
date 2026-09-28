using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public enum HomeAppTarget
{
    Steam,
    Library,
    ArmouryCrate,
    Custom
}

/// <summary>
/// Makes Handheld Optimiser selectable as the Windows 11 full screen experience home app, and stores which
/// app the launcher should open.
///
/// Windows builds the "Choose home app" list from packages that declare a windows.gamingApp extension
/// and the gamingHome capability. installer\HomeApp holds a registration-only package that does this
/// for HandheldOptimiser.HomeLauncher.exe.
///
/// The package is signed with a self-signed certificate that is trusted only while it is being added
/// (see <see cref="RegisterAsync"/>), so no certificate stays on the machine. It must be signed: Windows
/// re-checks an unsigned package's capability file at every sign-in, which fails once developer mode is
/// off again, leaving full screen mode on a blank screen.
/// </summary>
public static class HomeAppRegistration
{
    public const string PackageName = "HandheldOptimiser.HomeApp";

    /// <summary>Must match Identity Version in installer\HomeApp\AppxManifest.xml.</summary>
    public const string PackageVersion = "0.2.1.0";

    /// <summary>
    /// Derived by Windows from the Publisher in installer\HomeApp\AppxManifest.xml ("CN=Handheld
    /// Optimiser"); it changes if that Publisher changes. It depends only on the name, not the key, so a
    /// replacement signing certificate with the same subject keeps it.
    /// </summary>
    public const string PublisherId = "pgc7cpy78bqbw";

    public const string AppUserModelId = $"{PackageName}_{PublisherId}!HomeApp";

    /// <summary>The unsigned registration used by 0.2.0 to 0.4.0, which did not survive a restart.</summary>
    public const string LegacyAppUserModelId = $"{PackageName}_n7ggsqt1rt3jm!HomeApp";

    public const string GamingConfigurationKey = @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration";
    public const string HomeAppValue = "GamingHomeApp";
    public const string StartupValue = "StartupToGamingHome";

    // Read by HandheldOptimiser.HomeLauncher; keep the names in step with its Program.cs.
    private const string SettingsKey = @"Software\HandheldOptimiser\HomeApp";

    private const string ArmouryCratePackagePrefix = "B9ECED6F.ArmouryCrateSE_";

    // Per-user list of installed packages. Reading it avoids starting PowerShell just to ask.
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd('\\');

    public static string PackagePath => Path.Combine(InstallDirectory, "HomeApp", "HomeApp.msix");

    /// <summary>Public half of the certificate the package is signed with, trusted only while registering.</summary>
    public static string CertificatePath => Path.Combine(InstallDirectory, "HomeApp", "HomeApp.cer");

    // The package resolves its logos and resources.pri from the install folder, so those must be there too.
    public static bool LauncherPresent =>
        File.Exists(Path.Combine(InstallDirectory, "HandheldOptimiser.HomeLauncher.exe"))
        && File.Exists(PackagePath)
        && File.Exists(CertificatePath)
        && File.Exists(Path.Combine(InstallDirectory, "resources.pri"));

    /// <summary>
    /// Same test AnyFSE uses: the API set only exists on builds that have the full screen experience.
    /// </summary>
    public static bool IsFullScreenExperienceAvailable()
    {
        if (!NativeLibrary.TryLoad("api-ms-win-gaming-experience-l1-1-0.dll", out var handle))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(handle, "RegisterGamingFullScreenExperienceChangeNotification", out _);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>
    /// True only for the signed package bundled with this build. An older registration (stale logos, or
    /// the unsigned one that breaks on restart) counts as not registered, so the next apply replaces it.
    /// </summary>
    public static bool IsRegistered() => HasPackage($"{PackageName}_{PackageVersion}_x64__{PublisherId}");

    public static bool IsAnyVersionRegistered() => HasPackage(PackageName + "_");

    public static bool IsArmouryCrateInstalled() => HasPackage(ArmouryCratePackagePrefix);

    public static bool IsSteamInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamExe") is string exe && File.Exists(exe);
    }

    public static bool IsCurrentHomeApp() => string.Equals(ReadHomeApp(), AppUserModelId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Still pointed at the unsigned registration from an older version, which needs replacing.</summary>
    public static bool IsLegacyHomeApp() => string.Equals(ReadHomeApp(), LegacyAppUserModelId, StringComparison.OrdinalIgnoreCase);

    private static string? ReadHomeApp()
    {
        using var key = Registry.CurrentUser.OpenSubKey(GamingConfigurationKey);
        return key?.GetValue(HomeAppValue) as string;
    }

    private static bool HasPackage(string prefix)
    {
        using var repo = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);
        return repo?.GetSubKeyNames().Any(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static (HomeAppTarget Target, string CustomPath, string CustomArgs) LoadChoice()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);

        var target = (key?.GetValue("Target") as string)?.ToLowerInvariant() switch
        {
            "library" => HomeAppTarget.Library,
            "armourycrate" => HomeAppTarget.ArmouryCrate,
            "custom" => HomeAppTarget.Custom,
            _ => HomeAppTarget.Steam
        };

        return (target, key?.GetValue("CustomPath") as string ?? string.Empty, key?.GetValue("CustomArgs") as string ?? string.Empty);
    }

    /// <summary>
    /// The launcher's own preference, not a system setting, so it is written directly rather than
    /// journalled: it only changes which app our launcher opens.
    /// </summary>
    public static void SaveChoice(HomeAppTarget target, string customPath, string customArgs)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("Target", target switch
        {
            HomeAppTarget.Library => "library",
            HomeAppTarget.ArmouryCrate => "armourycrate",
            HomeAppTarget.Custom => "custom",
            _ => "steam"
        });
        key.SetValue("CustomPath", customPath);
        key.SetValue("CustomArgs", customArgs);
    }

    private const string AppModelUnlockKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock";
    private const string DeveloperModeValue = "AllowDevelopmentWithoutDevLicense";

    // Developer mode's value from before a registration, kept until it has been put back. In HKLM so
    // only an administrator can plant or change it, since the next start acts on it.
    private const string PendingKey = @"SOFTWARE\HandheldOptimiser";
    private const string PendingDeveloperModeValue = "DeveloperModeBeforeRegistration";
    private const string DeveloperModeWasAbsent = "absent";

    // Thumbprint of the signing certificate while it is temporarily trusted.
    private const string PendingCertificateValue = "CertificateTrustedForRegistration";

    private const string SigningSubject = "CN=Handheld Optimiser";

    /// <summary>
    /// Registers the package against the install folder.
    ///
    /// Windows only accepts the package's self-signed capability file (Catalog FFFF) while developer mode
    /// is on, and a signed package only while its certificate is trusted. Both are checked at install
    /// time only, so developer mode is switched on and the certificate trusted for this one
    /// Add-AppxPackage call, then both are put back. That is done here rather than in the script's own
    /// finally block, which never runs if PowerShell is killed. Both are also written down first, so if
    /// this app is closed or crashes midway, <see cref="RestoreIfInterrupted"/> puts them back on the next
    /// start.
    /// </summary>
    public static async Task<bool> RegisterAsync(TweakContext ctx, CancellationToken ct)
    {
        if (!LauncherPresent)
        {
            ctx.Log.Error($"Home app files are missing from {InstallDirectory}. Reinstall Handheld Optimiser.");
            return false;
        }

        using var certificate = LoadSigningCertificate(ctx.Log);
        if (certificate is null)
        {
            return false;
        }

        var before = ctx.Registry.ReadValue(RegistryRoot.LocalMachine, AppModelUnlockKey, DeveloperModeValue);
        SetPendingDeveloperMode(before is int value ? value.ToString() : DeveloperModeWasAbsent);

        var devModeSnapshot = ctx.Registry.WriteValue(new RegistryValueSpec(
            RegistryRoot.LocalMachine, AppModelUnlockKey, DeveloperModeValue, 1, RegistryValueKind.DWord));

        if (devModeSnapshot is null)
        {
            ClearPendingDeveloperMode();
            ctx.Log.Error("Could not switch developer mode on, which Windows needs to register the home app.");
            return false;
        }

        var trustedByUs = false;
        try
        {
            trustedByUs = TrustTemporarily(certificate, ctx.Log);

            var package = PackagePath.Replace("'", "''");
            var location = InstallDirectory.Replace("'", "''");

            var script = $$"""
                $ErrorActionPreference = 'Stop'
                try {
                    # A previous registration may point at an older install folder, or be the unsigned one
                    # from an older version; both are replaced.
                    Get-AppxPackage -Name '{{PackageName}}' | Remove-AppxPackage

                    Add-AppxPackage -Path '{{package}}' -ExternalLocation '{{location}}'
                    Write-Output 'RESULT=OK'
                }
                catch {
                    Write-Output "RESULT=FAIL $($_.Exception.Message)"
                }
                """;

            var outcome = await ctx.Runner.RunScriptAsync(script, "Register Handheld Optimiser as a home app", ct, echoScript: false);
            return outcome.StdOut.Contains("RESULT=OK", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            ctx.Log.Error($"Could not trust the home app's signing certificate: {ex.Message}");
            return false;
        }
        finally
        {
            // Each is left pending if putting it back fails, so the next start tries again.
            if (trustedByUs && RemoveTrust(certificate.Thumbprint, ctx.Log))
            {
                ClearPending(PendingCertificateValue);
            }

            if (ctx.Registry.RestoreSnapshot(devModeSnapshot))
            {
                ClearPendingDeveloperMode();
            }
        }
    }

    /// <summary>The bundled certificate, refused unless it is ours: it is about to be trusted machine-wide.</summary>
    private static X509Certificate2? LoadSigningCertificate(LogService log)
    {
        try
        {
            var certificate = new X509Certificate2(CertificatePath);
            if (certificate.Subject == SigningSubject)
            {
                return certificate;
            }

            log.Error($"The home app certificate is for \"{certificate.Subject}\", not \"{SigningSubject}\". Refusing to trust it.");
            certificate.Dispose();
            return null;
        }
        catch (CryptographicException ex)
        {
            log.Error($"Could not read the home app certificate: {ex.Message}");
            return null;
        }
    }

    /// <returns>True when the certificate was added here, and so must be removed again.</returns>
    private static bool TrustTemporarily(X509Certificate2 certificate, LogService log)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        // Someone who already trusts it keeps it; only a certificate added here is removed afterwards.
        if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
        {
            return false;
        }

        SetPending(PendingCertificateValue, certificate.Thumbprint);
        store.Add(certificate);
        log.Info("Trusted the home app signing certificate for registration.");
        return true;
    }

    private static bool RemoveTrust(string thumbprint, LogService log)
    {
        try
        {
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var match in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
            {
                store.Remove(match);
                match.Dispose();
            }

            log.Info("Removed the home app signing certificate again.");
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            log.Warning($"Could not remove the home app signing certificate yet ({ex.Message}); retrying at next start.");
            return false;
        }
    }

    /// <summary>
    /// Called at startup. If a registration was cut short, removes the signing certificate if it is still
    /// trusted, and puts developer mode back to the value recorded before the registration began.
    /// </summary>
    public static void RestoreIfInterrupted(RegistryHelper registry, LogService log)
    {
        if (ReadPending(PendingCertificateValue) is { } thumbprint)
        {
            log.Warning("A home app registration did not finish last time. Removing its signing certificate.");
            if (RemoveTrust(thumbprint, log))
            {
                ClearPending(PendingCertificateValue);
            }
        }

        var pending = ReadPending(PendingDeveloperModeValue);
        if (pending is null)
        {
            return;
        }

        var wasAbsent = pending == DeveloperModeWasAbsent;
        if (!wasAbsent && !int.TryParse(pending, out _))
        {
            log.Warning($"Ignoring an unreadable developer mode record (\"{pending}\").");
            ClearPendingDeveloperMode();
            return;
        }

        log.Warning("A home app registration did not finish last time. Putting developer mode back as it was.");

        var snapshot = new RegistryValueSnapshot
        {
            Root = RegistryRoot.LocalMachine,
            SubKey = AppModelUnlockKey,
            ValueName = DeveloperModeValue,
            KeyExisted = true,
            ValueExisted = !wasAbsent,
            OriginalKind = RegistryValueKind.DWord,
            OriginalValue = wasAbsent ? null : pending
        };

        if (registry.RestoreSnapshot(snapshot))
        {
            ClearPendingDeveloperMode();
        }
    }

    private static void SetPendingDeveloperMode(string value) => SetPending(PendingDeveloperModeValue, value);

    private static void ClearPendingDeveloperMode() => ClearPending(PendingDeveloperModeValue);

    private static string? ReadPending(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(PendingKey);
        return key?.GetValue(name) as string;
    }

    private static void SetPending(string name, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(PendingKey);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void ClearPending(string name)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(PendingKey, writable: true))
        {
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        // Only there to hold the record; removed once it is empty so nothing is left behind.
        bool empty;
        using (var check = Registry.LocalMachine.OpenSubKey(PendingKey))
        {
            empty = check is not null && check.ValueCount == 0 && check.SubKeyCount == 0;
        }

        if (empty)
        {
            Registry.LocalMachine.DeleteSubKey(PendingKey, throwOnMissingSubKey: false);
        }
    }

    public static async Task<bool> UnregisterAsync(TweakContext ctx, CancellationToken ct)
    {
        const string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Get-AppxPackage -Name '{{PackageName}}' | Remove-AppxPackage
                Write-Output 'RESULT=OK'
            }
            catch {
                Write-Output "RESULT=FAIL $($_.Exception.Message)"
            }
            """;

        var outcome = await ctx.Runner.RunScriptAsync(script, "Remove Handheld Optimiser from the home app list", ct, echoScript: false);
        return outcome.StdOut.Contains("RESULT=OK", StringComparison.Ordinal);
    }
}
