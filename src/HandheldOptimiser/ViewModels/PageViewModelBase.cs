using CommunityToolkit.Mvvm.ComponentModel;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// Services the shell provides to pages: exclusive execution, confirmation prompts and reboot tracking.
/// Keeps each page from reimplementing busy-state handling and lets the shell serialise operations, which
/// matters because two concurrent registry runs would corrupt the undo journal.
/// </summary>
public interface IShell
{
    bool IsBusy { get; }

    Task RunExclusiveAsync(string statusText, Func<IProgress<string>, CancellationToken, Task> work);

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    void NotifyRebootRequired();
}

public abstract partial class PageViewModelBase(IShell shell) : ObservableObject
{
    protected IShell Shell { get; } = shell;

    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph shown in the sidebar.</summary>
    public abstract string Glyph { get; }

    public virtual string Subtitle => string.Empty;

    /// <summary>Called when the page becomes visible. Used to lazily scan system state.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}
