using FilipBoard.Core;

namespace FilipBoard.Hardware;

/// <summary>
/// DEMO ONLY. Simulated sensors and fans, used only when the user explicitly enables Demo mode.
/// Every name is prefixed "[MOCK]" and every reading has IsMock=true; mock data is never written to history.
/// </summary>
public sealed class MockHardware : ISensorProvider, IFanBackend
{
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly double[] _percent = { 40, 40, 40 };
    private readonly bool[] _auto = { true, true, true };
    private readonly double[] _rpm = { 600, 600, 600 };

    public HardwareSummary Summary { get; } = new("MOCK (simulated)", true, true, "MOCK DATA - not real hardware",
        new[] { "MOCK Super I/O" }, new[] { "Demo mode: all values are simulated." });

    public IReadOnlyList<FanChannel> Channels { get; } = new[]
    {
        new FanChannel("mock/ctl/0", "[MOCK] Channel 1", "mock/fan/0"),
        new FanChannel("mock/ctl/1", "[MOCK] Channel 2", "mock/fan/1"),
        new FanChannel("mock/ctl/2", "[MOCK] Channel 3", "mock/fan/2"),
    };

    public IReadOnlyList<SensorReading> Poll()
    {
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        double cpu = 45 + 18 * Math.Sin(t / 25) + 4 * Math.Sin(t / 3);
        double gpu = 52 + 15 * Math.Sin(t / 40 + 1);
        var list = new List<SensorReading>
        {
            new("mock/temp/cpu", "[MOCK] CPU Package", "[MOCK]", SensorKind.Temperature, cpu, true),
            new("mock/temp/gpu", "[MOCK] GPU Core", "[MOCK]", SensorKind.Temperature, gpu, true),
            new("mock/temp/board", "[MOCK] Motherboard", "[MOCK]", SensorKind.Temperature, 33 + 3 * Math.Sin(t / 60), true),
            new("mock/load/cpu", "[MOCK] CPU Total", "[MOCK]", SensorKind.Load, 30 + 25 * Math.Sin(t / 20), true),
            new("mock/load/gpu", "[MOCK] GPU Core", "[MOCK]", SensorKind.Load, 20 + 15 * Math.Sin(t / 30), true),
        };
        for (var i = 0; i < 3; i++)
        {
            var pct = _auto[i] ? Math.Clamp((25 + (cpu - 30) * 1.4) * (0.85 + 0.15 * i), 25, 100) : _percent[i];
            _rpm[i] += (pct * 17 - _rpm[i]) * 0.35; // first-order spin up/down
            list.Add(new($"mock/fan/{i}", $"[MOCK] Fan {i + 1}", "[MOCK]", SensorKind.FanRpm, _rpm[i], true));
            list.Add(new($"mock/ctl/{i}", $"[MOCK] Control {i + 1}", "[MOCK]", SensorKind.ControlPercent, pct, true));
        }
        return list;
    }

    public bool TrySetPercent(string channelId, double percent, out string error)
    {
        error = "";
        var i = Array.FindIndex(Channels.ToArray(), c => c.Id == channelId);
        if (i < 0) { error = "Unknown channel."; return false; }
        _auto[i] = false; _percent[i] = percent;
        return true;
    }

    public void RestoreAutomatic(string? channelId = null)
    {
        for (var i = 0; i < 3; i++) if (channelId == null || Channels[i].Id == channelId) _auto[i] = true;
    }

    public void Dispose() { }
}
