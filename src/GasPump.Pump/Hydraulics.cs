using GasPump.Core;

namespace GasPump.Pump;

/// <summary>
/// The wet side. A valve opening in [0, 1] and a nominal full-flow rate. Given
/// a time slice it says how much fuel moved. Real pumps have a slow-flow
/// solenoid and a fast-flow solenoid; this is the continuous version.
///
/// Two things upstream of the valve can stop fuel with the valve wide open:
/// the supply (tank out, submersible pump tripped, line shut down by the
/// tank gauge) and the hose breakaway (a drive-off separates the coupling
/// and both halves seal). Both are modeled as switches here; the controller
/// reads the result through the flow switch, which is "did anything move".
/// </summary>
public sealed class Hydraulics(FlowRate nominalFlow)
{
    public FlowRate NominalFlow { get; } = nominalFlow;
    public double Opening { get; private set; }
    public bool ValveOpen => Opening > 0;

    /// <summary>False when the tank is dry, the STP is off, or the gauge shut the line. The valve position means nothing then.</summary>
    public bool SupplyAvailable { get; set; } = true;

    /// <summary>True after a drive-off. Sealed on both sides until an attendant resets the coupling.</summary>
    public bool BreakawaySeparated { get; set; }

    /// <summary>The flow switch: whether the last Deliver moved anything.</summary>
    public bool FlowSwitch { get; private set; }

    public void SetOpening(double opening) => Opening = Math.Clamp(opening, 0, 1);
    public void Close() => Opening = 0;

    public FlowRate CurrentFlow => SupplyAvailable && !BreakawaySeparated ? NominalFlow.Scale(Opening) : FlowRate.Zero;

    public Volume Deliver(TimeSpan dt)
    {
        var moved = CurrentFlow.Over(dt);
        FlowSwitch = moved > Volume.Zero;
        return moved;
    }
}

/// <summary>
/// The pulser. One pulse per milli-gallon. The controller trusts the meter
/// to count, but it checks the count against what the valve could have
/// passed, because a pulser can be spoofed and a meter can stick. A dead
/// pulser is the other direction: fuel moves and nothing is counted, so the
/// sale volume stands still while the tank empties.
/// </summary>
public sealed class Meter
{
    private long _pulsesThisTick;
    public Volume SaleVolume { get; private set; }

    /// <summary>When true, fuel that moves is not counted. The volume goes to UnmeteredVolume for the fault record.</summary>
    public bool PulserDead { get; set; }

    /// <summary>Fuel that moved while the pulser was dead this sale. It is never billed, and the fault entry records it.</summary>
    public Volume UnmeteredVolume { get; private set; }

    public void Register(Volume delivered)
    {
        if (PulserDead) { UnmeteredVolume += delivered; return; }
        _pulsesThisTick += delivered.MilliGallons;
        SaleVolume += delivered;
    }

    /// <summary>Test and attack hook: pulses that arrive with no fuel behind them.</summary>
    public void InjectPulses(long pulses)
    {
        _pulsesThisTick += pulses;
        SaleVolume += new Volume(pulses);
    }

    public long TakePulseCount()
    {
        var n = _pulsesThisTick;
        _pulsesThisTick = 0;
        return n;
    }

    public void ResetSale()
    {
        SaleVolume = Volume.Zero;
        UnmeteredVolume = Volume.Zero;
        _pulsesThisTick = 0;
    }
}
