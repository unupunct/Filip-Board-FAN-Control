using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilipBoard.App.Controls;
using FilipBoard.App.Services;
using FilipBoard.Core;
using FilipBoard.Data;
using Microsoft.Win32;

namespace FilipBoard.App.ViewModels;

// ---------------- Temperature monitor ----------------
public sealed partial class TempRowViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Hardware { get; init; } = "";
    [ObservableProperty] private string _current = "--";
    [ObservableProperty] private string _min = "--";
    [ObservableProperty] private string _max = "--";
    [ObservableProperty] private string _avg = "--";
    [ObservableProperty] private Brush _brush = Brushes.Gray;
    internal double MinV = double.MaxValue, MaxV = double.MinValue, Sum; internal long N;
}

public sealed partial class TemperatureViewModel : PageViewModel
{
    private readonly FanControlService _svc;
    private readonly Dictionary<string, List<double>> _hist = new();
    public override string Title => "Temperature Monitor";
    public ObservableCollection<TempRowViewModel> Rows { get; } = new();
    [ObservableProperty] private TempRowViewModel? _selected;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _series;
    [ObservableProperty] private string _thresholds = "";
    [ObservableProperty] private string _unit = "°C";

    public TemperatureViewModel(FanControlService svc) { _svc = svc; }

    public override void OnSnapshot(Snapshot s)
    {
        var st = _svc.Settings; Unit = Units.Suffix;
        Thresholds = $"Warning / critical (edit in Settings):  CPU {Units.Temp(st.CpuWarnC)} / {Units.Temp(st.CpuCritC)}   GPU {Units.Temp(st.GpuWarnC)} / {Units.Temp(st.GpuCritC)}   Board {Units.Temp(st.BoardWarnC)} / {Units.Temp(st.BoardCritC)}";
        foreach (var t in SensorCatalog.Temps(s.Readings))
        {
            var row = Rows.FirstOrDefault(r => r.Id == t.Id);
            if (row is null) Rows.Add(row = new TempRowViewModel { Id = t.Id, Name = t.Name, Hardware = t.Hardware });
            if (t.Value is not { } v) { row.Current = "n/a"; continue; }
            row.N++; row.Sum += v; row.MinV = Math.Min(row.MinV, v); row.MaxV = Math.Max(row.MaxV, v);
            row.Current = Units.Temp(v); row.Min = Units.Temp(row.MinV); row.Max = Units.Temp(row.MaxV); row.Avg = Units.Temp(row.Sum / row.N);
            var (w, c) = Limits(t);
            var lvl = TemperatureLevel.Classify(v, w, c);
            row.Brush = lvl == AlertLevel.Critical ? Brushes.OrangeRed : lvl == AlertLevel.Elevated ? Brushes.Orange : Brushes.LightGreen;
            if (!_hist.TryGetValue(t.Id, out var l)) _hist[t.Id] = l = new();
            l.Add(Units.Conv(v)); if (l.Count > 300) l.RemoveAt(0);
        }
        Selected ??= Rows.FirstOrDefault();
        Redraw();
    }

    private (double warn, double crit) Limits(SensorReading r)
    {
        var st = _svc.Settings;
        if (r.Id == SensorCatalog.GpuTemp(_svc.Latest?.Readings ?? Array.Empty<SensorReading>())?.Id) return (st.GpuWarnC, st.GpuCritC);
        if (r.Id.Contains("/lpc/") && !r.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) return (st.BoardWarnC, st.BoardCritC);
        return (st.CpuWarnC, st.CpuCritC);
    }

    partial void OnSelectedChanged(TempRowViewModel? value) => Redraw();

    private void Redraw()
    {
        if (Selected is null || !_hist.TryGetValue(Selected.Id, out var l)) { Series = null; return; }
        Series = new[] { new ChartSeries(Selected.Name, Color.FromRgb(0xFF, 0x3B, 0x4E), l.ToArray()) };
    }
}

