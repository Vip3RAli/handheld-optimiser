using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// The runtimes the Game Runtimes page scans and installs. All are official Microsoft or vendor
/// packages from the winget community repository, which verifies each installer's hash before running it.
/// </summary>
public static class RuntimeCatalog
{
    public const string NetFx3Id = "windows.netfx3";

    public static IReadOnlyList<RuntimePackage> All { get; } =
    [
        .. VisualCpp("2005", "Needed by games from roughly 2005 to 2008."),
        .. VisualCpp("2008", "Needed by many games from 2008 to 2011."),
        .. VisualCpp("2010", "Needed by many games from 2010 to 2013."),
        .. VisualCpp("2012", "Needed by games from 2012 to 2014."),
        .. VisualCpp("2013", "Needed by games from 2013 to 2016."),
        .. VisualCpp("2015+", "Covers Visual C++ 2015 to 2022. Most modern games need it.",
            "2015-2022"),

        new()
        {
            Id = "Microsoft.DirectX",
            Name = "DirectX End-User Runtime (June 2010)",
            Description =
                "Legacy libraries that DirectX 9, 10 and 11 games still load. Not included in Windows 11.",
            Detection = RuntimeDetection.Files,
            FixedVersionLabel = "June 2010",
            DetectFiles =
            [
                @"%WINDIR%\System32\d3dx9_43.dll",
                @"%WINDIR%\System32\xinput1_3.dll",
                @"%WINDIR%\SysWOW64\d3dx9_43.dll",
                @"%WINDIR%\SysWOW64\xinput1_3.dll"
            ]
        },

        new()
        {
            Id = NetFx3Id,
            Name = ".NET Framework 3.5",
            Description =
                "Needed by older games, mod tools and launchers. Installed from Windows Update, which " +
                "can take a few minutes.",
            Detection = RuntimeDetection.NetFx3,
            FixedVersionLabel = "3.5"
        },

        .. DotNet("6", "Out of support since November 2024, but still worth updating for apps that need it."),
        .. DotNet("8", "Current long-term support release."),
        .. DotNet("9", "Current standard-support release."),

        new()
        {
            Id = "Microsoft.XNARedist",
            Name = "XNA Framework 4.0",
            Description = "Used by indie games built on Microsoft XNA, such as Terraria.",
            Group = RuntimeGroup.Legacy
        },
        new()
        {
            Id = "CreativeTechnology.OpenAL",
            Name = "OpenAL",
            Description = "3D audio library used by some older PC games and emulators.",
            Group = RuntimeGroup.Legacy
        },
        new()
        {
            Id = "Nvidia.PhysXLegacy",
            Name = "NVIDIA PhysX (Legacy)",
            Description =
                "Physics runtime for some games from 2008 to 2012, such as Mirror's Edge and Batman: " +
                "Arkham Asylum.",
            Group = RuntimeGroup.Legacy
        }
    ];

    private static IEnumerable<RuntimePackage> VisualCpp(string version, string description, string? label = null)
    {
        foreach (var arch in new[] { "x64", "x86" })
        {
            yield return new RuntimePackage
            {
                Id = $"Microsoft.VCRedist.{version}.{arch}",
                Name = $"Visual C++ {label ?? version} ({arch})",
                Description = arch == "x86"
                    ? $"{description} Required for 32-bit games."
                    : description
            };
        }
    }

    /// <summary>
    /// Update-only: winget reports these accurately, but installing a .NET version that nothing on the
    /// machine uses would just be clutter.
    /// </summary>
    private static IEnumerable<RuntimePackage> DotNet(string major, string note)
    {
        yield return new RuntimePackage
        {
            Id = $"Microsoft.DotNet.DesktopRuntime.{major}",
            Name = $".NET Desktop Runtime {major}",
            Description = $"Runs desktop apps and launchers built on .NET {major}. {note}",
            InstallIfMissing = false,
            Detection = RuntimeDetection.DotNet,
            DotNetFramework = "Microsoft.WindowsDesktop.App",
            DotNetMajor = major
        };

        yield return new RuntimePackage
        {
            Id = $"Microsoft.DotNet.Runtime.{major}",
            Name = $".NET Runtime {major}",
            Description = $"The base .NET {major} runtime used by tools and background apps. {note}",
            InstallIfMissing = false,
            Detection = RuntimeDetection.DotNet,
            DotNetFramework = "Microsoft.NETCore.App",
            DotNetMajor = major
        };
    }
}
