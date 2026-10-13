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

/// <summary>
/// Something the window can show: the Dashboard, or one goal's page. Every page but the Dashboard is
/// also a card on it, which is what <see cref="Glyph"/>, <see cref="Hint"/> and <see cref="Status"/> are for.
/// </summary>
public abstract partial class PageViewModelBase(IShell shell) : ObservableObject
{
    protected IShell Shell { get; } = shell;

    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph shown on the page's Dashboard card.</summary>
    public abstract string Glyph { get; }

    /// <summary>The card's last line: what is inside, said before the card is pressed.</summary>
    public virtual string Hint => string.Empty;

    /// <summary>What the page's card says about it, and the colour of its dot.</summary>
    public virtual CardStatus Status => CardStatus.Checking;

    /// <summary>
    /// Reads what the page shows, unless it has been read or is being read already. Everything is
    /// loaded once when the app starts, so opening a page does not mean waiting for it.
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