// ---------------- Profiles ----------------
public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly FanControlService _svc;
    public override string Title => "Profiles";
    public ObservableCollection<FanProfile> Profiles { get; } = new();
    public Array RuleKinds { get; } = Enum.GetValues(typeof(RuleKind));
    [ObservableProperty] private FanProfile? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private RuleKind _ruleKind;
    [ObservableProperty] private string _ruleProcess = "";
    [ObservableProperty] private double _ruleThreshold;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _activeName = "";

    public ProfilesViewModel(FanControlService svc) { _svc = svc; Reload(); }

    public override void OnShown() => Reload();
    public override void OnSnapshot(Snapshot s) => ActiveName = _svc.ActiveProfile.Name;

    private void Reload()
    {
        var sel = Selected?.Name;
        Profiles.Clear();
        foreach (var p in _svc.Profiles) Profiles.Add(p);
        Selected = Profiles.FirstOrDefault(p => p.Name == sel) ?? Profiles.FirstOrDefault(p => p.Name == _svc.ActiveProfile.Name);
        ActiveName = _svc.ActiveProfile.Name;
    }

    partial void OnSelectedChanged(FanProfile? value)
    {
        if (value is null) return;
        NewName = value.Name; RuleKind = value.Rule.Kind; RuleProcess = value.Rule.Process; RuleThreshold = value.Rule.Threshold;
    }

    [RelayCommand]
    private void Activate()
    {
        if (Selected is null) return;
        _svc.SetActiveProfile(Selected); ActiveName = Selected.Name;
        Message = $"\"{Selected.Name}\" is now active.";
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        var name = Unique(Selected.Name + " copy");
        var copy = Selected.Clone(name);
        App.Store.SaveProfile(copy); _svc.ReloadProfiles(); Reload();
        Selected = Profiles.FirstOrDefault(p => p.Name == name); Message = $"Created \"{name}\".";
    }

    private string Unique(string baseName)
    {
        var n = baseName; var i = 2;
        while (_svc.Profiles.Any(p => p.Name == n)) n = $"{baseName} {i++}";
        return n;
    }

    [RelayCommand]
    private void Rename()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(NewName) || NewName == Selected.Name) return;
        if (Selected.BuiltIn || Selected.Name == "Custom") { Message = "Built-in profiles cannot be renamed. Duplicate it first."; return; }
        if (_svc.Profiles.Any(p => p.Name == NewName)) { Message = "That name is taken."; return; }
        var old = Selected.Name;
        var p = Selected; p.Name = NewName.Trim();
        App.Store.DeleteProfile(old); App.Store.SaveProfile(p);
        if (_svc.ActiveProfile.Name == old) _svc.SetActiveProfile(p);
        _svc.ReloadProfiles(); Reload(); Message = "Renamed.";
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null) return;
        if (Selected.BuiltIn || Selected.Name == "Custom") { Message = "Built-in profiles cannot be deleted."; return; }
        if (_svc.ActiveProfile.Name == Selected.Name) { Message = "Activate another profile first."; return; }
        App.Store.DeleteProfile(Selected.Name); _svc.ReloadProfiles(); Reload(); Message = "Deleted.";
    }

    [RelayCommand]
    private void SaveRule()
    {
        if (Selected is null) return;
        Selected.Rule = new ProfileRule { Kind = RuleKind, Process = RuleProcess.Trim(), Threshold = RuleThreshold };
        App.Store.SaveProfile(Selected);
        Message = _svc.Settings.AutoSelectProfile ? "Rule saved." : "Rule saved. Turn on \"Automatic profile selection\" in Settings to use rules.";
    }

    [RelayCommand]
    private void Export()
    {
        if (Selected is null) return;
        var dlg = new SaveFileDialog { Filter = "Fan profile (*.fbprofile.json)|*.fbprofile.json", FileName = Selected.Name + ".fbprofile.json" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, ProfileSerializer.ToJson(Selected));
        Message = "Exported.";
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Fan profile (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > 256 * 1024) throw new FormatException("File is too large to be a profile.");
            var p = ProfileSerializer.FromJson(File.ReadAllText(dlg.FileName));
            p.Name = Unique(p.Name);
            App.Store.SaveProfile(p); _svc.ReloadProfiles(); Reload();
            Selected = Profiles.FirstOrDefault(x => x.Name == p.Name);
            Message = $"Imported \"{p.Name}\" (values were range-checked).";
        }
        catch (Exception ex) when (ex is FormatException or IOException) { Message = "Import failed: " + ex.Message; }
    }
}

