using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilipBoard.App.Services;
using FilipBoard.Core;

namespace FilipBoard.App.ViewModels;

public sealed partial class FanRowViewModel : ObservableObject
{
    private readonly FanControlService _svc;
    public string Key { get; }
    public FanChannel Channel { get; private set; }
    public bool HasChannel => Channel.Id.Length > 0;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _rpmText = "--";
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private FanMode _mode;
    [ObservableProperty] private double _manualPercent = 40;
    [ObservableProperty] private bool _writeAllowed;
    [ObservableProperty] private bool _verified;
    [ObservableProperty] private string _writeReason = "";
    [ObservableProperty] private string _verifyResult = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _applyMessage = "";

    public Array Modes { get; } = Enum.GetValues(typeof(FanMode));

    public FanRowViewModel(FanControlService svc, string key, FanChannel ch)
    {
        _svc = svc; Key = key; Channel = ch;
        if (HasChannel) { var cfg = svc.GetConfig(ch.Id); _mode = cfg.Mode; _manualPercent = cfg.ManualPercent; }
    }

    public void Update(ChannelState c)
    {
        Channel = c.Channel;
        Header = HasChannel ? $"Header/channel: {c.Channel.Name}" : "Monitor only - the motherboard exposes no control channel for this header";
        RpmText = c.Rpm is { } r ? $"{r:0} RPM" : "no RPM signal";
        CommandText = c.CommandedPercent is { } p ? $"Requested {p:0}%" : "Requested: hardware control";
        Status = c.Status; WriteAllowed = c.WriteAllowed; Verified = c.Verified; WriteReason = c.WriteReason;
        if (string.IsNullOrEmpty(Name)) Name = c.Name;
    }

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _svc.Settings.ChannelNames[Key] = value;
        App.Store.SaveSettings(_svc.Settings);
    }

    [RelayCommand]
    private void Apply()
    {
        if (!HasChannel) return;
        var cfg = _svc.GetConfig(Channel.Id);
        cfg.Mode = Mode; cfg.ManualPercent = Math.Round(ManualPercent);
        _svc.SetConfig(Channel.Id, cfg);
        ApplyMessage = Mode == FanMode.Auto ? "Using hardware (BIOS) control."
            : _svc.Gate.IsAllowed(Channel.Id, _svc.Provider.Summary, out var why) ? "Saved. The app is applying it." : "Saved, but NOT applied: " + why;
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (!HasChannel || Busy) return;
        var ok = MessageBox.Show(
            $"This briefly drives \"{Name}\" to 40% and then 70% (about 20 seconds) to prove the motherboard really controls it, then returns it to BIOS control.\n\nProceed?",
            "Verify fan control", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        Busy = true; VerifyResult = "Testing... fan speed will change for ~20 s.";
        try
        {
            var r = await _svc.VerifyChannelAsync(Channel);
            VerifyResult = r.Message;
        }
        finally { Busy = false; }
    }
}

public sealed partial class FanControlViewModel : PageViewModel
{
    private readonly FanControlService _svc;
    public override string Title => "Fan Control";
    public ObservableCollection<FanRowViewModel> Rows { get; } = new();
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private string _note = "";

    public FanControlViewModel(FanControlService svc) { _svc = svc; }

    public override void OnSnapshot(Snapshot s)
    {
        ReadOnly = _svc.Gate.ReadOnlyMode;
        foreach (var c in s.Channels)
        {
            var key = c.Channel.Id.Length > 0 ? c.Channel.Id : c.Channel.RpmSensorId ?? c.Name;
            var row = Rows.FirstOrDefault(r => r.Key == key);
            if (row is null) Rows.Add(row = new FanRowViewModel(_svc, key, c.Channel) { Name = c.Name });
            row.Update(c);
        }
        Note = Rows.Count == 0 ? "No fan headers detected yet." :
            "Fan control stays OFF until: read-only mode is turned off in Settings, the app runs as Administrator, and each channel passes \"Verify\" " +
            "(the RPM must follow a test command). Anything unverified is left to the BIOS (Smart Fan 5).";
    }
}

public sealed record SourceOption(string Id, string Name);

public sealed partial class CurvesViewModel : PageViewModel
{
    private readonly FanControlService _svc;
    public override string Title => "Fan Curves";
    public ObservableCollection<FanRowViewModel> Fans { get; } = new();
    public ObservableCollection<SourceOption> Sources { get; } = new();
    private Snapshot? _last;

