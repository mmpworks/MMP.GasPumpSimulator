using GasPump.Core;
using GasPump.Security;

namespace GasPump.Pump;

public enum PumpPhase { Disarmed, Armed, Dispensing, Stopped, Faulted }

/// <summary>What one tick produced. The worker turns this into sale events.</summary>
public sealed record PumpTick(
    PumpPhase Phase,
    Volume Volume,
    Money Money,
    FlowRate Flow,
    string TaperPhase,
    bool CeilingReached,
    bool Stopped,
    string? Fault);

/// <summary>
/// The dispenser's brain. Single-threaded by contract: the pump worker is the
/// only caller. It opens the valve only after the authority armed it, it
/// closes the valve on the cent, and it stops dead on anything that looks like
/// tampering: pulses the valve could not have passed, fuel moving while
/// disarmed, a sale running past its time.
/// </summary>
public sealed class PumpController(
    PumpAuthority authority,
    Hydraulics hydraulics,
    Meter meter,
    Totalizer totalizer,
    EventLedger ledger,
    Pricing pricing,
    TaperPolicy taper,
    TimeProvider clock)
{
    private ArmedAuthorization? _armed;
    private DateTimeOffset _armedAt;
    private Grade _grade = Grade.Regular;
    private bool _nozzleLifted;
    private string _tickPhase = "armed";

    public PumpPhase Phase { get; private set; } = PumpPhase.Disarmed;
    public Money Money { get; private set; }
    public Volume Volume => meter.SaleVolume;
    public string? LastFault { get; private set; }
    public ArmedAuthorization? Armed => _armed;
    public bool NozzleLifted => _nozzleLifted;

    public (bool Ok, string Reason, ArmedAuthorization? Armed) Arm(string saleId, string tokenWire, Grade grade)
    {
        if (Phase is PumpPhase.Dispensing)
            return (false, "AlreadyDispensing", null);

        var (armed, refusal) = authority.TryArm(saleId, tokenWire);
        if (armed is null) return (false, refusal, null);

        _armed = armed;
        _armedAt = clock.GetUtcNow();
        _grade = grade;
        Money = Money.Zero;
        LastFault = null;
        meter.ResetSale();
        hydraulics.Close();
        Phase = PumpPhase.Armed;
        return (true, "", armed);
    }

    /// <summary>The nozzle switch. Lifting with no authorization is recorded and does nothing else.</summary>
    public bool LiftNozzle()
    {
        _nozzleLifted = true;
        if (Phase != PumpPhase.Armed || _armed is null)
        {
            ledger.Append("nozzle.lift.ignored", $"phase {Phase}");
            return false;
        }
        Phase = PumpPhase.Dispensing;
        hydraulics.SetOpening(1);
        ledger.Append("valve.open", $"{_armed.SaleId} grade {_grade} ceiling {_armed.Ceiling}");
        return true;
    }

    public void HangNozzle()
    {
        _nozzleLifted = false;
        if (Phase == PumpPhase.Dispensing) Stop("nozzle hung");
    }

    public void CloseValve(string reason) => Stop(reason);

    public void EmergencyStop(string reason)
    {
        hydraulics.Close();
        Phase = PumpPhase.Faulted;
        LastFault = reason;
        ledger.Append("pump.fault", $"{_armed?.SaleId ?? "-"} {reason} after {Volume} {Money}");
        _armed = null;
    }

    /// <summary>
    /// One slice of time. Checks the clock, moves fuel through the valve at the
    /// taper's opening, counts it, prices it, and clamps the last slice so the
    /// money lands on the ceiling and never past it.
    /// </summary>
    public PumpTick Tick(TimeSpan dt)
    {
        // Fuel moving while disarmed or stopped is a stuck valve or a bypass. Stop everything.
        if (Phase != PumpPhase.Dispensing)
        {
            var strayPulses = meter.TakePulseCount();
            if (strayPulses > 0 && Phase != PumpPhase.Faulted)
            {
                EmergencyStop($"{strayPulses} pulses while {Phase}");
                return Current(ceilingReached: false, stopped: true);
            }
            return Current(ceilingReached: false, stopped: Phase is PumpPhase.Stopped or PumpPhase.Faulted);
        }

        var armed = _armed!;
        var now = clock.GetUtcNow();
        if (now - _armedAt > armed.MaxDuration)
        {
            Stop("max sale duration");
            return Current(ceilingReached: false, stopped: true);
        }

        // Where the stop is, in volume: whichever comes first, the money ceiling or the volume ceiling.
        var volumeCap = armed.MaxVolume - Volume;
        var moneyCapVolume = pricing.MaxVolumeFor(_grade, armed.Ceiling) - Volume;
        var cap = Volume.Min(volumeCap, moneyCapVolume);

        // Taper: how far open for the money that is left. The displayed money can
        // round up to the ceiling a few pulses before the volume cap, so the taper
        // never closes the valve on its own. The cap below does the stop.
        var remaining = armed.Ceiling - Money;
        if (remaining <= Money.Zero && cap > Volume.Zero) remaining = new Money(1);
        var opening = taper.Opening(remaining, armed.Ceiling);
        hydraulics.SetOpening(opening);
        _tickPhase = opening >= 1 ? "full" : opening <= taper.MinimumOpening ? "creep" : "taper";

        // Move fuel, then clamp the last slice so it lands on the cap.
        var delivered = hydraulics.Deliver(dt);
        if (delivered == Volume.Zero && cap > Volume.Zero) delivered = new Volume(1);   // creep floor: at least one pulse per tick
        bool ceilingReached = false;
        if (delivered >= cap)
        {
            delivered = cap;
            ceilingReached = true;
        }

        meter.Register(delivered);

        // Pulser sanity. The valve at full open over one tick cannot pass more than this.
        var pulses = meter.TakePulseCount();
        if (pulses > authority.Limits.MaxPulsesPerTick * Math.Max(1.0, dt.TotalMilliseconds / 100.0))
        {
            EmergencyStop($"pulser reported {pulses} pulses in one tick, valve could pass at most {authority.Limits.MaxPulsesPerTick}");
            return Current(ceilingReached: false, stopped: true);
        }

        totalizer.Add(delivered);
        Money = pricing.CentsFor(_grade, Volume);

        if (Money > armed.Ceiling)
        {
            // Belt and braces. MaxVolumeFor makes this unreachable; the ledger will say if it ever is not.
            EmergencyStop($"money {Money} exceeded ceiling {armed.Ceiling}");
            return Current(ceilingReached: true, stopped: true);
        }

        if (ceilingReached)
        {
            Stop("ceiling reached");
            return Current(ceilingReached: true, stopped: true);
        }

        return Current(ceilingReached: false, stopped: false);
    }

    private void Stop(string reason)
    {
        if (Phase is PumpPhase.Stopped or PumpPhase.Faulted or PumpPhase.Disarmed) return;
        hydraulics.Close();
        Phase = PumpPhase.Stopped;
        ledger.Append("valve.close", $"{_armed?.SaleId} {reason} {Volume} {Money} totalizer {totalizer.Total}");
        _armed = null;
    }

    public void Disarm()
    {
        hydraulics.Close();
        _armed = null;
        Phase = PumpPhase.Disarmed;
    }

    private PumpTick Current(bool ceilingReached, bool stopped)
    {
        var phaseName = Phase == PumpPhase.Dispensing ? _tickPhase : Phase.ToString().ToLowerInvariant();
        return new PumpTick(Phase, Volume, Money, hydraulics.CurrentFlow, phaseName, ceilingReached, stopped, LastFault);
    }
}
