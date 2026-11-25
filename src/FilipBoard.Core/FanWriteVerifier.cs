namespace FilipBoard.Core;

/// <summary>
/// Proves a channel really controls a fan: command two different speeds, require the measured RPM to follow.
/// Always returns the channel to firmware control afterwards. Never reports success without an RPM readback.
/// </summary>
public sealed class FanWriteVerifier
{
    private readonly IFanBackend _backend;
    private readonly Func<IReadOnlyList<SensorReading>> _poll;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public double LowPercent { get; init; } = 40;
    public double HighPercent { get; init; } = 70;
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>Required relative RPM rise between low and high step.</summary>
    public double MinRelativeRise { get; init; } = 0.10;

    public FanWriteVerifier(IFanBackend backend, Func<IReadOnlyList<SensorReading>> poll,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _backend = backend; _poll = poll; _delay = delay ?? Task.Delay;
    }

    public async Task<WriteResult> VerifyAsync(FanChannel channel, CancellationToken ct = default)
    {
        if (channel.RpmSensorId is null)
            return new(WriteOutcome.NotConfirmed, 0, null, "No RPM sensor is mapped to this channel, so a change cannot be confirmed.");

        double? Rpm() => _poll().FirstOrDefault(r => r.Id == channel.RpmSensorId)?.Value;

        var baseline = Rpm();
        if (baseline is null or <= 0)
            return new(WriteOutcome.NotConfirmed, 0, baseline, "No fan RPM detected on this channel; cannot verify.");

        try
        {
            if (!_backend.TrySetPercent(channel.Id, LowPercent, out var err))
                return new(WriteOutcome.Failed, LowPercent, null, $"Driver rejected command: {err}");
            await _delay(Settle, ct);
            var low = Rpm();

            if (!_backend.TrySetPercent(channel.Id, HighPercent, out err))
                return new(WriteOutcome.Failed, HighPercent, low, $"Driver rejected command: {err}");
            await _delay(Settle, ct);
            var high = Rpm();

            if (low is null or <= 0 || high is null)
                return new(WriteOutcome.NotConfirmed, HighPercent, high, "RPM was unreadable after the command.");

            if (high >= low * (1 + MinRelativeRise))
                return new(WriteOutcome.Confirmed, HighPercent, high,
                    $"Confirmed: {low:0} RPM at {LowPercent:0}% -> {high:0} RPM at {HighPercent:0}%.");

            return new(WriteOutcome.NotConfirmed, HighPercent, high,
                $"Fan did not follow the command ({low:0} RPM at {LowPercent:0}%, {high:0} RPM at {HighPercent:0}%). " +
                "Control is not available on this channel (firmware may be overriding it).");
        }
        catch (OperationCanceledException)
        {
            return new(WriteOutcome.Failed, 0, null, "Verification cancelled.");
        }
        catch (Exception ex)
        {
            return new(WriteOutcome.Failed, 0, null, $"Verification error: {ex.Message}");
        }
        finally
        {
            try { _backend.RestoreAutomatic(channel.Id); } catch { /* nothing more we can do; logged by caller via result */ }
        }
    }
}
