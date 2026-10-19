using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// One health row as the page draws it: a mark, a title, and the answer in a few words. The longer
/// explanation sits beneath only where the row is not simply fine, so a healthy page stays short.
/// </summary>
public sealed class HealthRowViewModel(HealthCheck check, bool showGroup = false)
{
    public HealthCheck Check { get; } = check;

    public HealthStatus Status => Check.Status;
    public string Title => Check.Title;

    /// <summary>The row's group, shown beside the title where rows from several groups share a list.</summary>
    public string Tag => showGroup ? Check.Group : string.Empty;

    public string Value => Check.Summary ?? Check.Detail;

    /// <summary>The full explanation, where the short answer left something out worth reading.</summary>
    public string Note =>
        Check.Summary is not null && Check.Status is HealthStatus.Info or HealthStatus.Warning or HealthStatus.Bad
            ? Check.Detail
            : string.Empty;

    public string Detail => Check.Detail;
    public string Advice => Check.Advice ?? string.Empty;
    public string OpenText => Check.OpenText ?? string.Empty;

    public bool HasBar => Check.Used is not null;
    public double BarValue => (Check.Used ?? 0) * 100;
}

/// <summary>A row drawn as a small table: what a power plan holds, plugged in and on battery.</summary>
public sealed class HealthTableViewModel(HealthCheck check)
{
    public string Heading { get; } = check.Title;
    public IReadOnlyList<HealthTableRow> Rows { get; } = check.Table;
}

/// <summary>
/// A titled block of rows on the page. Two kinds fold away: a section that is a long list of things
/// that are fine (the protected components), and the power plans that are not in use.
/// </summary>
public sealed partial class HealthSectionViewModel : ObservableObject
{
    private readonly bool _foldsRows;
    private readonly string _foldedText;
    private readonly string _unfoldedText;

    public HealthSectionViewModel(
        string title,
        IReadOnlyList<HealthRowViewModel> rows,
        IReadOnlyList<HealthTableViewModel> tables,
        IReadOnlyList<HealthTableViewModel> moreTables,
        string note,
        bool foldsRows)
    {
        Title = title;
        Heading = title.ToUpperInvariant();
        Rows = rows;
        Tables = tables;
        MoreTables = moreTables;
        Note = note;
        _foldsRows = foldsRows;

        var attention = rows.Count(r => r.Status is HealthStatus.Warning or HealthStatus.Bad);
        var fine = rows.Count(r => r.Status == HealthStatus.Good);

        HasAttention = attention > 0;
        Summary = attention > 0 && foldsRows ? $"{attention} to look at, {fine} fine"
            : attention > 0 ? $"{attention} to look at"
            : foldsRows ? $"{fine} fine"
            : string.Empty;

        (_foldedText, _unfoldedText) = foldsRows
            ? ("Show all", "Hide")
            : (moreTables.Count == 1 ? "1 other plan" : $"{moreTables.Count} other plans", "Hide other plans");
    }

    public string Title { get; }
    public string Heading { get; }
    public string Summary { get; }
    public string Note { get; }
    public bool HasAttention { get; }

    public IReadOnlyList<HealthRowViewModel> Rows { get; }
    public IReadOnlyList<HealthTableViewModel> Tables { get; }
    public IReadOnlyList<HealthTableViewModel> MoreTables { get; }

    public bool CanToggle => _foldsRows || MoreTables.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isExpanded;

    public bool ShowRows => !_foldsRows || IsExpanded;
    public string ToggleText => IsExpanded ? _unfoldedText : _foldedText;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>The whole page: what needs a look, then the sections, then how many rows are fine.</summary>
public sealed record HealthPage(
    IReadOnlyList<HealthRowViewModel> Attention,
    IReadOnlyList<HealthSectionViewModel> Sections,
    int Fine);

/// <summary>
/// Arranges the health checks for reading at a glance. Anything yellow or red is lifted to the top
/// with its explanation, because on a healthy device that is one row in thirty. The rest is grouped
/// by subject, shortest answers first, and the protected components, which are nearly always all
/// fine, are folded into one line.
/// </summary>
public static class HealthLayout
{
    public const string ProtectedTitle = "Protected components";

    /// <summary>The groups with a section of their own, in page order. Every other group is a protected component.</summary>
    private static readonly (string Group, string Title)[] OwnSections =
    [
        ("Sleep", "Sleep"),
        ("Power plan", "Power plan"),
        ("Storage", "Storage"),
        ("Performance", "Performance")
    ];

    public static HealthPage Build(IReadOnlyList<HealthCheck> checks, string protectionNote)
    {
        var attention = checks
            .Where(c => c.Status is HealthStatus.Bad or HealthStatus.Warning)
            .OrderByDescending(c => c.Status == HealthStatus.Bad)
            .Select(c => new HealthRowViewModel(c, showGroup: true))
            .ToList();

        var sections = new List<HealthSectionViewModel>();

        foreach (var (group, title) in OwnSections)
        {
            var own = checks.Where(c => c.Group == group).ToList();
            if (own.Count == 0)
            {
                continue;
            }

            // A row with a table is a power plan. The first is the plan in use; the others fold away.
            var tables = own.Where(c => c.Table.Count > 0).Select(c => new HealthTableViewModel(c)).ToList();

            sections.Add(new HealthSectionViewModel(
                title,
                own.Where(c => c.Table.Count == 0).Select(c => new HealthRowViewModel(c)).ToList(),
                tables.Take(1).ToList(),
                tables.Skip(1).ToList(),
                tables.Count > 0 ? ProcessorPowerReport.VendorNote : string.Empty,
                foldsRows: false));
        }

        var protectedRows = checks
            .Where(c => OwnSections.All(s => s.Group != c.Group))
            .Select(c => new HealthRowViewModel(c, showGroup: true))
            .ToList();

        if (protectedRows.Count > 0)
        {
            sections.Add(new HealthSectionViewModel(ProtectedTitle, protectedRows, [], [], protectionNote, foldsRows: true));
        }

        return new HealthPage(attention, sections, checks.Count(c => c.Status == HealthStatus.Good));
    }
}
