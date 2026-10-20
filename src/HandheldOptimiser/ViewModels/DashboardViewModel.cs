using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Models;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The one-click page. "Apply recommended tweaks" applies every tweak marked for it, after a restore point and after
/// an explicit confirmation listing exactly what carries a warning.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModelBase
{
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly TweakEngine _engine;
    private readonly TweakStateStore _states;
    private readonly SystemStateService _systemState;
    private readonly HardwareInfoService _hardware;
    private readonly RestorePointService _restorePoints;

    // The device's own details (model, graphics, System Protection) are read once. They do not change
    // while the app is open, apart from what Re-check and the restore point button read again.
    private bool _deviceRead;
    private bool _readingDevice;

    public DashboardViewModel(
        TweakEngine engine,
        TweakStateStore states,
        SystemStateService systemState,
        HardwareInfoService hardware,
        RestorePointService restorePoints,
        IShell shell)
        : base(shell)
    {
        _engine = engine;
        _states = states;
        _systemState = systemState;
        _hardware = hardware;
        _restorePoints = restorePoints;

        // The ring follows the shared tweak states, so it fills in as the scan at start-up answers
        // and moves when a tweak is applied on any page.
        _states.Changed += _ => ShowTweaks();
        ShowTweaks();
    }

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
        await LoadAsync();

        // Create restore point, on the Maintenance & backup page, turns System Protection on. The
        // warning about it being off is asked about again on coming back, so it clears.
        if (_deviceRead && !ProtectionEnabled)
        {
            ProtectionEnabled = await _restorePoints.IsProtectionEnabledAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The tweaks behind the ring are asked about here too, in case nothing else has: this is the first
    /// page shown, and the scan at start-up is skipped when the app was opened to install an update.
    /// </summary>
    public override Task LoadAsync() => Task.WhenAll(
        _states.CheckMissingAsync(_engine.OneClickTweaks),
        _deviceRead || _readingDevice ? Task.CompletedTask : Shell.RunScanAsync("this device", ReadDeviceAsync));

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

    /// <summary>Re-check: reads the device and every tweak again, for changes made outside the app.</summary>
    [RelayCommand]
    private Task RefreshAsync() =>
        Shell.RunScanAsync("current state", ct => Task.WhenAll(ReadDeviceAsync(ct), _states.CheckAllAsync(ct: ct)));

    private async Task ReadDeviceAsync(CancellationToken ct)
    {
        _readingDevice = true;

        try
        {
            HardwareModel = _systemState.GetHardwareModel();
            IsRecognisedDevice = _systemState.DetectDevice() != HandheldDevice.Unknown;

            // Two separate questions, asked side by side.
            var graphics = _hardware.GetGraphicsAsync(ct);
            var protection = _restorePoints.IsProtectionEnabledAsync(ct);

            ShowGraphics(await graphics);
            ProtectionEnabled = await protection;

            _deviceRead = true;
        }
        finally
        {
            _readingDevice = false;
        }
    }

    /// <summary>How many of the one-click tweaks are applied, from the shared states.</summary>
    private void ShowTweaks()
    {
        var summary = _states.Summarise(_engine.OneClickTweaks);

        TotalCount = summary.Total;
        AppliedCount = summary.Applied;

        OptimisationStatus = !summary.IsComplete ? "Checking…"
            : summary.Applied == 0 ? "No recommended tweaks are on yet"
            : summary.Applied == summary.Total ? "All recommended tweaks are on"
            : $"{Plural.Count(summary.Total - summary.Applied, "recommended tweak")} still off";
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
            message += "These have downsides:\n" +
                       string.Join("\n\n", risky.Select(t => $"  • {t.Name}\n    {t.Warning}")) +
                       "\n\n";
        }

        message += "You can undo any of them later from its page.";

        if (!await Shell.ConfirmAsync("Apply recommended tweaks", message, "Create restore point & apply"))
        {
            return;
        }

        await Shell.RunExclusiveAsync("Apply recommended tweaks", async (progress, ct) =>
        {
            var summary = await _engine.ApplyAsync(pending, "Apply recommended tweaks", createRestorePoint: true, progress, ct);

            if (summary.Aborted)
            {
                await Shell.ConfirmAsync("Nothing was changed", summary.AbortReason ?? "Aborted.", "OK");
                return;
            }

            if (summary.RebootRequired)
            {
                Shell.NotifyRebootRequired();
            }

            progress.Report("Checking what changed");
            await _states.CheckAllAsync(ct: ct);

            await Shell.ConfirmAsync(
                "Recommended tweaks applied",
                Plural.Count(summary.Applied, "tweak") + " applied.\n" +
                $"{summary.AlreadyDone} were already set.\n" +
                (summary.Failures > 0 ? $"{summary.Failures} failed. The console has the detail.\n" : string.Empty) +
                (summary.RebootRequired ? "\nRestart required for some changes to take effect." : string.Empty) +
                "\n\nPreinstalled apps are removed from their own page, under Apps.",
                "OK");
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
