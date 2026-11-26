using FilipBoard.Core;
using FilipBoard.Data;

namespace FilipBoard.App.Services;

public sealed record ChannelState(
    FanChannel Channel, string Name, double? Rpm, double? ReportedPercent, double? CommandedPercent,
    FanMode Mode, double? SourceTempC, string SourceName, string Status, bool WriteAllowed, string WriteReason, bool Verified);

public sealed record Snapshot(DateTime Utc, IReadOnlyList<SensorReading> Readings, IReadOnlyList<ChannelState> Channels, HardwareSummary Hardware);

/// <summary>
/// Orchestrates polling, automation, safety, history and alerts.
/// Reading and writing are separate: provider = read, backend = write (null when no controllable hardware).
/// </summary>
public sealed class FanControlService : IDisposable
{
    private readonly SqliteStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, CurveGovernor> _governors = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _lastSent = new();
    private readonly HashSet<string> _hwControlled = new();
    private readonly FanHealthMonitor _health = new();
    private readonly Dictionary<string, DateTime> _alertCooldown = new();
    private readonly Watchdog _watchdog = new(TimeSpan.FromSeconds(10));
    private CancellationTokenSource? _cts;
    private Task? _loop, _dog;
    private DateTime _lastHistory = DateTime.MinValue;
    private DateTime _lastPrune = DateTime.MinValue;
    private DateTime _startedAt = DateTime.UtcNow;
    private IReadOnlyList<SensorReading> _lastReadings = Array.Empty<SensorReading>();
    private volatile bool _halted;
    private volatile bool _paused;

    public ISensorProvider Provider { get; }
    public IFanBackend? Backend { get; }
    public WriteGate Gate { get; }
    public AppSettings Settings { get; }
    public List<FanProfile> Profiles { get; private set; }
    public FanProfile ActiveProfile { get; private set; }
    public Snapshot? Latest { get; private set; }

    public event Action<Snapshot>? Updated;
    public event Action<HealthEvent>? HealthEventRaised;

    public FanControlService(ISensorProvider provider, IFanBackend? backend, SqliteStore store, AppSettings settings)
    {
        Provider = provider; Backend = backend; _store = store; Settings = settings;
        Gate = new WriteGate(store.LoadVerifications()) { ReadOnlyMode = settings.ReadOnlyMode };
        Profiles = store.LoadProfiles();
        if (provider.Summary.Fingerprint == BoardPresets.Fingerprint && Profiles.All(p => p.Name != BoardPresets.ProfileName))
        {
            store.SaveProfile(BoardPresets.BiosReplica());
            Profiles = store.LoadProfiles();
        }
        ActiveProfile = Profiles.FirstOrDefault(p => p.Name == settings.ActiveProfile) ?? Profiles[1];
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _dog = Task.Run(() => WatchdogAsync(_cts.Token));
        _store.Log("Info", "App", $"Started. Provider={Provider.Summary.Provider}, board={Provider.Summary.Motherboard}, " +
                                  $"chips=[{string.Join(", ", Provider.Summary.SuperIoChips)}], elevated={Provider.Summary.IsElevated}, readOnly={Gate.ReadOnlyMode}");
        foreach (var n in Provider.Summary.Notes) _store.Log("Warn", "Hardware", n);
    }

    public void SetReadOnly(bool readOnly)
    {
        Gate.ReadOnlyMode = readOnly;
        Settings.ReadOnlyMode = readOnly;
        _store.SaveSettings(Settings);
        if (readOnly) RestoreHardwareControl("Read-only mode enabled");
        _store.Log("Info", "Safety", readOnly ? "Read-only mode ON" : "Read-only mode OFF (writes allowed only on verified channels)");
    }

    public void SetActiveProfile(FanProfile p)
    {
        lock (_lock) { ActiveProfile = p; foreach (var g in _governors.Values) g.Reset(); }
        _baseProfile = Settings.AutoSelectProfile ? p.Name : "";
        Settings.ActiveProfile = p.Name; _store.SaveSettings(Settings);
        _store.Log("Info", "Profile", $"Active profile: {p.Name}");
    }

    public void ReloadProfiles()
    {
        lock (_lock)
        {
            Profiles = _store.LoadProfiles();
            ActiveProfile = Profiles.FirstOrDefault(p => p.Name == ActiveProfile.Name) ?? Profiles[1];
        }
    }