// ---------------- History & logs ----------------
public sealed record RangeOption(string Label, TimeSpan Span);

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly FanControlService _svc; private readonly SqliteStore _store;
    public override string Title => "History & Logs";
    public ObservableCollection<string> Sensors { get; } = new();
    public ObservableCollection<LogEntry> Log { get; } = new();
    public IReadOnlyList<RangeOption> Ranges { get; } = new[]
    {
        new RangeOption("Last hour", TimeSpan.FromHours(1)), new RangeOption("Last 24 hours", TimeSpan.FromHours(24)),
        new RangeOption("Last 7 days", TimeSpan.FromDays(7)), new RangeOption("Last 30 days", TimeSpan.FromDays(30)),
    };
    [ObservableProperty] private string? _sensor;
    [ObservableProperty] private RangeOption _range;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _series;
    [ObservableProperty] private string _info = "";

    public HistoryViewModel(FanControlService svc, SqliteStore store) { _svc = svc; _store = store; _range = Ranges[0]; }

    public override void OnShown() => Refresh();
    partial void OnSensorChanged(string? value) => Plot();
    partial void OnRangeChanged(RangeOption value) => Plot();

    [RelayCommand]
    private void Refresh()
    {
        var keep = Sensor;
        Sensors.Clear();
        foreach (var s in _store.KnownSensors()) Sensors.Add(s);
        Sensor = keep != null && Sensors.Contains(keep) ? keep : Sensors.FirstOrDefault(s => s.Contains("temperature", StringComparison.OrdinalIgnoreCase)) ?? Sensors.FirstOrDefault();
        Log.Clear();
        foreach (var e in _store.RecentLog(300)) Log.Add(e);
        Plot();
    }

    private void Plot()
    {
        if (Sensor is null) { Series = null; Info = _svc.Provider.Summary.IsMock ? "Demo mode: simulated data is never stored." : "No history stored yet."; return; }
        var pts = _store.QueryHistory(Sensor, DateTime.UtcNow - Range.Span, DateTime.UtcNow);
        Series = new[] { new ChartSeries(Sensor, Color.FromRgb(0x22, 0xD3, 0xEE), pts.Select(p => p.Value).ToArray()) };
        Info = pts.Count == 0 ? "No data in this range yet." : $"{pts.Count} points (averaged). Oldest: {pts[0].Utc.ToLocalTime():g}";
    }

    [RelayCommand]
    private void ExportHistory()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"fan-history-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (dlg.ShowDialog() == true) { _store.ExportHistoryCsv(dlg.FileName, DateTime.UtcNow - Range.Span, DateTime.UtcNow); Info = "History exported."; }
    }

    [RelayCommand]
    private void ExportLog()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"fan-log-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (dlg.ShowDialog() == true) { _store.ExportLogCsv(dlg.FileName); Info = "Log exported."; }
    }
}

// ---------------- Hardware information ----------------
public sealed record SensorRowInfo(string Hardware, string Name, string Kind, string Value, string Id);

public sealed partial class HardwareViewModel : PageViewModel
{
    private readonly FanControlService _svc;
    public override string Title => "Hardware Information";
    public ObservableCollection<SensorRowInfo> Sensors { get; } = new();
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _compat = "";
    [ObservableProperty] private string _message = "";
    private int _tick;

    public HardwareViewModel(FanControlService svc) { _svc = svc; }

