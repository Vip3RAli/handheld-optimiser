using System.Diagnostics;
using System.IO;
using System.Text;

namespace HandheldOptimiser.Services;

public sealed record ProcessOutcome(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;

    public IEnumerable<string> OutputLines =>
        StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
}

/// <summary>
/// Runs PowerShell and other console tools in a hidden window, streaming both pipes into the log so the
/// user can see exactly what was executed and what came back.
/// </summary>
public sealed class PowerShellRunner(LogService log)
{
    private readonly LogService _log = log;

    /// <summary>
    /// Executes a PowerShell script block. The script is passed via a temp file rather than
    /// -EncodedCommand so that the log shows readable source and quoting stops being a hazard.
    /// </summary>
    public async Task<ProcessOutcome> RunScriptAsync(
        string script,
        string description,
        CancellationToken ct = default,
        bool echoScript = true)
    {
        _log.Command($"{description}");

        if (echoScript)
        {
            foreach (var line in script.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    _log.Trace($"    {trimmed}");
                }
            }
        }

        var tempScript = Path.Combine(Path.GetTempPath(), $"ho-{Guid.NewGuid():N}.ps1");

        try
        {
            // UTF-8 with BOM so PowerShell 5.1 reads non-ASCII correctly.
            await File.WriteAllTextAsync(tempScript, script, new UTF8Encoding(true), ct);

            return await RunProcessAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tempScript],
                description,
                ct,
                echoCommand: false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempScript))
                {
                    File.Delete(tempScript);
                }
            }
            catch (IOException)
            {
                // Temp file cleanup is best effort.
            }
        }
    }

    public async Task<ProcessOutcome> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken ct = default,
        bool echoCommand = true,
        bool logOutput = true)
    {
        if (echoCommand)
        {
            _log.Command($"{description}");
            _log.Trace($"    {fileName} {string.Join(' ', arguments)}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);

            var shown = LastRedraw(e.Data);
            if (logOutput && !string.IsNullOrWhiteSpace(shown))
            {
                _log.Trace($"    {shown}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);

            var shown = LastRedraw(e.Data);
            if (logOutput && !string.IsNullOrWhiteSpace(shown))
            {
                _log.Warning($"    {shown}");
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _log.Error($"Could not start {fileName}: {ex.Message}");
            return new ProcessOutcome(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already gone.
            }

            throw;
        }

        return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// winget and DISM draw progress bars by rewriting one line with carriage returns. Only the final
    /// state of that line is worth showing; otherwise the console fills with every intermediate frame.
    /// </summary>
    private static string LastRedraw(string line)
    {
        var frames = line.Split('\r', StringSplitOptions.RemoveEmptyEntries);
        return frames.Length == 0 ? string.Empty : frames[^1].TrimEnd();
    }
}
