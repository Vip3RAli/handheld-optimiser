using System.IO;
using System.Runtime.CompilerServices;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Runs once as the tests load, before any of them. The library writes through Program.Log, and tests that
/// feed it a bad key or a plain one on purpose had their lines land in the player's own home-launcher.log,
/// where "Could not decrypt a setting" read as a fault on every test run.
/// </summary>
internal static class TestLogSetup
{
    [ModuleInitializer]
    internal static void SendLibraryLogElsewhere() =>
        HomeLauncher.Program.LogPath = Path.Combine(Path.GetTempPath(), "HandheldOptimiser.Tests", "home-launcher.log");
}
