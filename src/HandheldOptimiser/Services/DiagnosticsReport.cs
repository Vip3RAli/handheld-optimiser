using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

/// <summary>What a diagnostics report is built from, gathered once so the builder itself reads nothing.</summary>
public sealed record DiagnosticsFacts
{
    public required string AppVersion { get; init; }
    public required string Windows { get; init; }
    public required string Device { get; init; }
    public string? ProfileFolder { get; init; }
    public string? UserName { get; init; }
    public string? MachineName { get; init; }

    /// <summary>Every tweak for this device and what it was last found to be.</summary>
    public IReadOnlyList<(string Name, TweakState? State)> Tweaks { get; init; } = [];

    /// <summary>The names of the tweaks that have undo data.</summary>
    public IReadOnlyList<string> UndoEntries { get; init; } = [];

    /// <summary>Each log to include: its heading, and its text or why there is none.</summary>
    public IReadOnlyList<(string Heading, string Text)> Logs { get; init; } = [];
}

/// <summary>
/// The text behind Copy diagnostics: the version, the device, every tweak's state and the end of each
/// log, ready to paste into an issue on GitHub. It is text on the clipboard and not a file, because this
/// app runs as administrator and the folders a player could find a file in are ones any program can
/// rearrange. The player's name and the device's name are taken out, since an issue is public.
/// </summary>
public static class DiagnosticsReport
{
    /// <summary>GitHub turns down an issue or comment over 65,536 characters.</summary>
    public const int MaxLength = 60_000;

    /// <summary>How many lines are kept from the end of each log.</summary>
    public const int LogLines = 150;

    // The last part of a log file that is read at all. 150 lines fit many times over.
    private const int MaxLogBytes = 256 * 1024;

    private const string CutNote = "(earlier lines left out to fit)";

    public static string Build(DiagnosticsFacts facts)
    {
        var top = new StringBuilder();
        top.AppendLine("### Handheld Optimiser diagnostics");
        top.AppendLine();
        top.AppendLine($"- Version: {facts.AppVersion}");
        top.AppendLine($"- Windows: {facts.Windows}");
        top.AppendLine($"- Device: {facts.Device}");
        top.AppendLine();

        top.AppendLine("### Tweaks");
        top.AppendLine();
        foreach (var group in facts.Tweaks.GroupBy(t => StateText(t.State)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            top.AppendLine($"- {group.Key} ({group.Count()}): {string.Join("; ", group.Select(t => t.Name))}");
        }

        top.AppendLine($"- Undo data ({facts.UndoEntries.Count}): {(facts.UndoEntries.Count == 0 ? "none" : string.Join("; ", facts.UndoEntries))}");

        var head = Scrub(top.ToString(), facts);

        // The logs share what is left, so one long log cannot push the others out.
        var report = new StringBuilder(head);
        var room = facts.Logs.Count == 0 ? 0 : Math.Max(0, MaxLength - head.Length) / facts.Logs.Count;
        foreach (var (heading, text) in facts.Logs)
        {
            var title = $"\n### {heading}\n\n```\n";
            const string end = "\n```\n";
            var body = Fit(Scrub(Tail(text, LogLines), facts), room - title.Length - end.Length);
            report.Append(title).Append(body).Append(end);
        }

        return report.ToString();
    }

    public static string StateText(TweakState? state) => state switch
    {
        TweakState.Applied => "On",
        TweakState.NotApplied => "Off",
        TweakState.Partial => "Partly applied",
        TweakState.Error => "Could not be read",
        _ => "Not checked"
    };

    /// <summary>The last <paramref name="lines"/> lines of a text.</summary>
    public static string Tail(string text, int lines)
    {
        var all = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        return all.Length <= lines ? string.Join('\n', all) : string.Join('\n', all[^lines..]);
    }

    /// <summary>
    /// Shortens a text to <paramref name="max"/> characters by dropping whole lines from its start, as
    /// the newest lines of a log are the ones that say what went wrong.
    /// </summary>
    public static string Fit(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - CutNote.Length - 1;
        if (keep <= 0)
        {
            return CutNote;
        }

        var tail = text[^keep..];
        var firstBreak = tail.IndexOf('\n');
        return CutNote + "\n" + (firstBreak >= 0 ? tail[(firstBreak + 1)..] : tail);
    }

    /// <summary>
    /// Takes the player's profile folder, their account name and the device's name out of a text. The
    /// folder goes first, so that a path keeps its meaning as %USERPROFILE%.
    /// </summary>
    public static string Scrub(string text, DiagnosticsFacts facts) =>
        Scrub(text, facts.ProfileFolder, facts.UserName, facts.MachineName);

    public static string Scrub(string text, string? profileFolder, string? userName, string? machineName)
    {
        if (!string.IsNullOrWhiteSpace(profileFolder))
        {
            text = text.Replace(profileFolder.TrimEnd('\\'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        text = ReplaceWord(text, userName, "<user>");
        return ReplaceWord(text, machineName, "<device>");
    }

    /// <summary>
    /// Whole words only, and nothing shorter than three letters: a name such as "al" would otherwise
    /// take pieces out of ordinary words all through a log.
    /// </summary>
    private static string ReplaceWord(string text, string? word, string with)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Trim().Length < 3)
        {
            return text;
        }

        return Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{N}}])", with,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The end of a log file, or a line saying why it is not there. Never read through a junction or
    /// symbolic link: this runs as administrator, and a link planted in a folder the player owns could
    /// otherwise put a file only an administrator may read onto the clipboard.
    /// </summary>
    public static string ReadLogTail(string path)
    {
        try
        {
            if (SafetyGuard.FindLinkOnPath(path) is { } link)
            {
                return $"(not read: {link} is a junction or symbolic link)";
            }

            if (!File.Exists(path))
            {
                return "(no such log on this device)";
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(file.Length, MaxLogBytes);
            file.Seek(-length, SeekOrigin.End);
            var bytes = new byte[length];
            file.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes);

            // A read that began partway through the file began partway through a line too.
            if (file.Length > MaxLogBytes && text.IndexOf('\n') is >= 0 and var firstBreak)
            {
                text = text[(firstBreak + 1)..];
            }

            return text.Length == 0 ? "(empty)" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(not read: {ex.Message})";
        }
    }

    /// <summary>Windows' name, version and build, as Settings shows them.</summary>
    public static string WindowsText()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var version = Environment.OSVersion.Version;
        var display = Registry.GetValue(key, "DisplayVersion", null) as string;
        var revision = Registry.GetValue(key, "UBR", null) as int?;
        var edition = Registry.GetValue(key, "EditionID", null) as string;

        // The registry still says "Windows 10" on Windows 11, so the name comes from the build.
        var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        return string.Join(' ', new[] { name, edition, display, $"(build {version.Build}{(revision is { } r ? $".{r}" : string.Empty)})" }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>The maker, model and BIOS version, which Windows keeps in the registry from start-up.</summary>
    public static string DeviceText(HandheldDevice recognisedAs)
    {
        const string key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        var maker = Registry.GetValue(key, "SystemManufacturer", null) as string;
        var model = Registry.GetValue(key, "SystemProductName", null) as string;
        var bios = Registry.GetValue(key, "BIOSVersion", null) as string;

        var parts = new[] { maker, model }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim());
        return $"{string.Join(' ', parts)}{(string.IsNullOrWhiteSpace(bios) ? string.Empty : $", BIOS {bios.Trim()}")}, recognised as {recognisedAs}";
    }
}
