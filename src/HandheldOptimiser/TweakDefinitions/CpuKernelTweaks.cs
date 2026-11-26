using Microsoft.Win32;
using HandheldOptimiser.Models;

namespace HandheldOptimiser.TweakDefinitions;

/// <summary>
/// Scheduler, latency, power-plan efficiency and shared-memory settings for Windows 11 handhelds.
/// Reversible; nothing here touches protected security, update, or vendor driver paths.
/// </summary>
public static class CpuKernelTweaks
{
    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";
    private const string PciExpressSubgroup = "501a4d13-42af-4429-9fd1-a8218c268e20";

    private const string EppSetting = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";
    private const string CoreParkingMinSetting = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string PcieAspmSetting = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    private const string MultimediaProfileKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesTaskKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string ControlKey = @"SYSTEM\CurrentControlSet\Control";

    public static IReadOnlyList<Tweak> All =>
    [
        PrioritySeparation,
        MmcssGames,
        GlobalTimerResolution,
        ApuEnergyPreference,
        AcPcieAspmOff,
        AcCoreUnpark,
        SvcHostMerge,
        MemoryCompression
    ];

    /// <summary>
    /// 0x26 = short, variable quanta with the maximum (3:1) foreground boost. The Windows client default
    /// is 0x2, which already favours the foreground but with a smaller boost.
    /// </summary>
    public static Tweak PrioritySeparation => new()
    {
        Id = "cpu.priorityseparation",
        Name = "Give the game priority over background apps",
        Description =
            "Gives the game in the foreground the CPU ahead of background work. Sets the " +
            "Win32PrioritySeparation value. Takes effect immediately.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Safe,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
                "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// Tunes Windows' Multimedia Class Scheduler Service for games: lowers the background CPU reservation
    /// from 20% to 10% (leaving headroom for audio drivers without glitches) and sets High GPU/scheduling
    /// priority on Tasks\Games for tighter 1% lows when background apps wake.
    /// </summary>
    public static Tweak MmcssGames => new()
    {
        Id = "cpu.mmcss_games",
        Name = "Prioritise games in the multimedia scheduler",
        Description =
            "Lowers Windows' background CPU reservation from 20% to 10% and marks games as high " +
            "CPU, GPU and storage priority in MMCSS to tighten 1% lows.",
        Category = TweakCategory.Games,
        Risk = RiskLevel.Safe,
        RequiresReboot = true,
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, MultimediaProfileKey, "SystemResponsiveness", 10, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, GamesTaskKey, "GPU Priority", 8, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, GamesTaskKey, "Priority", 6, RegistryValueKind.DWord),
            new(RegistryRoot.LocalMachine, GamesTaskKey, "Scheduling Category", "High", RegistryValueKind.String),
            new(RegistryRoot.LocalMachine, GamesTaskKey, "SFIO Priority", "High", RegistryValueKind.String)
        ]
    };

    /// <summary>
    /// On Windows 11 22H2+, per-process timer resolution requests are ignored when a window loses focus
    /// unless GlobalTimerResolutionRequests is 1. Enabling it improves pacing for sleep-based frame caps
    /// (such as Radeon Chill or in-game limiters) alongside overlays, with a slight idle battery cost.
    /// </summary>
    public static Tweak GlobalTimerResolution => new()
    {
        Id = "cpu.global_timer_res",
        Name = "Honour high-precision frame timers globally",
        Description =
            "Stops Windows 11 24H2 throttling high-resolution timers across processes, smoothing sleep-based " +
            "frame limiters and overlay timing.",
        Category = TweakCategory.Games,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Apps that ask Windows for a 1 ms timer tick keep doing so in the background, which can slightly " +
            "increase idle battery drain.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, KernelKey, "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord)
        ]
    };

    /// <summary>
    /// On shared-TDP Ryzen Z1 Extreme APUs, setting EPP to 0% pushes CPU clocks up aggressively on light
    /// work and steals package watts from the RDNA 3 iGPU. Biasing EPP (65% plugged in, 75% on battery)
    /// lets the GPU keep more of the power budget in GPU-bound games.
    /// </summary>
    public static Tweak ApuEnergyPreference => PowerPlanHelper.CreateTweak(
        id: "cpu.apu_epp",
        name: "Bias APU power budget towards the GPU (EPP)",
        description:
            "Sets processor Energy Performance Preference to 65% plugged in and 75% on battery so the " +
            "CPU stops boosting unnecessarily on light loads and leaves more wattage for the integrated GPU.",
        category: TweakCategory.System,
        risk: RiskLevel.Moderate,
        warning:
            "Purely CPU-bound workloads and heavy emulators may boost slightly slower than at 0% EPP. Undo " +
            "if an emulator loses frame rate.",
        includeInOneClick: false,
        subgroup: ProcessorSubgroup,
        setting: EppSetting,
        ac: 65,
        dc: 75);

    /// <summary>
    /// Disables PCI Express Link State Power Management (ASPM) on AC power only, reducing NVMe and
    /// USB4/dock micro-stutters while leaving battery savings intact unplugged.
    /// </summary>
    public static Tweak AcPcieAspmOff => PowerPlanHelper.CreateTweak(
        id: "cpu.ac_pcie_aspm",
        name: "Turn off PCIe power saving when plugged in",
        description:
            "Disables PCI Express Link State Power Management (ASPM) on mains power to reduce NVMe " +
            "and dock micro-stutters while keeping battery savings unplugged.",
        category: TweakCategory.System,
        risk: RiskLevel.Safe,
        warning: null,
        includeInOneClick: true,
        subgroup: PciExpressSubgroup,
        setting: PcieAspmSetting,
        ac: 0,
        dc: null);

    /// <summary>
    /// Prevents Windows from parking CPU cores while plugged in, removing wake-up latency spikes in
    /// DX11 and CPU-sensitive games with zero impact on unplugged battery life.
    /// </summary>
    public static Tweak AcCoreUnpark => PowerPlanHelper.CreateTweak(
        id: "cpu.ac_core_unpark",
        name: "Keep all CPU cores unparked when plugged in",
        description:
            "Sets minimum unparked CPU cores to 100% on mains power so frames do not stutter waiting " +
            "for parked cores to wake, without affecting battery runtime.",
        category: TweakCategory.System,
        risk: RiskLevel.Safe,
        warning: null,
        includeInOneClick: true,
        subgroup: ProcessorSubgroup,
        setting: CoreParkingMinSetting,
        ac: 100,
        dc: null);

    /// <summary>
    /// Windows 11 splits services into separate svchost processes once RAM exceeds SvcHostSplitThresholdInKB.
    /// Raising the threshold to 64 GB merges them back into shared hosts, typically saving 100-250 MB on
    /// 16 GB handhelds.
    /// </summary>
    public static Tweak SvcHostMerge => new()
    {
        Id = "cpu.svchost_merge",
        Name = "Merge split Windows background hosts (save RAM)",
        Description =
            "Raises SvcHostSplitThresholdInKB so Windows groups background services instead of running " +
            "each in its own process, freeing roughly 100 to 250 MB of shared memory.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "A crash in one shared Windows service can affect others in the same host process. Intended for " +
            "16 GB handhelds running tight on shared memory.",
        RegistryValues =
        [
            new(RegistryRoot.LocalMachine, ControlKey, "SvcHostSplitThresholdInKB", 67108864, RegistryValueKind.DWord)
        ]
    };

    private const string CompressionJournalKey = "mmagent.compression";

    /// <summary>
    /// Disables Windows memory compression to remove background CPU decompression hitches on 24 GB+
    /// handhelds (such as the Ally X). Kept opt-in because 16 GB handhelds benefit from compression
    /// when VRAM is set to 6 GB.
    /// </summary>
    public static Tweak MemoryCompression => new()
    {
        Id = "cpu.memory_compression",
        Name = "Turn off Windows memory compression (24 GB+ handhelds)",
        Description =
            "Stops Windows compressing idle RAM pages in the background, removing decompression CPU " +
            "hitches during heavy streaming on 24 GB models such as the ROG Ally X.",
        Category = TweakCategory.System,
        Risk = RiskLevel.Moderate,
        RequiresReboot = true,
        IncludeInOneClick = false,
        Warning =
            "Recommended only on 24 GB or larger handhelds (such as the Ally X). On a 16 GB ROG Ally with " +
            "6 GB GPU memory assigned, turning off compression can cause games to run out of RAM and crash.",

        ScriptDetect = async (ctx, ct) =>
        {
            var outcome = await ctx.Runner.RunScriptAsync(
                "Write-Output \"MEMCOMP=$((Get-MMAgent).MemoryCompression)\"",
                "Check Windows memory compression state",
                ct,
                echoScript: false);

            return ParseMemoryCompressionState(outcome.OutputLines);
        },

        ScriptApply = async (ctx, journal, ct) =>
        {
            var detect = await ctx.Runner.RunScriptAsync(
                "Write-Output \"MEMCOMP=$((Get-MMAgent).MemoryCompression)\"",
                "Check Windows memory compression state",
                ct,
                echoScript: false);

            var current = ParseMemoryCompressionState(detect.OutputLines);
            if (current == TweakState.Applied)
            {
                return TweakResult.NoChange("cpu.memory_compression");
            }

            if (current != TweakState.NotApplied)
            {
                return TweakResult.Fail("cpu.memory_compression", "Could not read memory compression status. The console has the detail.");
            }

            journal.CapturedState[CompressionJournalKey] = "Enabled";

            var outcome = await ctx.Runner.RunScriptAsync(
                "Disable-MMAgent -MemoryCompression -ErrorAction Stop",
                "Disable Windows memory compression",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("cpu.memory_compression", "Windows memory compression turned off.", rebootRequired: true)
                : TweakResult.Fail("cpu.memory_compression", "Could not turn off memory compression. The console has the detail.");
        },

        ScriptRevert = async (ctx, journal, ct) =>
        {
            if (!journal.CapturedState.TryGetValue(CompressionJournalKey, out var was) || was != "Enabled")
            {
                return TweakResult.NoChange("cpu.memory_compression");
            }

            var outcome = await ctx.Runner.RunScriptAsync(
                "Enable-MMAgent -MemoryCompression -ErrorAction Stop",
                "Enable Windows memory compression",
                ct);

            return outcome.Succeeded
                ? TweakResult.Ok("cpu.memory_compression", "Windows memory compression turned back on.", rebootRequired: true)
                : TweakResult.Fail("cpu.memory_compression", "Could not turn memory compression back on. The console has the detail.");
        }
    };

    public static TweakState ParseMemoryCompressionState(IEnumerable<string> outputLines)
    {
        var line = outputLines.FirstOrDefault(l => l.StartsWith("MEMCOMP=", StringComparison.Ordinal));
        if (line is null)
        {
            return TweakState.Unknown;
        }

        var value = line["MEMCOMP=".Length..].Trim();
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase))
        {
            return TweakState.Applied;
        }

        return value.Equals("True", StringComparison.OrdinalIgnoreCase)
            ? TweakState.NotApplied
            : TweakState.Unknown;
    }
}
