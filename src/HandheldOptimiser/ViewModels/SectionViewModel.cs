using CommunityToolkit.Mvvm.ComponentModel;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// One section of a goal's page: a list of tweaks, the startup programs, the health report and so on.
/// A page is its sections one under another, so a section has no title of its own, only the heading
/// the page draws over it.
/// </summary>
public abstract partial class SectionViewModel(IShell shell) : ObservableObject
{
    protected IShell Shell { get; } = shell;

    /// <summary>The heading drawn over the section. Empty where the page draws none.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>Said at the right of the heading, such as "4 of 4 on".</summary>
    public virtual string Summary => string.Empty;

    /// <summary>
    /// Whether what this section shows comes from the internet. At start-up those load after the ones
    /// that only read this device, so the quick answers are not held up by the slow ones.
    /// </summary>
    public virtual bool LoadsFromInternet => false;

    /// <summary>
    /// What this section has to say on its page's Dashboard card, or null when it has nothing to say
    /// (yet). The first section of a page that says something decides the card.
    /// </summary>
    public virtual CardStatus? Status => null;

    /// <summary>
    /// Reads what the section shows, unless it has been read or is being read already. Every section
    /// is loaded once when the app starts, so opening a page does not mean waiting for it.
    /// </summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Reads it again, for the page's Re-check.</summary>
    public virtual Task RefreshAsync() => LoadAsync();

    /// <summary>Called when the section's page comes on screen.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>Called when another page takes over. Used to stop anything that only runs while visible.</summary>
    public virtual void OnHidden()
    {
    }
}