    public override void OnSnapshot(Snapshot s)
    {
        if (_tick++ % 3 != 0 && Sensors.Count > 0) return;
        var hw = s.Hardware;
        Summary = $"Provider: {hw.Provider}\nMotherboard: {hw.Motherboard}\nSuper I/O chip(s): {(hw.SuperIoChips.Count == 0 ? "none detected" : string.Join(", ", hw.SuperIoChips))}\n" +
                  $"Administrator: {(hw.IsElevated ? "yes" : "no")}\nTarget board (Z370 AORUS Gaming 7): {(hw.IsTargetBoard ? "yes" : "NO")}\n" +
                  $"Fan headers seen: {s.Channels.Count}   Control channels: {_svc.Backend?.Channels.Count ?? 0}   Verified: {s.Channels.Count(c => c.Verified)}";
        var lines = new List<string>();
        if (hw.IsMock) lines.Add("DEMO MODE - nothing below describes real hardware.");
        if (!hw.IsMock && !hw.IsElevated) lines.Add("Run as Administrator to access the Super I/O chip.");
        if (!hw.IsMock && !hw.IsTargetBoard) lines.Add("This is not the Gigabyte Z370 AORUS Gaming 7 the app was designed for. Readings may work; control is unproven here.");
        if (!hw.IsMock && hw.IsElevated && hw.SuperIoChips.Count == 0) lines.Add("No Super I/O chip recognised: fan RPM and control are unavailable. Use BIOS Smart Fan 5 or GIGABYTE System Information Viewer.");
        if (_svc.Backend is null && hw.IsElevated && !hw.IsMock) lines.Add("The driver exposes no writable fan channel on this board: monitoring only. Control fans in the BIOS (Smart Fan 5) or GIGABYTE System Information Viewer.");
        if (s.Channels.Count == 1) lines.Add("Only one fan header reports RPM. If three fans share it via a splitter, they cannot be read or controlled separately.");
        foreach (var n in hw.Notes) lines.Add(n);
        Compat = string.Join("\n", lines.Distinct());
        Sensors.Clear();
        foreach (var r in s.Readings.OrderBy(r => r.Hardware).ThenBy(r => r.Kind).ThenBy(r => r.Name))
            Sensors.Add(new SensorRowInfo(r.Hardware, r.Name, r.Kind.ToString(), r.Value is { } v ? v.ToString("0.#") : "n/a", r.Id));
    }

    [RelayCommand]
    private void ExportReport()
    {
        var s = _svc.Latest; if (s is null) return;
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"filipboard-diagnostic-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dlg.ShowDialog() != true) return;
        var report = new
        {
            generatedLocal = DateTime.Now, hardware = s.Hardware, readOnly = _svc.Gate.ReadOnlyMode,
            channels = _svc.Backend?.Channels, verifications = _svc.Gate.Records, sensors = s.Readings,
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Message = "Diagnostic report saved (contains your motherboard model and sensor values).";
    }
}

// ---------------- Settings ----------------
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly FanControlService _svc; private readonly SqliteStore _store;
    private bool _loading = true;
    public override string Title => "Settings";
    public IReadOnlyList<int> RefreshOptions { get; } = new[] { 500, 1000, 2000, 5000 };

    [ObservableProperty] private bool _readOnlyMode;
    [ObservableProperty] private bool _fahrenheit;
    [ObservableProperty] private int _refreshMs;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _autoProfile;
    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private double _cpuWarn;
    [ObservableProperty] private double _cpuCrit;
    [ObservableProperty] private double _gpuWarn;
    [ObservableProperty] private double _gpuCrit;
    [ObservableProperty] private double _boardWarn;
    [ObservableProperty] private double _boardCrit;
    [ObservableProperty] private int _logInterval;
    [ObservableProperty] private int _retentionDays;
    [ObservableProperty] private string _message = "";

    public SettingsViewModel(FanControlService svc, SqliteStore store)
    {
        _svc = svc; _store = store; Load(); _loading = false;
    }

