namespace FilipBoard.Core;

/// <summary>Exponential moving average to stop fan speed chasing temperature noise.</summary>
public sealed class TemperatureSmoother
{
    private readonly double _alpha;
    private double? _value;
    public TemperatureSmoother(double alpha = 0.3) => _alpha = Math.Clamp(alpha, 0.01, 1);
    public double Add(double sample)
    {
        _value = _value is null ? sample : _alpha * sample + (1 - _alpha) * _value.Value;
        return _value.Value;
    }
    public void Reset() => _value = null;
}

/// <summary>What the governor wants done with a channel.</summary>
public sealed record GovernorDecision(double? Percent, bool ReleaseToAuto, bool Emergency, string Reason);

/// <summary>
/// Per-channel decision logic: smoothing, hysteresis, ramp delays, minimum-speed floor, emergency override.
/// Pure logic with an injected clock (seconds) so it is fully testable without hardware.
/// </summary>
public sealed class CurveGovernor
{
    private readonly TemperatureSmoother _smoother = new();
    private double? _applied;
    private double _anchorTemp;
    private double? _upSince;
    private double? _downSince;

    public double? AppliedPercent => _applied;

    public void Reset() { _smoother.Reset(); _applied = null; _upSince = _downSince = null; }

    public GovernorDecision Decide(ChannelConfig cfg, double? tempC, double nowSeconds)
    {
        var floor = cfg.AllowFanStop ? 0 : Math.Max(cfg.MinSafePercent, 10);

        switch (cfg.Mode)
        {
            case FanMode.Auto:
                Reset();
                return new(null, true, false, "Hardware (BIOS) automatic control");
            case FanMode.Max:
                _applied = 100;
                return new(100, false, false, "Maximum speed mode");
            case FanMode.Manual:
                _applied = Math.Clamp(cfg.ManualPercent, floor, 100);
                return new(_applied, false, false, "Manual speed");
        }

        // Curve-based modes need a live temperature. Without one, hand control back to firmware.
        if (tempC is null || double.IsNaN(tempC.Value))
        {
            Reset();
            return new(null, true, false, "Temperature source unavailable - released to hardware control");
        }

        var smoothed = _smoother.Add(tempC.Value);

        // Emergency uses the RAW temperature so smoothing can never delay it.
        if (tempC.Value >= cfg.CriticalTempC)
        {
            _applied = 100; _anchorTemp = smoothed; _upSince = _downSince = null;
            return new(100, false, true, $"EMERGENCY: {tempC.Value:0.#}°C >= {cfg.CriticalTempC:0.#}°C");
        }

        var curve = cfg.Mode == FanMode.Curve ? cfg.Curve : CurvePresets.For(cfg.Mode);
        var target = Math.Clamp(curve.Evaluate(smoothed), floor, 100);

        if (_applied is null)
        {
            _applied = target; _anchorTemp = smoothed;
            return new(_applied, false, false, $"Curve start at {smoothed:0.#}°C");
        }

        const double deadband = 0.5;
        if (target > _applied.Value + deadband)
        {
            _downSince = null;
            _upSince ??= nowSeconds;
            if (nowSeconds - _upSince.Value >= cfg.RampUpSeconds)
            {
                _applied = target; _anchorTemp = smoothed; _upSince = null;
                return new(_applied, false, false, $"Ramp up at {smoothed:0.#}°C");
            }
        }
        else if (target < _applied.Value - deadband && smoothed <= _anchorTemp - cfg.HysteresisC)
        {
            _upSince = null;
            _downSince ??= nowSeconds;
            if (nowSeconds - _downSince.Value >= cfg.RampDownSeconds)
            {
                _applied = target; _anchorTemp = smoothed; _downSince = null;
                return new(_applied, false, false, $"Ramp down at {smoothed:0.#}°C");
            }
        }
        else
        {
            _upSince = _downSince = null;
        }

        return new(_applied, false, false, "Holding");
    }
}
