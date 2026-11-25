using System.Security.Principal;
using FilipBoard.Core;
using LibreHardwareMonitor.Hardware;

namespace FilipBoard.Hardware;

/// <summary>LibreHardwareMonitor-backed sensor provider. Read-only; control lives in <see cref="LhmFanBackend"/>.</summary>
public sealed class LhmSensorProvider : ISensorProvider
{
    private readonly Computer _computer;
    internal readonly object Gate = new();
    private readonly List<string> _notes = new();

    public HardwareSummary Summary { get; }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public LhmSensorProvider()
    {
        var elevated = IsElevated();
        _computer = new Computer { IsMotherboardEnabled = true, IsCpuEnabled = true, IsGpuEnabled = true };
        try { _computer.Open(); }
        catch (Exception ex) { _notes.Add("Hardware access failed to open: " + ex.Message); }

        if (!elevated) _notes.Add("Not running as Administrator: Super I/O (fans, board temperatures) and CPU temperatures are unavailable.");

        var board = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard)?.Name ?? "Unknown motherboard";
        var chips = _computer.Hardware.SelectMany(Flatten).Where(h => h.HardwareType == HardwareType.SuperIO).Select(h => h.Name).Distinct().ToList();
        if (chips.Count == 0) _notes.Add("No Super I/O chip was recognised; fan RPM and control are unavailable.");
        Summary = new HardwareSummary("LibreHardwareMonitor", false, elevated, board, chips, _notes);
    }

    internal IEnumerable<IHardware> AllHardware() => _computer.Hardware.SelectMany(Flatten);

    private static IEnumerable<IHardware> Flatten(IHardware h)
    {
        yield return h;
        foreach (var s in h.SubHardware) foreach (var x in Flatten(s)) yield return x;
    }

    public IReadOnlyList<SensorReading> Poll()
    {
        var list = new List<SensorReading>();
        lock (Gate)
        {
            foreach (var hw in AllHardware())
            {
                try { hw.Update(); } catch { continue; }
                foreach (var s in hw.Sensors)
                {
                    var kind = s.SensorType switch
                    {
                        SensorType.Temperature => SensorKind.Temperature,
                        SensorType.Fan => SensorKind.FanRpm,
                        SensorType.Load => SensorKind.Load,
                        SensorType.Control => SensorKind.ControlPercent,
                        _ => SensorKind.Other,
                    };
                    if (kind == SensorKind.Other) continue;
                    list.Add(new SensorReading(s.Identifier.ToString(), s.Name, hw.Name, kind, s.Value));
                }
            }
        }
        return list;
    }

    public void Dispose() { try { _computer.Close(); } catch { } }
}

/// <summary>
/// Control channels exposed by Super I/O chips via LibreHardwareMonitor. Exposure != verified;
/// the WriteGate and FanWriteVerifier decide whether it is ever used.
/// </summary>
public sealed class LhmFanBackend : IFanBackend
{
    private readonly LhmSensorProvider _provider;
    private readonly Dictionary<string, ISensor> _controls = new();
    public IReadOnlyList<FanChannel> Channels { get; }

    public LhmFanBackend(LhmSensorProvider provider)
    {
        _provider = provider;
        var channels = new List<FanChannel>();
        lock (provider.Gate)
        {
            foreach (var hw in provider.AllHardware().Where(h => h.HardwareType == HardwareType.SuperIO))
            {
                hw.Update();
                var fanIds = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).ToDictionary(s => s.Index, s => s.Identifier.ToString());
                foreach (var c in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null))
                {
                    var id = c.Identifier.ToString();
                    _controls[id] = c;
                    channels.Add(new FanChannel(id, c.Name, fanIds.TryGetValue(c.Index, out var rpm) ? rpm : null));
                }
            }
        }
        Channels = channels;
    }

    public bool TrySetPercent(string channelId, double percent, out string error)
    {
        error = "";
        if (!_controls.TryGetValue(channelId, out var s) || s.Control is null) { error = "Unknown channel."; return false; }
        try
        {
            lock (_provider.Gate) s.Control.SetSoftware((float)Math.Clamp(percent, 0, 100));
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public void RestoreAutomatic(string? channelId = null)
    {
        lock (_provider.Gate)
        {
            foreach (var kv in _controls.Where(kv => channelId == null || kv.Key == channelId))
            {
                try { kv.Value.Control?.SetDefault(); } catch { /* best effort; the app also restores on exit */ }
            }
        }
    }

    public void Dispose() => RestoreAutomatic();
}
