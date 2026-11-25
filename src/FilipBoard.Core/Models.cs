namespace FilipBoard.Core;

public enum SensorKind { Temperature, FanRpm, Load, ControlPercent, Other }

/// <summary>One sensor sample. IsMock marks simulated data so it can never be mistaken for real readings.</summary>
public sealed record SensorReading(string Id, string Name, string Hardware, SensorKind Kind, double? Value, bool IsMock = false);

public sealed record HardwareSummary(
    string Provider,
    bool IsMock,
    bool IsElevated,
    string Motherboard,
    IReadOnlyList<string> SuperIoChips,
    IReadOnlyList<string> Notes)
{
    public bool IsTargetBoard =>
        Motherboard.Contains("Z370", StringComparison.OrdinalIgnoreCase) &&
        Motherboard.Contains("AORUS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identifies board + chips; a write verification is only valid for the fingerprint it was done on.</summary>
    public string Fingerprint => $"{Motherboard}|{string.Join(",", SuperIoChips.OrderBy(c => c))}";
}

/// <summary>A motherboard control channel (header/PWM output) as exposed by the backend.</summary>
public sealed record FanChannel(string Id, string Name, string? RpmSensorId);

public enum WriteOutcome { Confirmed, NotConfirmed, Blocked, Failed }

public sealed record WriteResult(WriteOutcome Outcome, double RequestedPercent, double? ObservedRpm, string Message)
{
    public bool Confirmed => Outcome == WriteOutcome.Confirmed;
}

public enum AlertLevel { Normal, Elevated, Critical }

public enum HealthEventKind { FanStalled, FanRpmUnexpectedChange, SensorLost, Overheat, EmergencyFullSpeed, WatchdogRestore }

public sealed record HealthEvent(DateTime Utc, AlertLevel Level, HealthEventKind Kind, string Subject, string Message);
