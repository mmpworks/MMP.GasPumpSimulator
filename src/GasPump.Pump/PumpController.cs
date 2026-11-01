using GasPump.Core;
using GasPump.Security;

namespace GasPump.Pump;

/// <summary>
/// Disarmed: no authorization. Armed: authorized, valve shut, waiting for the
/// driver. Dispensing: trigger held, valve open. Paused: a pour has happened
/// or the nozzle is out, valve shut, the driver may squeeze again. Stopped:
/// the delivery ended. Faulted: something looked like tampering.
/// </summary>
public enum PumpPhase { Disarmed, Armed, Dispensing, Paused, Stopped, Faulted }

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
/// only caller. It opens the valve only after the authority armed it, only
/// while the driver holds the trigger, only with the nozzle seated in a fill
/// point, and only after a grade is chosen. It closes the valve on the cent,
/// and it stops dead on anything that looks like tampering: pulses the valve
/// could not have passed, fuel moving while the valve is shut, a sale running
/// past its time.
///
/// Nozzle lifecycle. Lift takes the nozzle off the harness. Seat puts it in
/// the tank or can. Squeeze opens the valve, Release shuts it, as many times
/// as the driver likes. Hang after any pour ends the delivery. Hang before
/// any pour changes nothing; the sale's own timer will cancel it.
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
    private Grade? _grade;
    private bool _nozzleLifted;
    private bool _nozzleSeated;
    private bool _triggerHeld;
    private bool _poured;
    private string _tickPhase = "armed";

    public PumpPhase Phase { get; private set; } = PumpPhase.Disarmed;
    public Money Money { get; private set; }
    public Volume Volume => meter.SaleVolume;
    public string? LastFault { get; private set; }
    public ArmedAuthorization? Armed => _armed;
    public bool NozzleLifted => _nozzleLifted;
    public bool NozzleSeated => _nozzleSeated;
    public bool TriggerHeld => _triggerHeld;
    public Grade? SelectedGrade => _grade;
    /// <summary>True once any fuel has moved in this sale. After this, hanging the nozzle ends the delivery.</summary>
    public bool Poured => _poured;

    /// <summary>Arm from a signed token. A preselected grade is a convenience for a driver who chose at the card reader.</summary>
    public (bool Ok, string Reason, ArmedAuthorization? Armed) Arm(string saleId, string tokenWire, Grade? preselectedGrade = null)
    {
        if (Phase is PumpPhase.Dispensing or PumpPhase.Paused)
            return (false, "AlreadyDispensing", null);

        var (armed, refusal) = authority.TryArm(saleId, tokenWire);
        if (armed is null) return (false, refusal, null);

        _armed = armed;
        _armedAt = clock.GetUtcNow();
        _grade = preselectedGrade;
        _poured = false;
        _triggerHeld = false;
        Money = Money.Zero;
        LastFault = null;
        meter.ResetSale();
        hydraulics.Close();
        Phase = PumpPhase.Armed;
        return (true, "", armed);
    }

    /// <summary>The grade buttons. Locked once fuel has moved: one grade per sale, because the price per gallon is fixed at the first drop.</summary>
    public (bool Ok, string Reason) SelectGrade(Grade grade)
    {
        if (_armed is null || Phase is not (PumpPhase.Armed or PumpPhase.Paused)) return (false, $"not armed ({Phase})");
        if (_poured) return (false, "grade locked after the first pour");
        _grade = grade;
        ledger.Append("grade.selected", $"{_armed.SaleId} {grade}");
        return (true, "");
    }

    /// <summary>The harness switch. Lifting with no authorization is recorded and does nothing else.</summary>
    public bool LiftNozzle()
    {
        _nozzleLifted = true;
        if (_armed is null)
        {
            ledger.Append("nozzle.lift.ignored", $"phase {Phase}");
            return false;
        }
        ledger.Append("nozzle.lift", $"{_armed.SaleId}");
        return true;
    }

    /// <summary>The fill-point sensor: the spout is in a tank or can.</summary>
    public bool SeatNozzle()
    {
        _nozzleSeated = true;
        if (_armed is null) return false;
        ledger.Append("nozzle.seated", $"{_armed.SaleId}");
        return true;
    }

    /// <summary>The spout left the fill point. Flow stops at once; the driver may seat and squeeze again.</summary>
    public bool UnseatNozzle()
    {
        _nozzleSeated = false;
        if (Phase != PumpPhase.Dispensing) return false;
        Pause("nozzle removed from fill point");
        return true;
    }

    /// <summary>
    /// The trigger. Three interlocks, all owned here: armed, grade chosen,
    /// nozzle lifted and seated. Returns whether this squeeze started the first pour.
    /// </summary>
    public (bool Ok, string Reason, bool FirstPour) SqueezeTrigger()
    {
        _triggerHeld = true;
        if (_armed is null || Phase is not (PumpPhase.Armed or PumpPhase.Paused))
        {
            ledger.Append("trigger.ignored", $"phase {Phase}");
            return (false, $"not armed ({Phase})", false);
        }
        if (_grade is null) { ledger.Append("trigger.ignored", $"{_armed.SaleId} no grade selected"); return (false, "select a grade first", false); }
        if (!_nozzleLifted) { ledger.Append("trigger.ignored", $"{_armed.SaleId} nozzle on harness"); return (false, "lift the nozzle first", false); }
        if (!_nozzleSeated) { ledger.Append("trigger.ignored", $"{_armed.SaleId} nozzle not in fill point"); return (false, "put the nozzle in the tank first", false); }

        var first = !_poured;
        _poured = true;
        Phase = PumpPhase.Dispensing;
        hydraulics.SetOpening(1);
        ledger.Append("valve.open", $"{_armed.SaleId} grade {_grade} ceiling {_armed.Ceiling}{(first ? "" : " (resumed)")}");
        return (true, "", first);
    }

    /// <summary>Letting go of the trigger shuts the valve. The sale stays open for another squeeze.</summary>
    public bool ReleaseTrigger()
    {
        _triggerHeld = false;
        if (Phase != PumpPhase.Dispensing) return false;
        Pause("trigger released");
        return true;
    }

    /// <summary>Back on the harness. Ends the delivery if anything was poured. Returns true when it did.</summary>
    public bool HangNozzle()
    {
        _nozzleLifted = false;
        _nozzleSeated = false;
        _triggerHeld = false;
        if (_armed is null || !_poured) return false;
        if (Phase is PumpPhase.Dispensing or PumpPhase.Paused) { Stop("nozzle hung"); return true; }
        return false;
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
        // Fuel moving while the valve is shut is a stuck valve or a bypass. Stop everything.
        if (Phase != PumpPhase.Dispensing)
        {
            var strayPulses = meter.TakePulseCount();
            if (strayPulses > 0 && Phase != PumpPhase.Faulted)
            {
                EmergencyStop($"{strayPulses} pulses while {Phase}");
                return Current(ceilingReached: false, stopped: true);
            }
            if ((Phase is PumpPhase.Paused or PumpPhase.Armed) && _armed is { } waiting && clock.GetUtcNow() - _armedAt > waiting.MaxDuration)
            {
                Stop("max sale duration");
                return Current(ceilingReached: false, stopped: true);
            }
            return Current(ceilingReached: false, stopped: Phase is PumpPhase.Stopped or PumpPhase.Faulted);
        }

        var armed = _armed!;
        var grade = _grade!.Value;
        var now = clock.GetUtcNow();
        if (now - _armedAt > armed.MaxDuration)
        {
            Stop("max sale duration");
            return Current(ceilingReached: false, stopped: true);
        }

        // Where the stop is, in volume: whichever comes first, the money ceiling or the volume ceiling.
        var volumeCap = armed.MaxVolume - Volume;
        var moneyCapVolume = pricing.MaxVolumeFor(grade, armed.Ceiling) - Volume;
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
        Money = pricing.CentsFor(grade, Volume);

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

    private void Pause(string reason)
    {
        hydraulics.Close();
        Phase = PumpPhase.Paused;
        ledger.Append("valve.pause", $"{_armed?.SaleId} {reason} {Volume} {Money}");
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
        _grade = null;
        _poured = false;
        Phase = PumpPhase.Disarmed;
    }

    private PumpTick Current(bool ceilingReached, bool stopped)
    {
        var phaseName = Phase == PumpPhase.Dispensing ? _tickPhase : Phase.ToString().ToLowerInvariant();
        return new PumpTick(Phase, Volume, Money, hydraulics.CurrentFlow, phaseName, ceilingReached, stopped, LastFault);
    }
}
