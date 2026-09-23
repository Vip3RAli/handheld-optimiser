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
    /// Reads the script from stdin as UTF-8 and runs it in the session's own scope, so it behaves as if
    /// it were a .ps1 run with -File: <c>exit N</c> sets the exit code, and finishing without one gives 0.
    /// Output is switched to UTF-8 so non-ASCII paths and user names come back intact; that can fail
    /// without a console, in which case output stays in the OEM code page as before.
    /// </summary>
    private const string StdinBootstrap =
        "try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }; " +
        "$r = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), (New-Object System.Text.UTF8Encoding $false)); " +
        "$s = $r.ReadToEnd(); " +
        ". ([scriptblock]::Create($s)); " +
        "exit 0";

    /// <summary>
    /// Executes a PowerShell script block, piped in over stdin.
    ///
    /// Not a temp file: this process is elevated, and anything unelevated running as the same user can
    /// write to %TEMP%, so a script file there could be swapped between being written and being run
    /// with administrator rights. Not -EncodedCommand either, which caps the script length and hides it
    /// from anyone reading a process list. The log still shows the readable source.
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

        return await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", StdinBootstrap],
            description,
            ct,
            echoCommand: false,
            standardInput: script);
    }

    public async Task<ProcessOutcome> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string description,
        CancellationToken ct = default,
        bool echoCommand = true,
        bool logOutput = true,
        string? standardInput = null)
    {
        if (echoCommand)
        {
            _log.Command($"{description}");
            _log.Trace($"    {fileName} {string.Join(' ', arguments)}");
        }

        var executable = TrustedExecutables.Resolve(fileName, out var untrusted);
        if (executable is null)
        {
            _log.Error($"Not running {fileName}: {untrusted}");
            return new ProcessOutcome(-1, string.Empty, untrusted ?? string.Empty);
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            // The current directory is wherever the app was launched from, possibly a folder the user
            // can write to; nothing started from here should look for files there.
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardInputEncoding = standardInput is not null ? new UTF8Encoding(false) : null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        psi.Environment["PSModulePath"] = TrustedExecutables.SystemModulePath;

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
            // Written after the output pipes are being drained, so a chatty process cannot deadlock the write.
            if (standardInput is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // The process exited before reading all of it; its exit code says what happened.
                }
            }

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
