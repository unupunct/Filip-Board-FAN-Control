using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FilipBoard.App.Controls;
using FilipBoard.App.Services;
using FilipBoard.Core;

namespace FilipBoard.App.ViewModels;

public sealed partial class FanCardViewModel : ObservableObject
{
    public string Key { get; init; } = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _rpmText = "--";
    [ObservableProperty] private string _percentText = "n/a";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private Brush _levelBrush = Brushes.Gray;
    [ObservableProperty] private double _rpmBar;
}

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly FanControlService _svc;
    private readonly Dictionary<string, List<double>> _hist = new();
    public override string Title => "Dashboard";

    [ObservableProperty] private double? _cpuTemp;
    [ObservableProperty] private string _cpuTempText = "--";
    [ObservableProperty] private double? _gpuTemp;
    [ObservableProperty] private string _gpuTempText = "--";
    [ObservableProperty] private double? _boardTemp;
    [ObservableProperty] private string _boardTempText = "--";
    [ObservableProperty] private double? _cpuLoad;
    [ObservableProperty] private string _cpuLoadText = "--";
    [ObservableProperty] private double? _gpuLoad;
    [ObservableProperty] private string _gpuLoadText = "--";
    [ObservableProperty] private int _totalFans;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _fanNotice = "";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _series;
    [ObservableProperty] private double _cpuWarn;
    [ObservableProperty] private double _cpuCrit;
    [ObservableProperty] private double _gpuWarn;
    [ObservableProperty] private double _gpuCrit;
    [ObservableProperty] private double _boardWarn;
    [ObservableProperty] private double _boardCrit;
    [ObservableProperty] private string _unit = "°C";

    public ObservableCollection<FanCardViewModel> Fans { get; } = new();

    public DashboardViewModel(FanControlService svc) { _svc = svc; }

    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24));
    private static readonly Brush Crit = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x4E));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x9A));

    private void Push(string key, double? v)
    {
        if (!_hist.TryGetValue(key, out var l)) _hist[key] = l = new List<double>();
        l.Add(v is null ? double.NaN : Units.Conv(v.Value));
        if (l.Count > 180) l.RemoveAt(0);
    }

    public override void OnSnapshot(Snapshot s)
    {
        var st = _svc.Settings;
        Unit = Units.Suffix;
        CpuWarn = st.CpuWarnC; CpuCrit = st.CpuCritC; GpuWarn = st.GpuWarnC; GpuCrit = st.GpuCritC; BoardWarn = st.BoardWarnC; BoardCrit = st.BoardCritC;

        var cpu = SensorCatalog.CpuTemp(s.Readings)?.Value; var gpu = SensorCatalog.GpuTemp(s.Readings)?.Value;
        var board = SensorCatalog.BoardTemp(s.Readings)?.Value;
        CpuTemp = cpu; CpuTempText = Units.Temp(cpu);
        GpuTemp = gpu; GpuTempText = Units.Temp(gpu);
        BoardTemp = board; BoardTempText = Units.Temp(board);
        CpuLoad = SensorCatalog.CpuLoad(s.Readings)?.Value; CpuLoadText = CpuLoad is null ? "--" : $"{CpuLoad:0}%";
        GpuLoad = SensorCatalog.GpuLoad(s.Readings)?.Value; GpuLoadText = GpuLoad is null ? "--" : $"{GpuLoad:0}%";
        ProfileName = _svc.ActiveProfile.Name;
        TotalFans = s.Channels.Count;

        Push("CPU", cpu); Push("GPU", gpu); Push("Board", board);
        Series = new[]
        {
            new ChartSeries("CPU", Color.FromRgb(0xFF, 0x3B, 0x4E), _hist["CPU"].ToArray()),
            new ChartSeries("GPU", Color.FromRgb(0x22, 0xD3, 0xEE), _hist["GPU"].ToArray()),
            new ChartSeries("Board", Color.FromRgb(0xF5, 0xA5, 0x24), _hist["Board"].ToArray()),
        };

        // sync cards
        foreach (var c in s.Channels)
        {
            var key = c.Channel.Id.Length > 0 ? c.Channel.Id : c.Channel.RpmSensorId ?? c.Name;
            var card = Fans.FirstOrDefault(f => f.Key == key);
            if (card is null) Fans.Add(card = new FanCardViewModel { Key = key });
            card.Name = c.Name;
            card.Header = c.Channel.Id.Length > 0 ? $"Channel: {Services.FanControlService.QualifiedName(c.Channel.Id, c.Channel.Name)}" : "Header: (monitor only)";
            card.RpmText = c.Rpm is { } r ? $"{r:0}" : "--";
            card.RpmBar = Math.Min(100, (c.Rpm ?? 0) / 20.0);
            card.PercentText = c.CommandedPercent is { } cp ? $"{cp:0}% (commanded)" : c.ReportedPercent is { } rp ? $"{rp:0}% (reported)" : "n/a";
            card.SourceText = c.Channel.Id.Length > 0 ? $"Source: {c.SourceName} {Units.Temp(c.SourceTempC)}" : "Source: n/a";
            card.ModeText = c.Channel.Id.Length > 0 ? c.Mode.ToString() : "Monitor";
            card.Status = c.Status;
            var lvl = c.SourceTempC is { } t ? TemperatureLevel.Classify(t, st.CpuWarnC, st.CpuCritC) : AlertLevel.Normal;
            card.LevelBrush = c.Rpm is null ? Idle : lvl == AlertLevel.Critical ? Crit : lvl == AlertLevel.Elevated ? Warn : Ok;
        }
        for (var i = Fans.Count - 1; i >= 0; i--)
            if (s.Channels.All(c => (c.Channel.Id.Length > 0 ? c.Channel.Id : c.Channel.RpmSensorId ?? c.Name) != Fans[i].Key)) Fans.RemoveAt(i);

        FanNotice = s.Channels.Count == 0
            ? (s.Hardware.IsMock ? "" : "No fans detected. Fan RPM comes from the motherboard Super I/O chip, which needs Administrator rights and a supported chip (see Hardware Information).")
            : "The motherboard sees one RPM reading per fan header. Several fans on a splitter or daisy chain appear as ONE fan, and cannot be read or controlled individually.";
    }
}
