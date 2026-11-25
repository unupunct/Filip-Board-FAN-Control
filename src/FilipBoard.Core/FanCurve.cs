namespace FilipBoard.Core;

public sealed record CurvePoint(double TempC, double Percent);

public sealed class FanCurve
{
    public List<CurvePoint> Points { get; set; } = new();

    public static FanCurve Default() => new()
    {
        Points =
        {
            new(25, 25), new(35, 30), new(45, 40), new(55, 55), new(65, 75), new(75, 100),
        },
    };

    public FanCurve Clone() => new() { Points = Points.Select(p => p with { }).ToList() };

    /// <summary>Linear interpolation; flat before the first and after the last point.</summary>
    public double Evaluate(double tempC)
    {
        var pts = Points.OrderBy(p => p.TempC).ToList();
        if (pts.Count == 0) return 100; // fail safe: an empty curve must never mean "slow"
        if (tempC <= pts[0].TempC) return pts[0].Percent;
        if (tempC >= pts[^1].TempC) return pts[^1].Percent;
        for (var i = 1; i < pts.Count; i++)
        {
            if (tempC <= pts[i].TempC)
            {
                var a = pts[i - 1]; var b = pts[i];
                var span = b.TempC - a.TempC;
                if (span <= 0) return b.Percent;
                return a.Percent + (b.Percent - a.Percent) * (tempC - a.TempC) / span;
            }
        }
        return pts[^1].Percent;
    }

    public IReadOnlyList<string> Validate(double minSafePercent)
    {
        var errors = new List<string>();
        if (Points.Count < 2) errors.Add("A curve needs at least 2 points.");
        var sorted = Points.OrderBy(p => p.TempC).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            if (p.Percent < 0 || p.Percent > 100) errors.Add($"Point {p.TempC}°C: speed must be 0-100%.");
            if (p.Percent < minSafePercent) errors.Add($"Point {p.TempC}°C: below minimum safe speed ({minSafePercent:0}%).");
            if (i > 0 && p.TempC == sorted[i - 1].TempC) errors.Add($"Duplicate temperature {p.TempC}°C.");
            if (i > 0 && p.Percent < sorted[i - 1].Percent) errors.Add($"Speed decreases at {p.TempC}°C; curves must not drop as temperature rises.");
        }
        if (sorted.Count > 0 && sorted[^1].Percent < 100 && sorted[^1].TempC < 90)
            errors.Add("The curve should reach 100% by 90°C or below.");
        return errors;
    }

    /// <summary>Sorted copy with speeds clamped to [minSafe,100] and forced non-decreasing.</summary>
    public FanCurve Normalized(double minSafePercent)
    {
        var result = new FanCurve();
        double last = minSafePercent;
        foreach (var p in Points.OrderBy(p => p.TempC))
        {
            var pct = Math.Clamp(p.Percent, minSafePercent, 100);
            pct = Math.Max(pct, last);
            result.Points.Add(new CurvePoint(p.TempC, pct));
            last = pct;
        }
        return result;
    }
}

public enum FanMode { Auto, Manual, Curve, Quiet, Balanced, Performance, Max }

public static class CurvePresets
{
    public static FanCurve For(FanMode mode) => mode switch
    {
        FanMode.Quiet => new FanCurve { Points = { new(30, 20), new(45, 28), new(55, 38), new(65, 55), new(75, 80), new(85, 100) } },
        FanMode.Performance => new FanCurve { Points = { new(25, 40), new(35, 50), new(45, 62), new(55, 78), new(65, 92), new(70, 100) } },
        _ => FanCurve.Default(), // Balanced
    };
}

public sealed class ChannelConfig
{
    public FanMode Mode { get; set; } = FanMode.Auto;
    public double ManualPercent { get; set; } = 40;
    public FanCurve Curve { get; set; } = FanCurve.Default();
    public string? SourceSensorId { get; set; }
    public double HysteresisC { get; set; } = 2;
    public double RampUpSeconds { get; set; } = 2;
    public double RampDownSeconds { get; set; } = 10;
    public double MinSafePercent { get; set; } = 20;
    /// <summary>Fan stop is OFF unless the user explicitly enables it.</summary>
    public bool AllowFanStop { get; set; } = false;
    public double CriticalTempC { get; set; } = 85;

    public ChannelConfig Clone()
    {
        var c = (ChannelConfig)MemberwiseClone();
        c.Curve = Curve.Clone();
        return c;
    }
}
