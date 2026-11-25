using FilipBoard.Core;
using Xunit;

namespace FilipBoard.Tests;

public class CurveTests
{
    [Theory]
    [InlineData(20, 25)]
    [InlineData(25, 25)]
    [InlineData(30, 27.5)]
    [InlineData(60, 65)]
    [InlineData(75, 100)]
    [InlineData(99, 100)]
    public void DefaultCurveInterpolates(double t, double expected) =>
        Assert.Equal(expected, FanCurve.Default().Evaluate(t), 3);

    [Fact]
    public void EmptyCurveFailsSafeToFull() => Assert.Equal(100, new FanCurve().Evaluate(30));

    [Fact]
    public void ValidateRejectsDecreasingAndBelowMin()
    {
        var c = new FanCurve { Points = { new(30, 50), new(40, 30), new(50, 5) } };
        var errors = c.Validate(20);
        Assert.Contains(errors, e => e.Contains("decreases"));
        Assert.Contains(errors, e => e.Contains("minimum safe"));
    }

    [Fact]
    public void DefaultCurveIsValid() => Assert.Empty(FanCurve.Default().Validate(20));

    [Fact]
    public void NormalizedClampsAndIsMonotonic()
    {
        var n = new FanCurve { Points = { new(50, 5), new(30, 60), new(40, 30) } }.Normalized(20);
        Assert.Equal(new[] { 30.0, 40.0, 50.0 }, n.Points.Select(p => p.TempC));
        Assert.Equal(new[] { 60.0, 60.0, 60.0 }, n.Points.Select(p => p.Percent));
    }
}

public class GovernorTests
{
    private static ChannelConfig Cfg() => new() { Mode = FanMode.Curve, RampUpSeconds = 2, RampDownSeconds = 10, HysteresisC = 3 };

    [Fact]
    public void AutoModeReleasesToHardware()
    {
        var d = new CurveGovernor().Decide(new ChannelConfig { Mode = FanMode.Auto }, 50, 0);
        Assert.True(d.ReleaseToAuto);
        Assert.Null(d.Percent);
    }

    [Fact]
    public void MissingSensorReleasesToHardwareNeverStops()
    {
        var d = new CurveGovernor().Decide(Cfg(), null, 0);
        Assert.True(d.ReleaseToAuto);
    }

    [Fact]
    public void ManualIsClampedToMinSafe()
    {
        var d = new CurveGovernor().Decide(new ChannelConfig { Mode = FanMode.Manual, ManualPercent = 0, MinSafePercent = 25 }, 40, 0);
        Assert.Equal(25, d.Percent);
    }

    [Fact]
    public void EmergencyUsesRawTempAndIsImmediate()
    {
        var g = new CurveGovernor();
        g.Decide(Cfg(), 30, 0);
        var d = g.Decide(Cfg(), 90, 1); // raw 90 >= 85 although smoothed is far lower
        Assert.True(d.Emergency);
        Assert.Equal(100, d.Percent);
    }

    [Fact]
    public void RampUpWaitsForDelay()
    {
        var g = new CurveGovernor();
        var first = g.Decide(Cfg(), 30, 0).Percent!.Value;
        var pending = g.Decide(Cfg(), 70, 1).Percent!.Value;   // smoothing + delay not elapsed
        Assert.Equal(first, pending);
        double last = pending;
        for (var t = 2; t < 12; t++) last = g.Decide(Cfg(), 70, t).Percent!.Value;
        Assert.True(last > first);
    }

    [Fact]
    public void RampDownNeedsHysteresisAndDelay()
    {
        var g = new CurveGovernor();
        var cfg = Cfg();
        for (var t = 0; t < 30; t++) g.Decide(cfg, 65, t);
        var high = g.AppliedPercent!.Value;
        // small dip inside hysteresis: no change even after a long time
        for (var t = 30; t < 80; t++) g.Decide(cfg, 64, t);
        Assert.Equal(high, g.AppliedPercent!.Value);
        // big drop sustained: ramps down
        for (var t = 80; t < 160; t++) g.Decide(cfg, 40, t);
        Assert.True(g.AppliedPercent < high);
    }

    [Fact]
    public void FanStopNeverHappensByDefault()
    {
        var g = new CurveGovernor();
        var cfg = new ChannelConfig { Mode = FanMode.Curve, MinSafePercent = 0, Curve = new FanCurve { Points = { new(20, 0), new(80, 100) } } };
        var d = g.Decide(cfg, 20, 0);
        Assert.True(d.Percent >= 10);
    }
}

public class SafetyTests
{
    private static HardwareSummary Hw(bool elevated = true, string board = "Z370 AORUS Gaming 7-CF", bool mock = false) =>
        new("test", mock, elevated, board, new[] { "IT8686E" }, Array.Empty<string>());

    [Fact]
    public void WriteGateDefaultsToBlocked()
    {
        var gate = new WriteGate();
        Assert.False(gate.IsAllowed("ch1", Hw(), out var why));
        Assert.Contains("Read-only", why);
    }

