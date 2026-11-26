namespace FilipBoard.Core;

/// <summary>
/// Hand-collected knowledge for the author's Gigabyte Z370 AORUS Gaming 7. Channel labels come from matching
/// the RPM shown in the BIOS (Smart Fan 5) against the app's readings; they are PROBABLE, not proven, and a
/// user-chosen name always wins. Nothing here enables fan control: the write gate and verification still apply.
/// </summary>
public static class BoardPresets
{
    public const string Fingerprint = "Gigabyte Z370 AORUS Gaming 7|ITE IT8686E,ITE IT8791E/IT8792E/IT8795E";
    public const string ProfileName = "BIOS replica (draft)";

    /// <summary>Channel id -> probable header (matched by RPM against the BIOS: 920/957/1110).</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["/lpc/it8686e/0/control/0"] = "CPU_OPT (top exhaust)",   // ~922 RPM vs BIOS 920
        ["/lpc/it8686e/0/control/1"] = "SYS_FAN1 (rear exhaust)", // ~958 RPM vs BIOS 957
        ["/lpc/it8792e/0/control/2"] = "SYS_FAN4 (bottom intake)", // ~1108 RPM vs BIOS 1110
    };

    public static string? LabelFor(string fingerprint, string channelId) =>
        fingerprint == Fingerprint && Labels.TryGetValue(channelId, out var l) ? l : null;

    /// <summary>The user's BIOS curves for the three channels that could be matched. Everything else stays on BIOS control.</summary>
    public static FanProfile BiosReplica()
    {
        static ChannelConfig Cfg(string source, double minSafe, params (double t, double p)[] pts)
        {
            var c = new ChannelConfig { Mode = FanMode.Curve, SourceSensorId = source, MinSafePercent = minSafe, AllowFanStop = false };
            c.Curve = new FanCurve { Points = pts.Select(x => new CurvePoint(x.t, x.p)).ToList() };
            return c;
        }
        const string cpu = "/intelcpu/0/temperature/8";   // CPU Package
        const string gpu = "/gpu-nvidia/0/temperature/0"; // stand-in for the BIOS "PCIEX16" sensor
        var p = new FanProfile { Name = ProfileName, BuiltIn = false, Default = new ChannelConfig { Mode = FanMode.Auto } };
        p.Channels["/lpc/it8686e/0/control/0"] = Cfg(cpu, 25, (40, 25), (55, 39), (65, 54), (75, 78), (85, 100));
        p.Channels["/lpc/it8686e/0/control/1"] = Cfg(cpu, 25, (40, 25), (55, 39), (65, 54), (75, 74), (85, 100));
        p.Channels["/lpc/it8792e/0/control/2"] = Cfg(gpu, 30, (35, 30), (45, 40), (55, 54), (65, 74), (75, 100));
        return p;
    }
}
