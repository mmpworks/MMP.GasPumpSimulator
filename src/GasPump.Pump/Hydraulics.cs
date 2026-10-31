using GasPump.Core;

namespace GasPump.Pump;

/// <summary>
/// The wet side. A valve opening in [0, 1] and a nominal full-flow rate. Given
/// a time slice it says how much fuel moved. Real pumps have a slow-flow
/// solenoid and a fast-flow solenoid; this is the continuous version.
/// </summary>
public sealed class Hydraulics(FlowRate nominalFlow)
{
    public FlowRate NominalFlow { get; } = nominalFlow;
    public double Opening { get; private set; }
    public bool ValveOpen => Opening > 0;

    public void SetOpening(double opening) => Opening = Math.Clamp(opening, 0, 1);
    public void Close() => Opening = 0;

    public FlowRate CurrentFlow => NominalFlow.Scale(Opening);

    public Volume Deliver(TimeSpan dt) => CurrentFlow.Over(dt);
}

/// <summary>
/// The pulser. One pulse per milli-gallon. The controller trusts the meter
/// to count, but it checks the count against what the valve could have
/// passed, because a pulser can be spoofed and a meter can stick.
/// </summary>
public sealed class Meter
{
    private long _pulsesThisTick;
    public Volume SaleVolume { get; private set; }

    public void Register(Volume delivered)
    {
        _pulsesThisTick += delivered.MilliGallons;
        SaleVolume += delivered;
    }

    /// <summary>Test and attack hook: pulses that arrive with no fuel behind them.</summary>
    public void InjectPulses(long pulses) => Register(new Volume(pulses));

    public long TakePulseCount()
    {
        var n = _pulsesThisTick;
        _pulsesThisTick = 0;
        return n;
    }

    public void ResetSale()
    {
        SaleVolume = Volume.Zero;
        _pulsesThisTick = 0;
    }
}