    [Fact]
    public void WriteGateRequiresVerificationForThisFingerprint()
    {
        var gate = new WriteGate { ReadOnlyMode = false };
        Assert.False(gate.IsAllowed("ch1", Hw(), out _));
        gate.Record(new VerificationRecord("ch1", Hw().Fingerprint, DateTime.UtcNow));
        Assert.True(gate.IsAllowed("ch1", Hw(), out _));
        Assert.False(gate.IsAllowed("ch1", Hw(board: "Other board"), out _)); // different board invalidates
        Assert.False(gate.IsAllowed("ch1", Hw(elevated: false), out _));
    }

    [Fact]
    public void WatchdogExpires()
    {
        var now = DateTime.UtcNow;
        var wd = new Watchdog(TimeSpan.FromSeconds(5), () => now);
        Assert.False(wd.IsExpired);
        now = now.AddSeconds(6);
        Assert.True(wd.IsExpired);
        wd.Beat();
        Assert.False(wd.IsExpired);
    }

    [Fact]
    public void HealthMonitorReportsStallOnce()
    {
        var m = new FanHealthMonitor(stallSeconds: 5);
        var events = new List<HealthEvent>();
        for (var t = 0; t < 20; t++) events.AddRange(m.Observe("f1", 0, 50, t, DateTime.UtcNow));
        Assert.Single(events);
        Assert.Equal(HealthEventKind.FanStalled, events[0].Kind);
    }

    [Fact]
    public void AlertClassification()
    {
        Assert.Equal(AlertLevel.Normal, TemperatureLevel.Classify(50, 70, 85));
        Assert.Equal(AlertLevel.Elevated, TemperatureLevel.Classify(70, 70, 85));
        Assert.Equal(AlertLevel.Critical, TemperatureLevel.Classify(90, 70, 85));
    }
}

public class VerifierTests
{
    private sealed class FakeBackend : IFanBackend
    {
        public double Commanded; public bool Restored; public bool Follows = true;
        public IReadOnlyList<FanChannel> Channels { get; } = new[] { new FanChannel("c1", "C1", "rpm1") };
        public bool TrySetPercent(string id, double p, out string e) { Commanded = p; e = ""; return true; }
        public void RestoreAutomatic(string? id = null) => Restored = true;
        public void Dispose() { }
    }

    [Fact]
    public async Task ConfirmsWhenRpmFollowsAndRestores()
    {
        var b = new FakeBackend();
        var v = new FanWriteVerifier(b, () => new[] { new SensorReading("rpm1", "r", "h", SensorKind.FanRpm, b.Commanded == 0 ? 500 : b.Commanded * 20) },
            (_, _) => Task.CompletedTask);
        var r = await v.VerifyAsync(b.Channels[0]);
        Assert.True(r.Confirmed);
        Assert.True(b.Restored);
    }

    [Fact]
    public async Task DoesNotConfirmWhenFirmwareIgnoresCommand()
    {
        var b = new FakeBackend();
        var v = new FanWriteVerifier(b, () => new[] { new SensorReading("rpm1", "r", "h", SensorKind.FanRpm, 800) },
            (_, _) => Task.CompletedTask);
        var r = await v.VerifyAsync(b.Channels[0]);
        Assert.False(r.Confirmed);
        Assert.Equal(WriteOutcome.NotConfirmed, r.Outcome);
        Assert.True(b.Restored);
    }

    [Fact]
    public async Task NoRpmSensorMeansNoConfirmation()
    {
        var b = new FakeBackend();
        var v = new FanWriteVerifier(b, () => Array.Empty<SensorReading>(), (_, _) => Task.CompletedTask);
        var r = await v.VerifyAsync(new FanChannel("c1", "C1", null));
        Assert.False(r.Confirmed);
    }
}

public class ProfileTests
{
    [Fact]
    public void BuiltInsExistAndRoundTrip()
    {
        var all = BuiltInProfiles.Create();
        Assert.Equal(new[] { "Silent", "Balanced", "Gaming", "Performance", "Custom" }, all.Select(p => p.Name));
        var back = ProfileSerializer.FromJson(ProfileSerializer.ToJson(all[2]));
        Assert.Equal("Gaming", back.Name);
        Assert.Equal(all[2].Default.Curve.Points.Count, back.Default.Curve.Points.Count);
    }

    [Fact]
    public void ImportSanitisesDangerousValues()
    {
        const string json = """{"Name":"x","Default":{"Mode":"Curve","MinSafePercent":0,"Curve":{"Points":[{"TempC":30,"Percent":0},{"TempC":60,"Percent":0}]}}}""";
        var p = ProfileSerializer.FromJson(json);
        Assert.True(p.Default.MinSafePercent >= 10);
        Assert.All(p.Default.Curve.Points, pt => Assert.True(pt.Percent >= 10));
    }

    [Fact]
    public void ImportRejectsGarbage() => Assert.Throws<FormatException>(() => ProfileSerializer.FromJson("{nope"));

    [Fact]
    public void SelectorMatchesRules()
    {
        var gaming = BuiltInProfiles.Create().First(p => p.Name == "Gaming");
        var ctx = new SelectionContext(new HashSet<string>(), 10, 80);
        Assert.Same(gaming, ProfileSelector.Select(new[] { gaming }, ctx));
        Assert.Null(ProfileSelector.Select(new[] { gaming }, ctx with { GpuTempC = 50 }));
    }
}
