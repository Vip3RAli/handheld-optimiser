using System.Collections.ObjectModel;
using System.IO;
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
/// </summary>
public sealed class LogService
{
    private readonly object _fileLock = new();
    private readonly string _logFilePath;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public string LogFilePath => _logFilePath;

    public LogService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandheldOptimiser", "logs");
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
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
            dispatcher.Invoke(AddToUi);
        }

        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_logFilePath,
                    $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{severity}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Losing a log line must never take down an operation that is midway through
                // modifying the system.
            }
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
