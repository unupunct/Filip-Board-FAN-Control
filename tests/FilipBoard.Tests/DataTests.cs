using FilipBoard.Core;
using FilipBoard.Data;
using Xunit;

namespace FilipBoard.Tests;

public class DataTests
{
    [Fact]
    public void SettingsRoundTrip()
    {
        using var s = new SqliteStore(":memory:");
        Assert.True(s.LoadSettings().ReadOnlyMode); // safe default
        var cfg = new AppSettings { ReadOnlyMode = false, RefreshMs = 500 };
        cfg.ChannelNames["ch1"] = "Case Fan 1 - ARCTIC P14";
        s.SaveSettings(cfg);
        var back = s.LoadSettings();
        Assert.False(back.ReadOnlyMode);
        Assert.Equal(500, back.RefreshMs);
        Assert.Equal("Case Fan 1 - ARCTIC P14", back.ChannelNames["ch1"]);
    }

    [Fact]
    public void ProfilesPersistAndBuiltInsAlwaysPresent()
    {
        using var s = new SqliteStore(":memory:");
        Assert.Equal(5, s.LoadProfiles().Count);
        var mine = BuiltInProfiles.Create()[1].Clone("Mine");
        s.SaveProfile(mine);
        var all = s.LoadProfiles();
        Assert.Equal(6, all.Count);
        Assert.Contains(all, p => p.Name == "Mine" && !p.BuiltIn);
        s.DeleteProfile("Mine");
        Assert.Equal(5, s.LoadProfiles().Count);
    }

    [Fact]
    public void VerificationRoundTrip()
    {
        using var s = new SqliteStore(":memory:");
        var v = new VerificationRecord("ch1", "board|IT8686E", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        s.SaveVerification(v);
        Assert.Equal(v, s.LoadVerifications().Single());
    }

    [Fact]
    public void HistoryStoresRealButNeverMockAndExportsCsv()
    {
        using var s = new SqliteStore(":memory:");
        var now = DateTime.UtcNow;
        s.AddSamples(now, new[]
        {
            new SensorReading("cpu", "CPU", "x", SensorKind.Temperature, 50),
            new SensorReading("fake", "Fake", "x", SensorKind.Temperature, 99, IsMock: true),
            new SensorReading("none", "None", "x", SensorKind.Temperature, null),
        });
        Assert.Equal(new[] { "cpu" }, s.KnownSensors());
        Assert.Single(s.QueryHistory("cpu", now.AddMinutes(-1), now.AddMinutes(1)));

        var file = Path.Combine(Path.GetTempPath(), $"fb-{Guid.NewGuid():N}.csv");
        try
        {
            s.ExportHistoryCsv(file, now.AddMinutes(-1), now.AddMinutes(1));
            var text = File.ReadAllText(file);
            Assert.Contains("cpu,50", text);
            Assert.DoesNotContain("fake", text);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void LogAndPrune()
    {
        using var s = new SqliteStore(":memory:");
        s.Log("Error", "Fan", "boom, \"quoted\"", DateTime.UtcNow.AddDays(-40));
        s.Log("Info", "Fan", "ok");
        Assert.Equal(2, s.RecentLog().Count);
        s.Prune(30);
        Assert.Single(s.RecentLog());
    }
}
