using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public enum HomeAppTarget
{
    Steam,
    ArmouryCrate,
    Custom
}

/// <summary>
/// Makes Handheld Optimiser selectable as the Windows 11 full screen experience home app, and stores which
/// app the launcher should open.
///
/// Windows builds the "Choose home app" list from packages that declare a windows.gamingApp extension
/// and the gamingHome capability. installer\HomeApp holds a registration-only package that does this
/// for HandheldOptimiser.HomeLauncher.exe. It is unsigned, so no certificate is added to the machine.
/// </summary>
public static class HomeAppRegistration
{
    public const string PackageName = "HandheldOptimiser.HomeApp";

    /// <summary>Must match Identity Version in installer\HomeApp\AppxManifest.xml.</summary>
    public const string PackageVersion = "0.1.2.0";

    /// <summary>
    /// The suffix is derived by Windows from the Publisher in installer\HomeApp\AppxManifest.xml; it
    /// changes if that Publisher changes.
    /// </summary>
    public const string AppUserModelId = "HandheldOptimiser.HomeApp_n7ggsqt1rt3jm!HomeApp";

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

    // The package resolves its logos and resources.pri from the install folder, so those must be there too.
    public static bool LauncherPresent =>
        File.Exists(Path.Combine(InstallDirectory, "HandheldOptimiser.HomeLauncher.exe"))
        && File.Exists(PackagePath)
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
    /// True only for the version bundled with this build. An older registration still works but carries
    /// stale logos, so it counts as not registered and the next apply replaces it.
    /// </summary>
    public static bool IsRegistered() => HasPackage($"{PackageName}_{PackageVersion}_");

    public static bool IsAnyVersionRegistered() => HasPackage(PackageName + "_");

    public static bool IsArmouryCrateInstalled() => HasPackage(ArmouryCratePackagePrefix);

    public static bool IsSteamInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamExe") is string exe && File.Exists(exe);
    }

    public static bool IsCurrentHomeApp()
    {
        using var key = Registry.CurrentUser.OpenSubKey(GamingConfigurationKey);
        return string.Equals(key?.GetValue(HomeAppValue) as string, AppUserModelId, StringComparison.OrdinalIgnoreCase);
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
            HomeAppTarget.ArmouryCrate => "armourycrate",
            HomeAppTarget.Custom => "custom",
            _ => "steam"
        });
        key.SetValue("CustomPath", customPath);
        key.SetValue("CustomArgs", customArgs);
    }

    /// <summary>
    /// Registers the package against the install folder.
    ///
    /// Windows only accepts the package's unsigned capability file (Catalog FFFF) while developer mode
    /// is on, and only checks it at install time. So developer mode is switched on for this one
    /// Add-AppxPackage call and put back to its previous value in a finally block, whatever happens.
    /// </summary>
    public static async Task<bool> RegisterAsync(TweakContext ctx, CancellationToken ct)
    {
        if (!LauncherPresent)
        {
            ctx.Log.Error($"Home app files are missing from {InstallDirectory}. Reinstall Handheld Optimiser.");
            return false;
        }

        var package = PackagePath.Replace("'", "''");
        var location = InstallDirectory.Replace("'", "''");

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
            $name = 'AllowDevelopmentWithoutDevLicense'
            $before = (Get-ItemProperty $key -Name $name -ErrorAction SilentlyContinue).$name

            try {
                # A previous registration may point at an older install folder.
                Get-AppxPackage -Name '{{PackageName}}' | Remove-AppxPackage

                if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null }
                Set-ItemProperty $key -Name $name -Value 1 -Type DWord
                Write-Output 'Developer mode switched on for registration'

                Add-AppxPackage -Path '{{package}}' -ExternalLocation '{{location}}' -AllowUnsigned
                Write-Output 'RESULT=OK'
            }
            catch {
                Write-Output "RESULT=FAIL $($_.Exception.Message)"
            }
            finally {
                if ($null -eq $before) { Remove-ItemProperty $key -Name $name -ErrorAction SilentlyContinue }
                else { Set-ItemProperty $key -Name $name -Value $before -Type DWord }
                Write-Output "Developer mode restored to previous value ($(if ($null -eq $before) { 'not set' } else { $before }))"
            }
            """;

        var outcome = await ctx.Runner.RunScriptAsync(script, "Register Handheld Optimiser as a home app", ct, echoScript: false);
        return outcome.StdOut.Contains("RESULT=OK", StringComparison.Ordinal);
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
