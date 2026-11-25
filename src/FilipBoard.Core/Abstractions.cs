namespace FilipBoard.Core;

/// <summary>Reads sensors. Never writes. Implementations: LibreHardwareMonitor, Mock.</summary>
public interface ISensorProvider : IDisposable
{
    HardwareSummary Summary { get; }

    /// <summary>Refreshes hardware and returns a snapshot. Must not throw; returns what is available.</summary>
    IReadOnlyList<SensorReading> Poll();
}

/// <summary>Writes fan speed. Separate from sensor reading so the two can be swapped independently.</summary>
public interface IFanBackend : IDisposable
{
    /// <summary>Channels the driver exposes. "Exposed" does NOT mean verified writable.</summary>
    IReadOnlyList<FanChannel> Channels { get; }

    /// <summary>
    /// Sends a PWM command. Returns true only if the driver accepted the call; this is NOT confirmation
    /// that the fan changed speed. Confirmation requires an RPM readback.
    /// </summary>
    bool TrySetPercent(string channelId, double percent, out string error);

    /// <summary>Returns the channel (or all channels) to firmware/BIOS control.</summary>
    void RestoreAutomatic(string? channelId = null);
}
