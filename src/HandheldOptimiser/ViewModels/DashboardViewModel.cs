using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The one-click page. "Apply Optimised Tweaks" applies every tweak marked for it, after a restore point and after
/// an explicit confirmation listing exactly what carries a warning.
/// </summary>
public sealed partial class DashboardViewModel(
    TweakEngine engine,
    SystemStateService systemState,
    HardwareInfoService hardware,
    RestorePointService restorePoints,
    LogService log,
    IShell shell)
    : PageViewModelBase(shell)
{
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly TweakEngine _engine = engine;
    private readonly SystemStateService _systemState = systemState;
    private readonly HardwareInfoService _hardware = hardware;
    private readonly RestorePointService _restorePoints = restorePoints;
    private readonly LogService _log = log;

    public override string Title => "Dashboard";
    public override string Glyph => "";

    [ObservableProperty]
    private string _hardwareModel = "Detecting…";

    [ObservableProperty]
    private bool _isRecognisedDevice;

    [ObservableProperty]
    private string _optimisationStatus = "Not checked yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedAngle), nameof(AppliedLevel))]
    private int _appliedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedAngle), nameof(AppliedLevel))]
    private int _totalCount;

    /// <summary>
    /// Which colour the ring on the Dashboard is drawn in: red under a third applied, yellow from a
    /// third, green from two thirds.
    /// </summary>
    public OptimisationLevel AppliedLevel => AppliedCount * 3 < TotalCount ? OptimisationLevel.Low
        : AppliedCount * 3 < TotalCount * 2 ? OptimisationLevel.Medium
        : OptimisationLevel.High;

    /// <summary>
    /// How far round the ring on the Dashboard is filled, in degrees. Stops just short of a full turn:
    /// an arc that ends where it starts is drawn as nothing at all.
    /// </summary>
    public double AppliedAngle => TotalCount == 0 ? 0 : Math.Min(359.99, 360.0 * AppliedCount / TotalCount);

    [ObservableProperty]
    private bool _protectionEnabled = true;

    [ObservableProperty]
    private bool _canRevert;

    [ObservableProperty]
    private string _revertSummary = string.Empty;

    [ObservableProperty]
    private string _batteryChargeText = "Checking…";

    [ObservableProperty]
    private string _batteryPowerText = string.Empty;

    [ObservableProperty]
    private string _batteryHealthText = "Checking…";

    [ObservableProperty]
    private string _batteryHealthDetail = string.Empty;

    [ObservableProperty]
    private string _graphicsMemoryText = "Checking…";

    [ObservableProperty]
    private string _graphicsMemoryDetail = string.Empty;

    [ObservableProperty]
    private string _graphicsDriverText = "Checking…";

    [ObservableProperty]
    private string _graphicsDriverDetail = string.Empty;

    // The tiles show a figure and a word or two. The fuller explanation is a tooltip on each, which on
    // a touch screen opens with a long press.
    [ObservableProperty]
    private string _batteryHealthTip = string.Empty;

    [ObservableProperty]
    private string _graphicsMemoryTip = string.Empty;

    [ObservableProperty]
    private string _graphicsDriverTip = string.Empty;

    private System.Windows.Threading.DispatcherTimer? _batteryTimer;
    private uint? _designCapacity;

    public override async Task OnNavigatedToAsync()
    {
        StartBatteryUpdates();
        await RefreshCommand.ExecuteAsync(null);
    }

    public override void OnNavigatedFrom() => _batteryTimer?.Stop();

    /// <summary>
    /// Charge and power draw move by the second, so they are read on a timer for as long as the page is
    /// showing. This stays outside the shell's busy gate: it changes nothing and must not hold up a tweak.
    /// </summary>
    private void StartBatteryUpdates()
    {
        if (_batteryTimer is null)
        {
            _batteryTimer = new System.Windows.Threading.DispatcherTimer { Interval = BatteryRefreshInterval };
            _batteryTimer.Tick += (_, _) => UpdateBattery();
        }

        _designCapacity ??= _hardware.ReadDesignCapacity();
        UpdateBattery();
        _batteryTimer.Start();
    }

    private void UpdateBattery()
    {
        var battery = _hardware.ReadBattery();
        if (battery is null)
        {
            BatteryChargeText = "No battery";
            BatteryPowerText = string.Empty;
            BatteryHealthText = "No battery";
            BatteryHealthDetail = string.Empty;
            BatteryHealthTip = string.Empty;
            return;
        }

        BatteryChargeText = battery.ChargePercent is { } percent ? $"{percent}%" : "Not reported";

        var watts = battery.Rate is { } rate ? $" {rate / 1000.0:0.0} W" : string.Empty;
        BatteryPowerText = battery.Charging ? $"Charging{watts}"
            : battery.OnMains ? "Plugged in"
            : battery.Rate is null ? "On battery"
            : $"Discharging{watts}";

        if (_designCapacity is not { } design || battery.FullChargedCapacity == 0)
        {
            BatteryHealthText = "Not reported";
            BatteryHealthDetail = string.Empty;
            BatteryHealthTip = "This battery does not report its design capacity.";
            return;
        }

        // A new battery can hold slightly more than its rated capacity, which is not 104% health.
        var health = (int)Math.Round(Math.Min(100.0, battery.FullChargedCapacity * 100.0 / design));
        BatteryHealthText = $"{health}%";
        BatteryHealthDetail = $"{100 - health}% wear";
        BatteryHealthTip =
            $"Holds {battery.FullChargedCapacity / 1000.0:0.0} Wh of the {design / 1000.0:0.0} Wh it was built for.";
    }

    private void ShowGraphics(GraphicsReading? graphics)
    {
        if (graphics is null)
        {
            GraphicsMemoryText = "Not reported";
            GraphicsMemoryDetail = string.Empty;
            GraphicsMemoryTip = "No AMD or Intel graphics adapter was found.";
            GraphicsDriverText = "Not reported";
            GraphicsDriverDetail = string.Empty;
            GraphicsDriverTip = string.Empty;
            return;
        }

        const long Mebibyte = 1024 * 1024;
        const long Gibibyte = 1024 * Mebibyte;

        if (graphics.IsIntel)
        {
            GraphicsMemoryText = "Shared";
            GraphicsMemoryDetail = "No fixed buffer";
            GraphicsMemoryTip = "Intel graphics take memory from system RAM as needed; there is no fixed buffer.";
        }
        else
        {
            // The fixed sizes a BIOS offers start at 1 GB. Anything smaller is what the Auto setting
            // reserves up front, with the rest handed over as games ask for it, so it is shown as Auto
            // rather than as a figure that looks like a fault.
            (GraphicsMemoryText, GraphicsMemoryDetail, GraphicsMemoryTip) = graphics.DedicatedMemoryBytes switch
            {
                long bytes when bytes >= Gibibyte =>
                    ($"{(double)bytes / Gibibyte:0.#} GB", "UMA buffer", "Memory set aside for graphics (the UMA buffer)."),
                long bytes =>
                    ("Auto", "UMA buffer", $"UMA buffer on Auto: {bytes / Mebibyte} MB reserved, more shared as games need it."),
                null => ("Not reported", string.Empty, string.Empty)
            };
        }

        string?[] details =
        [
            graphics.Name,
            graphics.VendorSoftwareVersion is { } software ? $"AMD Software {software}" : null
        ];

        GraphicsDriverText = string.IsNullOrWhiteSpace(graphics.DriverVersion) ? "Not reported" : graphics.DriverVersion;
        GraphicsDriverDetail = graphics.DriverDate?.ToString("d MMM yyyy") ?? string.Empty;
        GraphicsDriverTip = string.Join(" · ", details.Where(d => !string.IsNullOrWhiteSpace(d)));
    }

    [RelayCommand]
    private async Task RefreshAsync() =>
        await Shell.RunExclusiveAsync("Checking current state…", (progress, ct) => LoadStateAsync(progress, ct));

    private async Task LoadStateAsync(IProgress<string> progress, CancellationToken ct)
    {
        HardwareModel = _systemState.GetHardwareModel();
        IsRecognisedDevice = _systemState.DetectDevice() != HandheldDevice.Unknown;

        ShowGraphics(await _hardware.GetGraphicsAsync(ct));

        ProtectionEnabled = await _restorePoints.IsProtectionEnabledAsync(ct);

        var tweaks = _engine.OneClickTweaks.ToList();
        TotalCount = tweaks.Count;

        var applied = 0;
        foreach (var tweak in tweaks)
        {
            progress.Report($"Checking {tweak.Name}");
            if (await _engine.DetectAsync(tweak, ct) == TweakState.Applied)
            {
                applied++;
            }
        }

        AppliedCount = applied;

        OptimisationStatus = applied == 0 ? "Stock Windows: nothing optimised yet"
            : applied == TotalCount ? "Fully optimised"
            : $"Partially optimised: {TotalCount - applied} tweak(s) still available";

        CanRevert = _engine.HasUndoData;
        RevertSummary = CanRevert
            ? "Undo data is available for previously applied tweaks."
            : "No undo data recorded yet.";
    }

    [RelayCommand]
    private async Task RunOneClickAsync()
    {
        var pending = _engine.OneClickTweaks.ToList();

        var risky = pending.Where(t => t.Risk is RiskLevel.SecurityTradeoff or RiskLevel.Breaking).ToList();

        var message =
            $"This applies {pending.Count} tweaks in one pass, after creating a System Restore point.\n\n";

        if (risky.Count > 0)
        {
            message += "These carry real trade-offs:\n" +
                       string.Join("\n\n", risky.Select(t => $"  • {t.Name}\n    {t.Warning}")) +
                       "\n\n";
        }

        message += "Each tweak can be individually reverted afterwards from its category page.";

        if (!await Shell.ConfirmAsync("Apply Optimised Tweaks", message, "Create restore point & apply"))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Apply Optimised Tweaks", async (progress, ct) =>
        {
            var summary = await _engine.ApplyAsync(pending, "Apply Optimised Tweaks", createRestorePoint: true, progress, ct);

            if (summary.Aborted)
            {
                await Shell.ConfirmAsync("Nothing was changed", summary.AbortReason ?? "Aborted.", "OK");
                return;
            }

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            await LoadStateAsync(progress, ct);

            await Shell.ConfirmAsync(
                "Optimisation complete",
                $"{summary.Applied} tweak(s) applied.\n" +
                $"{summary.AlreadyDone} were already set.\n" +
                (summary.Failures > 0 ? $"{summary.Failures} failed. Check the log.\n" : string.Empty) +
                (summary.RebootRequired ? "\nRestart required for some changes to take effect." : string.Empty) +
                "\n\nBloatware removal is deliberately separate. Review it on the Bloatware page.",
                "OK");
        });
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (!await Shell.ConfirmAsync(
                "Undo all changes",
                "Every tweak this app applied will be restored to the value it had beforehand, using the " +
                "recorded undo data.\n\nRemoved bloatware is not restored. That has to come back from the " +
                "Microsoft Store.",
                "Undo all changes"))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Undoing all changes…", async (progress, ct) =>
        {
            var summary = await _engine.RevertAllAsync(progress, ct);

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            await LoadStateAsync(progress, ct);

            await Shell.ConfirmAsync(
                "Undo finished",
                $"{summary.Applied} tweak(s) reverted." +
                (summary.Failures > 0 ? $"\n{summary.Failures} failed. Check the log." : string.Empty),
                "OK");
        });
    }

    [RelayCommand]
    private async Task CreateRestorePointOnlyAsync()
    {
        await Shell.RunExclusiveAsync("Creating System Restore point…", async (progress, ct) =>
        {
            var result = await _restorePoints.CreateAsync("Handheld Optimiser: manual checkpoint", ct);

            await Shell.ConfirmAsync(
                result.Created ? "Restore point created" : "Could not create restore point",
                result.Message,
                "OK");

            ProtectionEnabled = await _restorePoints.IsProtectionEnabledAsync(ct);
        });
    }
}

/// <summary>How much of the optimised set is applied, for the colour of the Dashboard ring.</summary>
public enum OptimisationLevel
{
    Low,
    Medium,
    High
}
