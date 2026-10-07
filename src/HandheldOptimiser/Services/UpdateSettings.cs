using System.IO;
using System.Security;
using Microsoft.Win32;

namespace HandheldOptimiser.Services;

/// <summary>Everything the Updates page installs through, and what it remembers.</summary>
public sealed record UpdateSources(WindowsUpdates Windows, StoreUpdates Store, WingetUpdates Winget, UpdateSettings Settings)
{
    public static UpdateSources Create(LogService log, PowerShellRunner runner) =>
        new(new WindowsUpdates(log, runner), new StoreUpdates(log, runner), new WingetUpdates(log, runner), new UpdateSettings(log));
}

/// <summary>An update the user chose to skip, kept with its name so it can be listed without checking again.</summary>
public sealed record SkippedUpdate(string SkipKey, string Name);

/// <summary>
/// What the Updates page remembers between runs: the updates the user skipped, and whether to list
/// driver updates. Kept under HKLM like the undo journal, where only administrators can write.
/// </summary>
public sealed class UpdateSettings(LogService log)
{
    private const string Key = @"SOFTWARE\HandheldOptimiser\Updates";
    private const string SkippedValue = "Skipped";
    private const string DriversValue = "IncludeDrivers";

    private readonly LogService _log = log;

    public IReadOnlyList<SkippedUpdate> LoadSkipped()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return (key?.GetValue(SkippedValue) as string[] ?? [])
                .Select(line => line.Split('\t', 2))
                .Where(parts => parts.Length == 2 && parts[0].Length > 0)
                .Select(parts => new SkippedUpdate(parts[0], parts[1]))
                .ToList();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warning($"Could not read the skipped updates: {ex.Message}");
            return [];
        }
    }

    public void SaveSkipped(IEnumerable<SkippedUpdate> skipped) =>
        Write(SkippedValue, skipped.Select(s => $"{s.SkipKey}\t{s.Name}").ToArray(), RegistryValueKind.MultiString);

    public bool LoadIncludeDrivers()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return key?.GetValue(DriversValue) is int value && value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void SaveIncludeDrivers(bool include) => Write(DriversValue, include ? 1 : 0, RegistryValueKind.DWord);

    private void Write(string name, object value, RegistryValueKind kind)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(Key);
            key.SetValue(name, value, kind);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warning($"Could not save the update settings: {ex.Message}");
        }
    }
}