    [ObservableProperty] private FanRowViewModel? _selectedFan;
    [ObservableProperty] private SourceOption? _selectedSource;
    [ObservableProperty] private FanCurve _curve = FanCurve.Default();
    [ObservableProperty] private double _hysteresis = 2;
    [ObservableProperty] private double _rampUp = 2;
    [ObservableProperty] private double _rampDown = 10;
    [ObservableProperty] private double _minSafe = 20;
    [ObservableProperty] private double _critical = 85;
    [ObservableProperty] private double? _currentTemp;
    [ObservableProperty] private string _validation = "";
    [ObservableProperty] private string _message = "";

    public CurvesViewModel(FanControlService svc) { _svc = svc; }

    public override void OnSnapshot(Snapshot s)
    {
        _last = s;
        foreach (var c in s.Channels.Where(c => c.Channel.Id.Length > 0))
            if (Fans.All(f => f.Key != c.Channel.Id))
            {
                var row = new FanRowViewModel(_svc, c.Channel.Id, c.Channel) { Name = c.Name };
                Fans.Add(row);
            }
        foreach (var f in Fans) { var c = s.Channels.FirstOrDefault(x => x.Channel.Id == f.Key); if (c != null) f.Name = c.Name; }
        var temps = SensorCatalog.Temps(s.Readings).Where(t => t.Value != null).ToList();
        if (temps.Count != Sources.Count)
        {
            Sources.Clear();
            foreach (var t in temps) Sources.Add(new SourceOption(t.Id, $"{t.Hardware} - {t.Name}"));
            if (SelectedFan != null) LoadFan(SelectedFan);
        }
        if (SelectedFan is null && Fans.Count > 0) SelectedFan = Fans[0];
        CurrentTemp = SelectedSource is null ? SensorCatalog.CpuTemp(s.Readings)?.Value : s.Readings.FirstOrDefault(r => r.Id == SelectedSource.Id)?.Value;
    }

    partial void OnSelectedFanChanged(FanRowViewModel? value) { if (value != null) LoadFan(value); }

    private void LoadFan(FanRowViewModel f)
    {
        var cfg = _svc.GetConfig(f.Key);
        Curve = cfg.Mode is FanMode.Quiet or FanMode.Balanced or FanMode.Performance ? CurvePresets.For(cfg.Mode).Clone() : cfg.Curve.Clone();
        Hysteresis = cfg.HysteresisC; RampUp = cfg.RampUpSeconds; RampDown = cfg.RampDownSeconds; MinSafe = cfg.MinSafePercent; Critical = cfg.CriticalTempC;
        SelectedSource = cfg.SourceSensorId is null
            ? Sources.FirstOrDefault(s => s.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase) && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            : Sources.FirstOrDefault(s => s.Id == cfg.SourceSensorId);
        Validation = ""; Message = "";
    }

    [RelayCommand]
    private void Preset(string name)
    {
        var mode = Enum.Parse<FanMode>(name);
        Curve = CurvePresets.For(mode).Clone();
        Message = $"Loaded the {name} preset. Click Save curve to use it.";
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedFan is null) return;
        var errors = Curve.Validate(MinSafe);
        if (errors.Count > 0) { Validation = string.Join("\n", errors); Message = ""; return; }
        Validation = "";
        var cfg = _svc.GetConfig(SelectedFan.Key);
        cfg.Mode = FanMode.Curve; cfg.Curve = Curve.Clone(); cfg.SourceSensorId = SelectedSource?.Id;
        cfg.HysteresisC = Hysteresis; cfg.RampUpSeconds = RampUp; cfg.RampDownSeconds = RampDown; cfg.MinSafePercent = MinSafe; cfg.CriticalTempC = Critical;
        _svc.SetConfig(SelectedFan.Key, cfg);
        Message = _svc.Gate.IsAllowed(SelectedFan.Key, _svc.Provider.Summary, out var why)
            ? "Curve saved and active." : "Curve saved to the profile, but not applied to hardware: " + why;
    }

    [RelayCommand]
    private void NotifyCurveEdited()
    {
        Validation = string.Join("\n", Curve.Validate(MinSafe));
        OnPropertyChanged(nameof(PreviewText));
    }

    public string PreviewText => CurrentTemp is { } t ? $"At {Units.Temp(t)} this curve requests {Curve.Evaluate(t):0}%." : "No temperature available for preview.";
    partial void OnCurrentTempChanged(double? value) => OnPropertyChanged(nameof(PreviewText));
}
