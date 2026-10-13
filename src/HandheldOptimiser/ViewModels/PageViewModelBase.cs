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

    /// <summary>
    /// Runs something that only reads, in the background. It does not take the busy gate, so the app
    /// stays usable while it runs and it can run beside other scans. <paramref name="what"/> is what
    /// is being checked ("updates"), shown at the bottom of the window until it finishes.
    /// </summary>
    Task RunScanAsync(string what, Func<CancellationToken, Task> scan);

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

    /// <summary>Whether this is the page on screen, which is what highlights its tab.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>
    /// Whether what this page shows comes from the internet. At start-up those pages load after the
    /// ones that only read this device, so the quick answers are not held up by the slow ones.
    /// </summary>
    public virtual bool LoadsFromInternet => false;

    /// <summary>
    /// Reads what the page shows, unless it has been read or is being read already. Every page is
    /// loaded once when the app starts, so opening one does not mean waiting for it.
    /// </summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when the page becomes visible. Loads the page if start-up did not get to it, which is
    /// the case when the app was opened only to install an update.
    /// </summary>
    public virtual Task OnNavigatedToAsync() => LoadAsync();

    /// <summary>Called when another page takes over. Used to stop anything that only runs while visible.</summary>
    public virtual void OnNavigatedFrom()
    {
    }
}