    public void SaveActiveProfile()
    {
        lock (_lock) _store.SaveProfile(ActiveProfile);
    }

    /// <summary>Copy of the effective config for a channel; the UI edits copies, never live objects.</summary>
    public ChannelConfig GetConfig(string channelId)
    {
        lock (_lock) return ActiveProfile.For(channelId).Clone();
    }

    public void SetConfig(string channelId, ChannelConfig cfg)
    {
        lock (_lock)
        {
            ActiveProfile.Channels[channelId] = cfg.Clone();
            if (_governors.TryGetValue(channelId, out var g)) g.Reset();
            _store.SaveProfile(ActiveProfile);
        }
        _store.Log("Info", "Config", $"{channelId}: mode={cfg.Mode}, manual={cfg.ManualPercent:0}%, source={cfg.SourceSensorId ?? "default"}");
    }

    private string _baseProfile = "";
    private DateTime _lastAuto = DateTime.MinValue;
    private HashSet<string> _procs = new(StringComparer.OrdinalIgnoreCase);

    private void AutoSelectProfile(IReadOnlyList<SensorReading> readings, DateTime now)
    {
        if (!Settings.AutoSelectProfile) { _baseProfile = ""; return; }
        if ((now - _lastAuto).TotalSeconds < 5) return;
        _lastAuto = now;
        if (_baseProfile == "") _baseProfile = ActiveProfile.Name;
        try
        {
            _procs = System.Diagnostics.Process.GetProcesses().Select(p => p.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { /* keep previous list */ }
        var ctx = new SelectionContext(_procs, SensorCatalog.CpuLoad(readings)?.Value ?? 0, SensorCatalog.GpuTemp(readings)?.Value);
        List<FanProfile> candidates;
        lock (_lock) candidates = Profiles.Where(p => p.Name != _baseProfile).ToList();
        var pick = ProfileSelector.Select(candidates, ctx)
                   ?? Profiles.FirstOrDefault(p => p.Name == _baseProfile);
        if (pick != null && pick.Name != ActiveProfile.Name)
        {
            lock (_lock) { ActiveProfile = pick; foreach (var g in _governors.Values) g.Reset(); }
            _store.Log("Info", "Profile", $"Auto-selected profile: {pick.Name}");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                // Any application error: hand fans back to firmware rather than leave them at a stale speed.
                _store.Log("Error", "Control", "Control loop error: " + ex.Message);
                RestoreHardwareControl("control loop error");
            }
            try { await Task.Delay(Math.Max(250, Settings.RefreshMs), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            if (_watchdog.IsExpired && !_halted)
            {
                _halted = true;
                RestoreHardwareControl("watchdog: control loop stalled");
                Raise(new HealthEvent(DateTime.UtcNow, AlertLevel.Critical, HealthEventKind.WatchdogRestore, "watchdog",
                    "Control loop stalled; fans returned to hardware control."));
            }
        }
    }

    private void Tick()
    {
        if (_paused) { _watchdog.Beat(); return; } // a verification test owns the hardware right now
        var now = DateTime.UtcNow;
        var sec = (now - _startedAt).TotalSeconds;
        var readings = Provider.Poll();
        _lastReadings = readings;
        _watchdog.Beat();
        _halted = false;

        AutoSelectProfile(readings, now);
        FanProfile profile;
        lock (_lock) profile = ActiveProfile;
        var hw = Provider.Summary;
        var states = new List<ChannelState>();

        // Build one state per detected fan (RPM sensor), joined with its control channel when one exists.
        var channels = Backend?.Channels ?? Array.Empty<FanChannel>();
        var fans = SensorCatalog.Fans(readings).ToList();
        var shown = new HashSet<string>();

        foreach (var ch in channels)
        {
            ChannelConfig cfg;
            lock (_lock) cfg = profile.For(ch.Id);
            var rpm = ch.RpmSensorId is null ? null : readings.FirstOrDefault(r => r.Id == ch.RpmSensorId)?.Value;
            if (ch.RpmSensorId != null) shown.Add(ch.RpmSensorId);
            var reportedPct = readings.FirstOrDefault(r => r.Id == ch.Id)?.Value;
            var allowed = Gate.IsAllowed(ch.Id, hw, out var why);
            var verified = Gate.IsVerified(ch.Id, hw);

            var srcId = cfg.SourceSensorId;
            var src = srcId != null ? readings.FirstOrDefault(r => r.Id == srcId)
                      : SensorCatalog.CpuTemp(readings);
            var temp = src?.Value;

            if (!_governors.TryGetValue(ch.Id, out var gov)) _governors[ch.Id] = gov = new CurveGovernor();
            var decision = gov.Decide(cfg, temp, sec);
            string status = decision.Reason;
            double? commanded = _lastSent.TryGetValue(ch.Id, out var ls) ? ls : null;

            if (decision.Emergency)
            {
                Raise(new HealthEvent(now, AlertLevel.Critical, HealthEventKind.EmergencyFullSpeed, ch.Name, decision.Reason));
            }

            if (!allowed)
            {
                // Observation only; make sure we are not leaving a stale command behind.
                if (_lastSent.ContainsKey(ch.Id)) { Backend!.RestoreAutomatic(ch.Id); _lastSent.TryRemove(ch.Id, out _); commanded = null; }
                status = cfg.Mode == FanMode.Auto ? "Hardware (BIOS) control" : $"Not applied: {why}";
            }
            else if (decision.ReleaseToAuto)
            {
                if (_lastSent.TryRemove(ch.Id, out _)) { Backend!.RestoreAutomatic(ch.Id); _store.Log("Info", "FanCommand", $"{ch.Name}: released to hardware control ({decision.Reason})"); }
                commanded = null;
            }
            else if (decision.Percent is { } pct)
            {
                if (!_lastSent.TryGetValue(ch.Id, out var prev) || Math.Abs(prev - pct) >= 1)
                {
                    if (Backend!.TrySetPercent(ch.Id, pct, out var err))
                    {
                        _lastSent[ch.Id] = pct; commanded = pct;
                        _store.Log("Info", "FanCommand", $"{ch.Name}: {pct:0}% ({decision.Reason})");
                    }
                    else _store.Log("Error", "FanCommand", $"{ch.Name}: command {pct:0}% failed: {err}");
                }
            }

            foreach (var e in _health.Observe(ch.Name, rpm, commanded, sec, now)) Raise(e);
            states.Add(new ChannelState(ch, DisplayName(ch.Id, ch.Name), rpm, reportedPct, commanded, cfg.Mode, temp, src?.Name ?? "none",
                status, allowed, why, verified));
        }

        // Fans without any control channel: monitoring only.
        foreach (var f in fans.Where(f => !shown.Contains(f.Id)))
        {
            var fake = new FanChannel("", f.Name, f.Id);
            foreach (var e in _health.Observe(f.Name, f.Value, null, sec, now)) Raise(e);
            states.Add(new ChannelState(fake, DisplayName(f.Id, f.Name), f.Value, null, null, FanMode.Auto, null, "n/a",
                "Monitoring only (no control channel)", false, "No control channel is exposed for this header.", false));
        }

        CheckAlerts(readings, now);
        LogHistory(readings, now);

        var snap = new Snapshot(now, readings, states, hw);
        Latest = snap;
        Updated?.Invoke(snap);
    }

    public string DisplayName(string idOrChannel, string fallback) =>
        Settings.ChannelNames.TryGetValue(idOrChannel, out var n) && !string.IsNullOrWhiteSpace(n) ? n
        : BoardPresets.LabelFor(Provider.Summary.Fingerprint, idOrChannel) ?? QualifiedName(idOrChannel, fallback);

    /// <summary>"/lpc/it8686e/0/control/2" + "Fan #3" -> "IT8686E Fan #3", so two Super I/O chips never show identical names.</summary>
    public static string QualifiedName(string id, string name)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "lpc" ? $"{parts[1].ToUpperInvariant()} {name}" : name;
    }

    private void CheckAlerts(IReadOnlyList<SensorReading> readings, DateTime now)
    {
        void Check(SensorReading? r, double warn, double crit, string label)
        {
            if (r?.Value is not { } v) return;
            var level = TemperatureLevel.Classify(v, warn, crit);
            if (level == AlertLevel.Normal) return;
            if (_alertCooldown.TryGetValue(label, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
            _alertCooldown[label] = now;
            Raise(new HealthEvent(now, level, HealthEventKind.Overheat, label,
                $"{label} is {v:0.#}°C ({(level == AlertLevel.Critical ? "critical" : "elevated")}; warn {warn:0}, critical {crit:0})."));
        }
        Check(SensorCatalog.CpuTemp(readings), Settings.CpuWarnC, Settings.CpuCritC, "CPU");
        Check(SensorCatalog.GpuTemp(readings), Settings.GpuWarnC, Settings.GpuCritC, "GPU");
        Check(SensorCatalog.BoardTemp(readings), Settings.BoardWarnC, Settings.BoardCritC, "Motherboard");
    }

    private void LogHistory(IReadOnlyList<SensorReading> readings, DateTime now)
    {
        if ((now - _lastHistory).TotalSeconds < Math.Max(1, Settings.LogIntervalSeconds)) return;
        _lastHistory = now;
        _store.AddSamples(now, readings.Where(r => r.Kind is SensorKind.Temperature or SensorKind.FanRpm or SensorKind.Load or SensorKind.ControlPercent));
        if ((now - _lastPrune).TotalHours >= 6) { _lastPrune = now; _store.Prune(Settings.RetentionDays); }
    }

    private void Raise(HealthEvent e)
    {
        _store.Log(e.Level == AlertLevel.Critical ? "Error" : "Warn", e.Kind.ToString(), $"{e.Subject}: {e.Message}", e.Utc);
        HealthEventRaised?.Invoke(e);
    }

    /// <summary>Returns every channel to firmware control. Safe to call repeatedly and from any thread.</summary>
    public void RestoreHardwareControl(string reason)
    {
        try
        {
            Backend?.RestoreAutomatic();
            _lastSent.Clear();
            foreach (var g in _governors.Values) g.Reset();
            _store.Log("Info", "Safety", "Hardware control restored: " + reason);
        }
        catch (Exception ex) { _store.Log("Error", "Safety", "Restore failed: " + ex.Message); }
    }

    /// <summary>Full speed on all channels that are allowed to be written. Returns how many were set.</summary>
    public int EmergencyFullSpeed()
    {
        if (Backend is null) return 0;
        var n = 0;
        foreach (var ch in Backend.Channels)
        {
            if (!Gate.IsAllowed(ch.Id, Provider.Summary, out _)) continue;
            if (Backend.TrySetPercent(ch.Id, 100, out _)) { _lastSent[ch.Id] = 100; n++; }
        }
        // The governor would undo this next tick, so pin the profile's channels to Max until the user changes it.
        if (n > 0)
        {
            lock (_lock)
            {
                ActiveProfile.Default.Mode = FanMode.Max;
                ActiveProfile.Channels.Clear();
            }
        }
        _store.Log("Warn", "Safety", $"Emergency full speed requested; applied to {n} channel(s).");
        return n;
    }

    public async Task<WriteResult> VerifyChannelAsync(FanChannel ch, CancellationToken ct = default)
    {
        if (Backend is null) return new(WriteOutcome.Blocked, 0, null, "No controllable hardware.");
        if (Gate.ReadOnlyMode) return new(WriteOutcome.Blocked, 0, null, "Turn off read-only mode first (Settings).");
        if (!Provider.Summary.IsElevated) return new(WriteOutcome.Blocked, 0, null, "Administrator rights required.");

        // Pause automation so the test is not fought by the governor.
        _paused = true;
        try
        {
            var v = new FanWriteVerifier(Backend, () => _lastReadings = Provider.Poll());
            var result = await v.VerifyAsync(ch, ct);
            if (result.Confirmed)
            {
                var rec = new VerificationRecord(ch.Id, Provider.Summary.Fingerprint, DateTime.UtcNow);
                Gate.Record(rec); _store.SaveVerification(rec);
            }
            else { Gate.Revoke(ch.Id); _store.DeleteVerification(ch.Id); }
            _store.Log(result.Confirmed ? "Info" : "Warn", "Verification", $"{ch.Name}: {result.Message}");
            return result;
        }
        finally { _paused = false; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { Task.WaitAll(new[] { _loop, _dog }.Where(t => t != null).Cast<Task>().ToArray(), 3000); } catch { }
        RestoreHardwareControl("application exit");
        Backend?.Dispose();
        Provider.Dispose();
    }
}
