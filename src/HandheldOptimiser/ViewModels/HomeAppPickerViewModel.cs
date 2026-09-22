using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandheldOptimiser.Services;

namespace HandheldOptimiser.ViewModels;

/// <summary>
/// Which app full screen mode opens. Saved as soon as it changes: the launcher reads it each time it
/// starts, so a new choice takes effect on the next home button press without re-registering anything.
/// </summary>
public sealed partial class HomeAppPickerViewModel : ObservableObject, IPageSection
{
    private readonly LogService _log;
    private bool _loading;

    public HomeAppPickerViewModel(LogService log)
    {
        _log = log;
        Reload();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteam), nameof(IsArmouryCrate), nameof(IsCustom))]
    private HomeAppTarget _target;

    [ObservableProperty]
    private string _customPath = string.Empty;

    [ObservableProperty]
    private string _customArgs = string.Empty;

    [ObservableProperty]
    private string _availabilityText = string.Empty;

    [ObservableProperty]
    private bool _isAvailable;

    public bool SteamInstalled { get; private set; }
    public bool ArmouryCrateInstalled { get; private set; }

    public string SteamNote => SteamInstalled ? "Opens straight into Big Picture mode." : "Steam is not installed.";
    public string ArmouryCrateNote => ArmouryCrateInstalled ? "Opens the Armoury Crate SE home screen." : "Armoury Crate SE is not installed.";

    // Radio buttons bind to these; each setter only acts on being checked.
    public bool IsSteam
    {
        get => Target == HomeAppTarget.Steam;
        set { if (value) Target = HomeAppTarget.Steam; }
    }

    public bool IsArmouryCrate
    {
        get => Target == HomeAppTarget.ArmouryCrate;
        set { if (value) Target = HomeAppTarget.ArmouryCrate; }
    }

    public bool IsCustom
    {
        get => Target == HomeAppTarget.Custom;
        set { if (value) Target = HomeAppTarget.Custom; }
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            var (target, path, args) = HomeAppRegistration.LoadChoice();
            Target = target;
            CustomPath = path;
            CustomArgs = args;
        }
        finally
        {
            _loading = false;
        }

        SteamInstalled = HomeAppRegistration.IsSteamInstalled();
        ArmouryCrateInstalled = HomeAppRegistration.IsArmouryCrateInstalled();
        OnPropertyChanged(nameof(SteamNote));
        OnPropertyChanged(nameof(ArmouryCrateNote));

        IsAvailable = HomeAppRegistration.IsFullScreenExperienceAvailable();
        AvailabilityText = !IsAvailable
            ? "The full screen experience is not available on this version of Windows. It needs Windows 11 24H2 or later on a handheld."
            : HomeAppRegistration.IsCurrentHomeApp() && !HomeAppRegistration.IsRegistered()
                ? "This version of the app has an updated home app registration. Switch on \"Use Handheld Optimiser as the full screen home app\" below to refresh it."
                : HomeAppRegistration.IsCurrentHomeApp()
                    ? "Handheld Optimiser is the current home app."
                    : "Choose an app below, then switch on \"Use Handheld Optimiser as the full screen home app\".";
    }

    partial void OnTargetChanged(HomeAppTarget value) => Save();
    partial void OnCustomPathChanged(string value) => Save();
    partial void OnCustomArgsChanged(string value) => Save();

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            HomeAppRegistration.SaveChoice(Target, CustomPath.Trim(), CustomArgs.Trim());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            _log.Error($"Could not save the home app choice: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the app full screen mode should open",
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            CustomPath = dialog.FileName;
            Target = HomeAppTarget.Custom;
        }
    }
}