    private void Load()
    {
        var s = _svc.Settings;
        ReadOnlyMode = _svc.Gate.ReadOnlyMode; Fahrenheit = s.UseFahrenheit; RefreshMs = s.RefreshMs; StartWithWindows = s.StartWithWindows;
        MinimizeToTray = s.MinimizeToTray; StartMinimized = s.StartMinimized; AutoProfile = s.AutoSelectProfile; DemoMode = s.DemoMode;
        CpuWarn = s.CpuWarnC; CpuCrit = s.CpuCritC; GpuWarn = s.GpuWarnC; GpuCrit = s.GpuCritC; BoardWarn = s.BoardWarnC; BoardCrit = s.BoardCritC;
        LogInterval = s.LogIntervalSeconds; RetentionDays = s.RetentionDays;
    }

    private void Persist()
    {
        if (_loading) return;
        var s = _svc.Settings;
        s.UseFahrenheit = Fahrenheit; Units.Fahrenheit = Fahrenheit;
        s.RefreshMs = RefreshMs; s.MinimizeToTray = MinimizeToTray; s.StartMinimized = StartMinimized; s.AutoSelectProfile = AutoProfile;
        s.CpuWarnC = CpuWarn; s.CpuCritC = Math.Max(CpuCrit, CpuWarn); s.GpuWarnC = GpuWarn; s.GpuCritC = Math.Max(GpuCrit, GpuWarn);
        s.BoardWarnC = BoardWarn; s.BoardCritC = Math.Max(BoardCrit, BoardWarn);
        s.LogIntervalSeconds = Math.Clamp(LogInterval, 1, 3600); s.RetentionDays = Math.Clamp(RetentionDays, 1, 365);
        _store.SaveSettings(s);
    }

    partial void OnFahrenheitChanged(bool value) => Persist();
    partial void OnRefreshMsChanged(int value) => Persist();
    partial void OnMinimizeToTrayChanged(bool value) => Persist();
    partial void OnStartMinimizedChanged(bool value) => Persist();
    partial void OnAutoProfileChanged(bool value) => Persist();
    partial void OnCpuWarnChanged(double value) => Persist();
    partial void OnCpuCritChanged(double value) => Persist();
    partial void OnGpuWarnChanged(double value) => Persist();
    partial void OnGpuCritChanged(double value) => Persist();
    partial void OnBoardWarnChanged(double value) => Persist();
    partial void OnBoardCritChanged(double value) => Persist();
    partial void OnLogIntervalChanged(int value) => Persist();
    partial void OnRetentionDaysChanged(int value) => Persist();

    partial void OnReadOnlyModeChanged(bool value)
    {
        if (_loading) return;
        if (!value)
        {
            var ok = MessageBox.Show(
                "Turning off read-only mode lets this app send fan commands, but ONLY to channels that pass the verification test.\n\n" +
                "The BIOS keeps its own fan protection. If the app exits or fails, fans return to BIOS control.\n\nContinue?",
                "Allow fan control", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) { _loading = true; ReadOnlyMode = true; _loading = false; return; }
        }
        _svc.SetReadOnly(value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        _svc.Settings.StartWithWindows = value; Persist();
        Message = StartupManager.Apply(value, _svc.Provider.Summary.IsElevated);
    }

    partial void OnDemoModeChanged(bool value)
    {
        if (_loading) return;
        _svc.Settings.DemoMode = value; _store.SaveSettings(_svc.Settings);
        Message = "Demo mode " + (value ? "enabled" : "disabled") + ". Restart the app to apply. Demo data is simulated and clearly labelled.";
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var ok = MessageBox.Show("Reset all settings, custom profiles and fan verifications to defaults? History is kept.",
            "Restore defaults", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        _svc.SetReadOnly(true);
        foreach (var p in _store.LoadProfiles()) _store.DeleteProfile(p.Name);
        foreach (var v in _store.LoadVerifications()) { _store.DeleteVerification(v.ChannelId); _svc.Gate.Revoke(v.ChannelId); }
        var d = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties().Where(p => p.CanWrite)) prop.SetValue(_svc.Settings, prop.GetValue(d));
        _store.SaveSettings(_svc.Settings);
        _svc.ReloadProfiles(); _svc.SetActiveProfile(_svc.Profiles[1]);
        _loading = true; Load(); _loading = false; Units.Fahrenheit = false;
        Message = "Defaults restored (read-only mode is ON).";
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(_store.Path)!);
}
