using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilipBoard.App.Services;
using FilipBoard.Data;

namespace FilipBoard.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual void OnSnapshot(Snapshot s) { }
    public virtual void OnShown() { }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly FanControlService _svc;
    private readonly Action _restartAdmin;
    private readonly Action _exit;

    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _selectedPage;
    [ObservableProperty] private string _banner = "";
    [ObservableProperty] private bool _bannerIsWarning;
    [ObservableProperty] private bool _showAdminButton;
    [ObservableProperty] private string _profileText = "";
    [ObservableProperty] private string _emergencyMessage = "";

    public MainViewModel(FanControlService svc, SqliteStore store, Action restartAdmin, Action exit)
    {
        _svc = svc; _restartAdmin = restartAdmin; _exit = exit;
        Units.Fahrenheit = svc.Settings.UseFahrenheit;
        Pages = new ObservableCollection<PageViewModel>
        {
            new DashboardViewModel(svc),
            new FanControlViewModel(svc),
            new CurvesViewModel(svc),
            new TemperatureViewModel(svc),
            new ProfilesViewModel(svc),
            new HistoryViewModel(svc, store),
            new HardwareViewModel(svc),
            new SettingsViewModel(svc, store),
        };
        _selectedPage = Pages[0];
        UpdateBanner();
        svc.Updated += s => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var p in Pages) p.OnSnapshot(s);
            UpdateBanner();
        });
    }

    partial void OnSelectedPageChanged(PageViewModel value) => value.OnShown();

    private void UpdateBanner()
    {
        var hw = _svc.Provider.Summary;
        ProfileText = $"Profile: {_svc.ActiveProfile.Name}";
        var parts = new List<string>();
        var warn = false;
        if (hw.IsMock) { parts.Add("DEMO MODE: all values are SIMULATED, not real hardware."); warn = true; }
        if (!hw.IsMock && !hw.IsElevated) { parts.Add("Limited access: run as Administrator to read motherboard fans and CPU temperatures."); warn = true; }
        if (_svc.Gate.ReadOnlyMode) parts.Add("Read-only mode: no fan commands are sent; the BIOS controls the fans.");
        Banner = string.Join("  ", parts);
        BannerIsWarning = warn;
        ShowAdminButton = !hw.IsMock && !hw.IsElevated;
    }

    [RelayCommand] private void RestartAdmin() => _restartAdmin();

    [RelayCommand]
    private void Emergency()
    {
        var n = _svc.EmergencyFullSpeed();
        EmergencyMessage = n > 0 ? $"Full speed applied to {n} channel(s)." :
            "Full-speed override unavailable: no verified writable channel. The BIOS still controls the fans.";
    }

    [RelayCommand] private void Exit() => _exit();
}
