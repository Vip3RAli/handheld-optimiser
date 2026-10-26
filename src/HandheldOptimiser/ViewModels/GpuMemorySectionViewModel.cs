using CommunityToolkit.Mvvm.ComponentModel;
using HandheldOptimiser.PowerService;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>One size the GPU memory can be set to, in GB with 0 for Auto, and how the dropdown names it.</summary>
public sealed record GpuMemoryChoice(int Size, string Name);

/// <summary>What the GPU memory row says. No window, so it can be tested.</summary>
public static class GpuMemoryText
{
    /// <summary>"Auto" or "4 GB".</summary>
    public static string Name(int size) => size == 0 ? "Auto" : $"{size} GB";

    /// <summary>What Windows has, and what waits for a restart: "6 GB", or "6 GB, 4 GB after restart".</summary>
    public static string Describe(int? now, int? next) => now is not { } size
        ? "Unknown"
        : next is { } waiting ? $"{Name(size)}, {Name(waiting)} after restart" : Name(size);
}

/// <summary>
/// The Ally's GPU memory, at the top of the Performance page. TDP Engine reads and sets it through the
/// firmware. A new size is used from the next restart, so picking one asks to restart straight away, as
/// Armoury Crate does. Left out on a device that cannot set it.
/// </summary>
public sealed partial class GpuMemorySectionViewModel : SectionViewModel
{
    private readonly TdpEngineClient _engine;
    private bool _loaded;
    private bool _placing;
    private bool _shown = true;
    private int? _now;
    private int? _next;

    public GpuMemorySectionViewModel(LogService log, IShell shell)
        : base(shell)
    {
        _engine = new TdpEngineClient(log);
    }

    /// <summary>Every size on offer, Auto first.</summary>
    public IReadOnlyList<GpuMemoryChoice> Choices { get; } = [.. GpuMemory.Sizes.Select(s => new GpuMemoryChoice(s, GpuMemoryText.Name(s)))];

    /// <summary>The size Windows will have after the next restart. Picked by the player, it is set by <see cref="ApplyAsync"/>.</summary>
    [ObservableProperty]
    private GpuMemoryChoice? _selected;

    [ObservableProperty]
    private string _detail = "Reading";

    /// <summary>Whether TDP Engine answered, so a size can be picked.</summary>
    [ObservableProperty]
    private bool _isReachable;

    public override bool IsShown => _shown;

    public override async Task LoadAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RefreshAsync();
    }

    public override Task RefreshAsync() => Shell.RunScanAsync("GPU memory", async ct =>
        Show(await _engine.SendAsync(new AutoTdpRequest(AutoTdpProtocol.GpuMemoryRequest), ct)));

    /// <summary>
    /// Sets the size picked, once the dropdown has closed on it, then asks to restart. Not on every change of
    /// the selection: an open WPF dropdown changes it at each arrow press.
    /// </summary>
    public async Task ApplyAsync()
    {
        if (_placing || Selected is not { } picked || picked.Size == (_next ?? _now))
        {
            return;
        }

        var size = picked.Size;
        AutoTdpStatus? answer = null;
        await Shell.RunExclusiveAsync("Setting GPU memory", async (_, ct) =>
            answer = await _engine.SendAsync(new AutoTdpRequest(AutoTdpProtocol.SetGpuMemory, GpuMemory: size), ct));
        Show(answer);

        if (answer?.Error is { } problem)
        {
            await Shell.ConfirmAsync("GPU memory not changed", problem, "OK");
            return;
        }

        // Back to the size Windows has now: nothing waits, so there is nothing to restart for.
        if (answer?.GpuMemoryNext is not { } next)
        {
            return;
        }

        if (await Shell.ConfirmAsync(
                "Restart now?",
                $"GPU memory changes to {GpuMemoryText.Name(next)} when Windows restarts. Save anything you have open first.",
                "Restart now"))
        {
            WindowsRestart.Start();
        }
        else
        {
            Shell.NotifyRebootRequired();
        }
    }

    /// <summary>Shows TDP Engine's answer, or that it did not answer, and puts the dropdown on the size to come.</summary>
    private void Show(AutoTdpStatus? status)
    {
        if (status is null)
        {
            IsReachable = false;
            Detail = "TDP Engine is not running";
        }
        else
        {
            var offered = status.GpuMemoryOffered ?? [];
            SetShown(offered.Length > 0);
            IsReachable = offered.Length > 0;
            _now = status.GpuMemoryNow;
            _next = status.GpuMemoryNext;
            Detail = GpuMemoryText.Describe(_now, _next);
        }

        _placing = true;
        Selected = Choices.FirstOrDefault(c => c.Size == (_next ?? _now));
        _placing = false;
    }

    private void SetShown(bool shown)
    {
        if (_shown != shown)
        {
            _shown = shown;
            OnPropertyChanged(nameof(IsShown));
        }
    }
}
