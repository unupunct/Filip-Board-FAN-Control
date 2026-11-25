namespace FilipBoard.Core;

/// <summary>A channel's write ability was proven (RPM readback) on a specific board/chip fingerprint.</summary>
public sealed record VerificationRecord(string ChannelId, string Fingerprint, DateTime VerifiedUtc);

/// <summary>
/// Single choke point for "may we write to this fan channel?". Default is read-only.
/// A write is allowed only when: read-only mode is off, the process is elevated,
/// and the channel has a verification record matching the current board fingerprint.
/// </summary>
public sealed class WriteGate
{
    private readonly Dictionary<string, VerificationRecord> _records = new();
    public bool ReadOnlyMode { get; set; } = true;

    public WriteGate(IEnumerable<VerificationRecord>? existing = null)
    {
        if (existing != null) foreach (var r in existing) _records[r.ChannelId] = r;
    }

    public IReadOnlyCollection<VerificationRecord> Records => _records.Values;
    public void Record(VerificationRecord r) => _records[r.ChannelId] = r;
    public void Revoke(string channelId) => _records.Remove(channelId);

    public bool IsVerified(string channelId, HardwareSummary hw) =>
        _records.TryGetValue(channelId, out var r) && r.Fingerprint == hw.Fingerprint;

    public bool IsAllowed(string channelId, HardwareSummary hw, out string reason)
    {
        if (ReadOnlyMode) { reason = "Read-only mode is on."; return false; }
        if (hw.IsElevated == false) { reason = "Administrator rights are required for fan control."; return false; }
        if (!IsVerified(channelId, hw)) { reason = "This channel has not passed the write/readback verification on this board."; return false; }
        reason = "";
        return true;
    }
}

/// <summary>Detects a stuck control loop so firmware control can be restored.</summary>
public sealed class Watchdog
{
    private readonly TimeSpan _timeout;
    private readonly Func<DateTime> _clock;
    private DateTime _last;

    public Watchdog(TimeSpan timeout, Func<DateTime>? clock = null)
    {
        _timeout = timeout;
        _clock = clock ?? (() => DateTime.UtcNow);
        _last = _clock();
    }

    public void Beat() => _last = _clock();
    public bool IsExpired => _clock() - _last > _timeout;
}

public static class TemperatureLevel
{
    public static AlertLevel Classify(double tempC, double warnC, double critC) =>
        tempC >= critC ? AlertLevel.Critical : tempC >= warnC ? AlertLevel.Elevated : AlertLevel.Normal;
}

/// <summary>Flags stalled fans and RPM changes that no command explains.</summary>
public sealed class FanHealthMonitor
{
    private sealed class State
    {
        public double? LastRpm; public double? LastCommand; public double CommandChangedAt;
        public double? StallSince; public bool StallReported; public bool DropReported;
    }

    private readonly Dictionary<string, State> _states = new();
    private readonly double _stallSeconds;
    private readonly double _settleSeconds;

    public FanHealthMonitor(double stallSeconds = 8, double settleSeconds = 10)
    {
        _stallSeconds = stallSeconds; _settleSeconds = settleSeconds;
    }

    public IEnumerable<HealthEvent> Observe(string channel, double? rpm, double? commandedPercent, double nowSec, DateTime utc)
    {
        if (!_states.TryGetValue(channel, out var s)) _states[channel] = s = new State { CommandChangedAt = nowSec };
        var events = new List<HealthEvent>();

        if (commandedPercent != s.LastCommand) { s.CommandChangedAt = nowSec; s.DropReported = false; }
        s.LastCommand = commandedPercent;

        if (rpm is null)
        {
            s.LastRpm = null;
            return events;
        }

        // Stall: reading 0 while we expect motion (commanded >= 10% or unknown/auto).
        if (rpm <= 0 && (commandedPercent is null || commandedPercent >= 10))
        {
            s.StallSince ??= nowSec;
            if (!s.StallReported && nowSec - s.StallSince >= _stallSeconds)
            {
                s.StallReported = true;
                events.Add(new(utc, AlertLevel.Critical, HealthEventKind.FanStalled, channel,
                    $"{channel}: RPM reads 0 for {_stallSeconds:0}s. Fan missing, unplugged or failed."));
            }
        }
        else { s.StallSince = null; s.StallReported = false; }

        // Unexpected drop: >40% fall with no command change inside the settle window.
        if (s.LastRpm is > 300 && rpm > 0 && rpm < s.LastRpm * 0.6
            && nowSec - s.CommandChangedAt > _settleSeconds && !s.DropReported)
        {
            s.DropReported = true;
            events.Add(new(utc, AlertLevel.Elevated, HealthEventKind.FanRpmUnexpectedChange, channel,
                $"{channel}: RPM fell from {s.LastRpm:0} to {rpm:0} without a speed change."));
        }

        s.LastRpm = rpm;
        return events;
    }
}
