using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilipBoard.Core;

public enum RuleKind { None, ProcessRunning, CpuLoadAbove, GpuTempAbove }

public sealed class ProfileRule
{
    public RuleKind Kind { get; set; } = RuleKind.None;
    public string Process { get; set; } = "";
    public double Threshold { get; set; }
}

public sealed class FanProfile
{
    public string Name { get; set; } = "Custom";
    public bool BuiltIn { get; set; }
    /// <summary>Config applied to every channel without its own entry.</summary>
    public ChannelConfig Default { get; set; } = new();
    public Dictionary<string, ChannelConfig> Channels { get; set; } = new();
    public ProfileRule Rule { get; set; } = new();

    public ChannelConfig For(string channelId) => Channels.TryGetValue(channelId, out var c) ? c : Default;

    public FanProfile Clone(string? newName = null)
    {
        var p = new FanProfile
        {
            Name = newName ?? Name, BuiltIn = false, Default = Default.Clone(),
            Rule = new ProfileRule { Kind = Rule.Kind, Process = Rule.Process, Threshold = Rule.Threshold },
        };
        foreach (var kv in Channels) p.Channels[kv.Key] = kv.Value.Clone();
        return p;
    }
}

public static class BuiltInProfiles
{
    public static List<FanProfile> Create() => new()
    {
        Make("Silent", FanMode.Quiet, null),
        Make("Balanced", FanMode.Balanced, null),
        Make("Gaming", FanMode.Curve, new FanCurve { Points = { new(25, 35), new(40, 45), new(50, 60), new(60, 78), new(70, 95), new(75, 100) } },
            new ProfileRule { Kind = RuleKind.GpuTempAbove, Threshold = 70 }),
        Make("Performance", FanMode.Performance, null),
        // Starts on hardware control: nothing is written until the user configures and verifies.
        Make("Custom", FanMode.Auto, null),
    };

    private static FanProfile Make(string name, FanMode mode, FanCurve? curve, ProfileRule? rule = null)
    {
        var cfg = new ChannelConfig { Mode = mode };
        if (curve != null) cfg.Curve = curve;
        return new FanProfile { Name = name, BuiltIn = name != "Custom", Default = cfg, Rule = rule ?? new() };
    }
}

public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(FanProfile p) => JsonSerializer.Serialize(p, Opts);

    /// <summary>Parses and sanitises an imported profile. Throws FormatException on bad input.</summary>
    public static FanProfile FromJson(string json)
    {
        FanProfile? p;
        try { p = JsonSerializer.Deserialize<FanProfile>(json, Opts); }
        catch (JsonException ex) { throw new FormatException("Not a valid profile file: " + ex.Message); }
        if (p is null || string.IsNullOrWhiteSpace(p.Name)) throw new FormatException("Profile has no name.");
        p.BuiltIn = false;
        Sanitise(p.Default);
        foreach (var c in p.Channels.Values) Sanitise(c);
        return p;
    }

    private static void Sanitise(ChannelConfig c)
    {
        c.MinSafePercent = Math.Clamp(c.MinSafePercent, 0, 100);
        if (!c.AllowFanStop) c.MinSafePercent = Math.Max(c.MinSafePercent, 10);
        c.ManualPercent = Math.Clamp(c.ManualPercent, 0, 100);
        c.HysteresisC = Math.Clamp(c.HysteresisC, 0, 15);
        c.RampUpSeconds = Math.Clamp(c.RampUpSeconds, 0, 120);
        c.RampDownSeconds = Math.Clamp(c.RampDownSeconds, 0, 600);
        c.CriticalTempC = Math.Clamp(c.CriticalTempC, 60, 100);
        c.Curve = c.Curve.Normalized(c.AllowFanStop ? 0 : c.MinSafePercent);
        if (c.Curve.Points.Count < 2) c.Curve = FanCurve.Default();
    }
}

public sealed record SelectionContext(IReadOnlySet<string> RunningProcesses, double CpuLoad, double? GpuTempC);

public static class ProfileSelector
{
    /// <summary>First profile (in list order) whose rule matches, else null.</summary>
    public static FanProfile? Select(IEnumerable<FanProfile> profiles, SelectionContext ctx)
    {
        foreach (var p in profiles)
        {
            var r = p.Rule;
            var match = r.Kind switch
            {
                RuleKind.ProcessRunning => !string.IsNullOrWhiteSpace(r.Process) &&
                    ctx.RunningProcesses.Contains(r.Process.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
                RuleKind.CpuLoadAbove => ctx.CpuLoad >= r.Threshold,
                RuleKind.GpuTempAbove => ctx.GpuTempC is { } g && g >= r.Threshold,
                _ => false,
            };
            if (match) return p;
        }
        return null;
    }
}

public sealed class AppSettings
{
    public bool ReadOnlyMode { get; set; } = true;
    public bool UseFahrenheit { get; set; }
    public int RefreshMs { get; set; } = 1000;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AutoSelectProfile { get; set; }
    public string ActiveProfile { get; set; } = "Balanced";
    public double CpuWarnC { get; set; } = 75;
    public double CpuCritC { get; set; } = 90;
    public double GpuWarnC { get; set; } = 75;
    public double GpuCritC { get; set; } = 88;
    public double BoardWarnC { get; set; } = 50;
    public double BoardCritC { get; set; } = 65;
    public int LogIntervalSeconds { get; set; } = 5;
    public int RetentionDays { get; set; } = 30;
    /// <summary>Explicit, user-initiated demo mode with simulated data. Always shown with a banner.</summary>
    public bool DemoMode { get; set; }
    /// <summary>channelId -> user-chosen display name (e.g. "Case Fan 1 - ARCTIC P14").</summary>
    public Dictionary<string, string> ChannelNames { get; set; } = new();
    /// <summary>channelId -> physical fan count behind that header (splitter), for honest UI text.</summary>
    public Dictionary<string, int> FansPerChannel { get; set; } = new();
}
