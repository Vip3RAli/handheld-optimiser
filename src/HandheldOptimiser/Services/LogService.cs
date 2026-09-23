using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.Services;

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogSeverity Severity { get; init; }
    public string Message { get; init; } = string.Empty;

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string Prefix => Severity switch
    {
        LogSeverity.Command => ">",
        LogSeverity.Success => "OK",
        LogSeverity.Warning => "!",
        LogSeverity.Error => "X",
        LogSeverity.Trace => "·",
        _ => "-"
    };
}

/// <summary>
/// Single sink for everything the app does. The UI binds directly to <see cref="Entries"/>, and the
/// same lines are appended to a session file so a user can send a log after something goes wrong.
///
/// The session file lives in %LOCALAPPDATA%, where the user can find it, but that is also a folder any
/// unelevated process can rearrange, and this app writes there as administrator. <see cref="SessionFile"/>
/// checks every open by handle, so nothing is written anywhere but the intended file.
/// </summary>
public sealed class LogService
{
    // Held while a viewer has the file open exclusively. A viewer that never lets go must not grow
    // this without limit.
    private const int MaxPendingChars = 1_000_000;

    private readonly object _fileLock = new();
    private readonly string _logFilePath;
    private readonly StringBuilder _pending = new();
    private SessionFile? _file;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public string LogFilePath => _logFilePath;

    public LogService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandheldOptimiser", "logs");
        var stamp = $"session-{DateTime.Now:yyyyMMdd-HHmmss}";

        _file = SessionFile.Create(dir, stamp, out var problem);
        _logFilePath = _file?.Path ?? Path.Combine(dir, $"{stamp}.log");

        if (problem is not null)
        {
            Warning($"This session is not being saved to a log file: {problem}");
        }
    }

    public void Info(string message) => Write(LogSeverity.Info, message);
    public void Trace(string message) => Write(LogSeverity.Trace, message);
    public void Command(string message) => Write(LogSeverity.Command, message);
    public void Success(string message) => Write(LogSeverity.Success, message);
    public void Warning(string message) => Write(LogSeverity.Warning, message);
    public void Error(string message) => Write(LogSeverity.Error, message);

    public void Write(LogSeverity severity, string message)
    {
        var entry = new LogEntry { Severity = severity, Message = message };

        void AddToUi()
        {
            Entries.Add(entry);

            // The log view is a live console on a 7" screen; unbounded growth is the only way this
            // becomes a memory problem during a long debloat run.
            const int maxEntries = 5000;
            if (Entries.Count > maxEntries)
            {
                for (var i = 0; i < 500; i++)
                {
                    Entries.RemoveAt(0);
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AddToUi();
        }
        else
        {
            // Queue rather than block: winget and DISM emit lines fast enough that waiting on the UI
            // thread for each one stalls the worker. Posts from one thread keep their order.
            dispatcher.BeginInvoke(AddToUi);
        }

        string? lostReason = null;

        // Losing a log line must never take down an operation that is midway through modifying the
        // system, so nothing in here throws.
        lock (_fileLock)
        {
            if (_file is null)
            {
                return;
            }

            _pending.Append($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{severity}] {message}{Environment.NewLine}");

            switch (_file.TryAppend(_pending.ToString(), out var problem))
            {
                case AppendResult.Written:
                    _pending.Clear();
                    break;

                case AppendResult.Busy:
                    // Usually Notepad, which opens the file exclusively. Kept and written with the next line.
                    if (_pending.Length > MaxPendingChars)
                    {
                        _pending.Clear();
                        _pending.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{LogSeverity.Warning}] " +
                                        $"Some lines were not saved here while another program had this file open.{Environment.NewLine}");
                    }
                    break;

                default:
                    _file = null;
                    _pending.Clear();
                    lostReason = problem;
                    break;
            }
        }

        // Outside the lock, and with _file already cleared, so this goes to the screen only.
        if (lostReason is not null)
        {
            Warning($"Stopped saving this session to {_logFilePath}: {lostReason}");
        }
    }

    public void Clear()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Entries.Clear();
        }
        else
        {
            dispatcher.Invoke(Entries.Clear);
        }
    }
}
