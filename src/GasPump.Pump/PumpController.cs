using GasPump.Core;
using GasPump.Security;

namespace GasPump.Pump;

/// <summary>
/// Disarmed: no authorization. Armed: authorized, valve shut, waiting for the
/// driver. Dispensing: lever held, valve open. Paused: a pour has happened
/// or the nozzle is out, valve shut, the driver may squeeze again. Stopped:
/// the delivery ended. Faulted: something looked like tampering or a part failed.
/// </summary>
/// <summary>
/// Where the pump is. Disarmed and Stopped are the resting phases between
/// sales. Calling is the nozzle off the boot with no authorization: the pump
/// is asking for one. Closed is the operator's switch: the pump works and
/// refuses sales. Armed, Dispensing, and Paused are a sale in progress.
/// Faulted is a part that stopped the pump.
/// </summary>
public enum PumpPhase { Disarmed, Armed, Dispensing, Paused, Stopped, Faulted, Calling, Closed }

/// <summary>What one tick produced. The worker turns this into sale events. Fault carries the formatted code and detail; FaultCode the code alone.</summary>
public sealed record PumpTick(
    PumpPhase Phase,
    Volume Volume,
    Money Money,
    FlowRate Flow,
    string TaperPhase,
    bool CeilingReached,
    bool Stopped,
    string? Fault,
    string? FaultCode,
    /// <summary>Money itemized by the price schedule. The sum is Money.</summary>
    IReadOnlyList<ChargeLine> Lines);

/// <summary>
/// The dispenser's controller. Single-threaded by contract: the pump worker is the
/// only caller. It opens the valve only after the authority armed it, only
/// while the driver holds the lever, only with the spout inserted in a fill
/// point, and only after a grade is chosen. It closes the valve on the cent,
/// and it stops on anything that looks like tampering or a failed part:
/// pulses the valve could not have passed, fuel moving while the valve is
/// shut, fuel moving with no pulses, no fuel moving with the valve open, a
/// separated hose, a sale running past its time. Every stop carries a
/// diagnostic code naming the part.
///
/// Two seams for parts that are not here yet. The price schedule is an
/// interface, so a fee or a discount wraps the price board without this
/// class knowing. The delivery stages see every metered slice, can hold the
/// pump out of service, and can take the hose for a slice (fuel valve shut,
/// fuel meter idle, the switch in the ledger), so an additive injector or a
/// vapor monitor bolts on without this class changing.
///
/// Nozzle lifecycle. Lift takes the nozzle out of the boot. InsertSpout puts the spout in
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
    IPriceSchedule pricing,
    TaperPolicy taper,
    TimeProvider clock,
    IReadOnlyList<IDeliveryStage>? stages = null,
    TransactionBuffer? transactions = null)
{
    private readonly IReadOnlyList<IDeliveryStage> _stages = stages ?? [];

    /// <summary>The finished deliveries the island still holds for the station. The pump refuses to arm while one is open.</summary>
    public TransactionBuffer Transactions { get; } = transactions ?? new TransactionBuffer(ledger);

    /// <summary>The transaction this sale's delivery wrote, so the event that ends the sale can name it. Null when no pour ended for that sale.</summary>
    public long? TransactionFor(string saleId) => Transactions.Last is { } t && t.SaleId == saleId ? t.TxSeq : null;

    /// <summary>The code an arm refusal carries while a delivered transaction waits to be cleared.</summary>
    public const string TransactionPayable = "PUMP.TX.PAYABLE";

    /// <summary>Fuel moved and the delivery is over: write the transaction the station will settle from.</summary>
    private void RecordTransaction(string endReason)
    {
        if (_armed is null || !_poured || Volume == Volume.Zero) return;
        Transactions.Record(_armed.SaleId, _grade ?? Grade.Regular, Volume, Money, Lines, clock.GetUtcNow(), endReason);
    }

    /// <summary>The parts on the delivery path downstream of the meter, in the order they see each slice.</summary>
    public IReadOnlyList<IDeliveryStage> Stages => _stages;

    /// <summary>Valve open and nothing moving for this long is a supply fault. Three seconds covers a slow-flow solenoid opening.</summary>
    public static readonly TimeSpan NoFlowWindow = TimeSpan.FromSeconds(3);
    /// <summary>Fuel moving with no pulses for this long is a dead pulser. Short, because every tick of it is unmetered fuel.</summary>
    public static readonly TimeSpan NoPulseWindow = TimeSpan.FromMilliseconds(300);

    private ArmedAuthorization? _armed;
    private DateTimeOffset _armedAt;
    private Grade? _grade;
    private bool _nozzleLifted;
    private bool _spoutInserted;
    private bool _leverHeld;
    private bool _poured;
    private string _tickPhase = "armed";
    private TimeSpan _noFlowFor;
    private TimeSpan _noPulseFor;
    private TimeSpan _pouredFor;
    private IDeliveryStage? _hoseOwner;

    private bool _closedForSales;

    public PumpPhase Phase { get; private set; } = PumpPhase.Disarmed;
    /// <summary>True while the operator holds the pump out of sales. Takes effect at once when idle, and at the end of a delivery in progress.</summary>
    public bool ClosedForSales => _closedForSales;
    public Money Money { get; private set; }
    /// <summary>Money itemized by the price schedule, for the receipt and the settlement. Empty until fuel moves.</summary>
    public IReadOnlyList<ChargeLine> Lines { get; private set; } = [];
    public Volume Volume => meter.SaleVolume;
    public string? LastFault { get; private set; }
    public string? LastFaultCode { get; private set; }
    public ArmedAuthorization? Armed => _armed;
    public bool NozzleLifted => _nozzleLifted;
    public bool SpoutInserted => _spoutInserted;
    public bool LeverHeld => _leverHeld;
    public Grade? SelectedGrade => _grade;
    /// <summary>True once any fuel has moved in this sale. After this, hanging the nozzle ends the delivery.</summary>
    public bool Poured => _poured;
    public bool BreakawayOpen => hydraulics.BreakawaySeparated;
    public bool SupplyAvailable => hydraulics.SupplyAvailable;

    /// <summary>Arm from a signed token. A preselected grade is a convenience for a driver who chose at the card reader.</summary>
    public (bool Ok, string Reason, ArmedAuthorization? Armed) Arm(string saleId, string tokenWire, Grade? preselectedGrade = null)
    {
        if (Phase is PumpPhase.Dispensing or PumpPhase.Paused)
            return (false, "AlreadyDispensing", null);
        if (Conditions().FirstOrDefault() is { } blocking)
        {
            ledger.Append("arm.refused", blocking.Format($"{saleId} pump out of service: {blocking.Part}. {ServiceActions.Describe(blocking.ClearedBy)}"));
            return (false, blocking.Code, null);
        }

        if (Transactions.HasOpen)
        {
            // The last delivery is not settled yet. The forecourt rule: no new sale until the controller clears the buffer.
            var open = Transactions.Open[0];
            ledger.Append("arm.refused", $"{saleId} {TransactionPayable} #{open.TxSeq} {open.SaleId} {open.Money} awaits clearance");
            return (false, TransactionPayable, null);
        }
        var (armed, refusal) = authority.TryArm(saleId, tokenWire);
        if (armed is null) return (false, refusal, null);

        _armed = armed;
        _armedAt = clock.GetUtcNow();
        _grade = preselectedGrade;
        _poured = false;
        _leverHeld = false;
        _noFlowFor = TimeSpan.Zero;
        _noPulseFor = TimeSpan.Zero;
        _pouredFor = TimeSpan.Zero;
        _hoseOwner = null;
        Money = Money.Zero;
        Lines = [];
        LastFault = null;
        LastFaultCode = null;
        meter.ResetSale();
        hydraulics.Close();
        foreach (var stage in _stages) stage.OnArmed(saleId);
        Phase = PumpPhase.Armed;
        return (true, "", armed);
    }

    /// <summary>The grade buttons. Locked once fuel has moved: one grade per sale, because the price per gallon is fixed at the first pour.</summary>
    public (bool Ok, string Reason) SelectGrade(Grade grade)
    {
        if (_armed is null || Phase is not (PumpPhase.Armed or PumpPhase.Paused)) return (false, $"not armed ({Phase})");
        if (_poured) return (false, "grade locked after the first pour");
        _grade = grade;
        ledger.Append("grade.selected", $"{_armed.SaleId} {grade}");
        return (true, "");
    }

    /// <summary>
    /// The boot switch. With a sale armed, the lift is part of the hand
    /// sequence. With no sale, a resting pump moves to Calling: the nozzle is
    /// out and the pump is asking the station for an authorization. A closed
    /// or faulted pump records the lift and stays where it is. Returns true
    /// when the station should hear about it.
    /// </summary>
    public bool LiftNozzle()
    {
        _nozzleLifted = true;
        if (_armed is not null)
        {
            ledger.Append("nozzle.lift", $"{_armed.SaleId}");
            return true;
        }
        if (Phase is PumpPhase.Disarmed or PumpPhase.Stopped)
        {
            Phase = PumpPhase.Calling;
            ledger.Append("nozzle.lift.calling", "no authorization, waiting for one");
            return true;
        }
        ledger.Append("nozzle.lift.ignored", $"phase {Phase}");
        return false;
    }

    /// <summary>
    /// The operator's switch. Idle, the pump closes now. Mid-delivery, the
    /// close waits for the stop, so a driver is never cut off. A faulted pump
    /// remembers the close and shows it once the part is fixed.
    /// </summary>
    public void CloseForSales(string by)
    {
        _closedForSales = true;
        ledger.Append("pump.closed", DiagnosticCodes.PumpClosed.Format($"by {by} while {Phase}"));
        if (Phase is PumpPhase.Disarmed or PumpPhase.Stopped or PumpPhase.Calling)
        {
            _nozzleLifted = false;
            _spoutInserted = false;
            _leverHeld = false;
            Phase = PumpPhase.Closed;
        }
    }

    /// <summary>The operator's switch, the other way. A closed pump goes back to Disarmed. A pending close is forgotten.</summary>
    public void OpenForSales(string by)
    {
        _closedForSales = false;
        ledger.Append("pump.opened", DiagnosticCodes.PumpClosed.Format($"by {by}"));
        if (Phase == PumpPhase.Closed) Phase = PumpPhase.Disarmed;
    }

    /// <summary>The phase a delivery or a fault returns to: Closed if the operator asked for it meanwhile, Disarmed otherwise.</summary>
    private PumpPhase RestingPhase => _closedForSales ? PumpPhase.Closed : PumpPhase.Disarmed;

    /// <summary>The bellows interlock: the spout is in a tank or can.</summary>
    public bool InsertSpout()
    {
        _spoutInserted = true;
        if (_armed is null) return false;
        ledger.Append("spout.inserted", $"{_armed.SaleId}");
        return true;
    }

    /// <summary>The spout left the fill pipe. Flow stops at once; the driver may insert the spout and squeeze again.</summary>
    public bool WithdrawSpout()
    {
        _spoutInserted = false;
        if (Phase != PumpPhase.Dispensing) return false;
        Pause("spout withdrawn from the fill pipe");
        return true;
    }

    /// <summary>
    /// The lever. Three interlocks, all owned here: armed, grade chosen,
    /// nozzle lifted and spout inserted. Returns whether this squeeze started the first pour.
    /// </summary>
    public (bool Ok, string Reason, bool FirstPour) SqueezeLever()
    {
        _leverHeld = true;
        if (_armed is null || Phase is not (PumpPhase.Armed or PumpPhase.Paused))
        {
            ledger.Append("lever.ignored", $"phase {Phase}");
            return (false, $"not armed ({Phase})", false);
        }
        if (_grade is null) { ledger.Append("lever.ignored", $"{_armed.SaleId} no grade selected"); return (false, "select a grade first", false); }
        if (!_nozzleLifted) { ledger.Append("lever.ignored", $"{_armed.SaleId} nozzle in boot"); return (false, "lift the nozzle first", false); }
        if (!_spoutInserted) { ledger.Append("lever.ignored", $"{_armed.SaleId} spout not in the fill pipe"); return (false, "insert the spout first", false); }

        var first = !_poured;
        _poured = true;
        Phase = PumpPhase.Dispensing;
        _noFlowFor = TimeSpan.Zero;
        hydraulics.SetOpening(1);
        ledger.Append("valve.open", $"{_armed.SaleId} grade {_grade} ceiling {_armed.Ceiling}{(first ? "" : " (resumed)")}");
        return (true, "", first);
    }

    /// <summary>Letting go of the lever shuts the valve. The sale stays open for another squeeze.</summary>
    public bool ReleaseLever()
    {
        _leverHeld = false;
        if (Phase != PumpPhase.Dispensing) return false;
        Pause("lever released");
        return true;
    }

    /// <summary>Back in the boot. Ends the delivery if anything was poured. Returns true when it did.</summary>
    public bool HangNozzle()
    {
        _nozzleLifted = false;
        _spoutInserted = false;
        _leverHeld = false;
        if (Phase == PumpPhase.Calling)
        {
            // The driver put it back without paying. The call is withdrawn.
            Phase = RestingPhase;
            ledger.Append("nozzle.hang.calling", "call withdrawn, no sale");
            return false;
        }
        if (_armed is null || !_poured) return false;
        if (Phase is PumpPhase.Dispensing or PumpPhase.Paused) { Stop("nozzle hung"); return true; }
        return false;
    }

    /// <summary>The attendant reconnected the breakaway coupling and hung the nozzle. The next sale may arm.</summary>
    public void ResetBreakaway()
    {
        hydraulics.BreakawaySeparated = false;
        _nozzleLifted = false;
        _spoutInserted = false;
        ledger.Append("hose.breakaway.reset", DiagnosticCodes.PumpHoseBreakaway.Format("coupling reconnected by the attendant"));
    }

    /// <summary>The conditions holding this pump out of service, read from the part sensors now. Nothing is stored, so a fixed part can never still read as broken.</summary>
    public ServiceStatus Service => ServiceStatus.Of(Conditions());

    /// <summary>One sensor per row, then each delivery stage in order. The order is fixed, so the first entry is the one an arm refusal names.</summary>
    private IEnumerable<DiagnosticCode> Conditions()
    {
        if (_closedForSales) yield return DiagnosticCodes.PumpClosed;
        if (hydraulics.BreakawaySeparated) yield return DiagnosticCodes.PumpHoseBreakaway;
        if (!hydraulics.SupplyAvailable) yield return DiagnosticCodes.PumpSupplyNoFlow;
        if (meter.PulserDead) yield return DiagnosticCodes.PumpMeterNoPulse;
        foreach (var stage in _stages)
            foreach (var condition in stage.Conditions()) yield return condition;
    }

    /// <summary>
    /// A pump faulted for a part that needs service goes back to Disarmed once
    /// no condition holds it. A per-sale fault (spoofed pulses, stray pulses)
    /// stays Faulted until the next arm. Returns true when it moved.
    /// </summary>
    public bool ReturnToService()
    {
        if (Phase != PumpPhase.Faulted) return false;
        if (LastFaultCode is not { } code || DiagnosticCodes.Find(code) is not { OutOfService: true } fault) return false;
        if (Conditions().Any(c => c != DiagnosticCodes.PumpClosed)) return false;
        Phase = RestingPhase;
        LastFault = null;
        LastFaultCode = null;
        ledger.Append("pump.in-service", fault.Format($"{fault.Part} {fault.ClearedBy}; pump disarmed and ready"));
        return true;
    }

    public void CloseValve(string reason) => Stop(reason);

    public void EmergencyStop(DiagnosticCode code, string detail)
    {
        hydraulics.Close();
        Phase = PumpPhase.Faulted;
        LastFault = code.Format(detail);
        LastFaultCode = code.Code;
        ledger.Append("pump.fault", code.Format($"{_armed?.SaleId ?? "-"} {detail} after {Volume} {Money}"));
        RecordTransaction($"fault {code.Code}");
        ClearSale();
    }

    /// <summary>
    /// One slice of time. Checks the clock, moves fuel through the valve at the
    /// taper's opening, counts it, prices it, and clamps the last slice so the
    /// money lands on the ceiling and never past it. Between those steps it
    /// watches the three disagreements that name a failed part: the valve says
    /// open and nothing moves, fuel moves and nothing is counted, more is
    /// counted than could have moved.
    /// </summary>
    public PumpTick Tick(TimeSpan dt)
    {
        // Fuel moving while the valve is shut is a stuck valve or a bypass. Stop everything.
        if (Phase != PumpPhase.Dispensing)
        {
            var strayPulses = meter.TakePulseCount();
            if (strayPulses > 0 && Phase != PumpPhase.Faulted)
            {
                EmergencyStop(DiagnosticCodes.PumpMeterStray, $"{strayPulses} pulses while {Phase}");
                return Current(ceilingReached: false, stopped: true);
            }
            if ((Phase is PumpPhase.Paused or PumpPhase.Armed) && _armed is { } waiting && clock.GetUtcNow() - _armedAt > waiting.MaxDuration)
            {
                Stop(DiagnosticCodes.PumpMaxTime.Format("max sale duration"));
                return Current(ceilingReached: false, stopped: true);
            }
            return Current(ceilingReached: false, stopped: Phase is PumpPhase.Stopped or PumpPhase.Faulted);
        }

        var armed = _armed!;
        var grade = _grade!.Value;
        var now = clock.GetUtcNow();
        if (now - _armedAt > armed.MaxDuration)
        {
            Stop(DiagnosticCodes.PumpMaxTime.Format("max sale duration"));
            return Current(ceilingReached: false, stopped: true);
        }

        // A separated hose is a sensed event, never inferred from zero flow.
        if (hydraulics.BreakawaySeparated)
        {
            EmergencyStop(DiagnosticCodes.PumpHoseBreakaway, "breakaway coupling separated during delivery");
            return Current(ceilingReached: false, stopped: true);
        }

        _pouredFor += dt;

        // A stage may take the hose for this slice: its product moves, fuel does not. The fuel valve is shut,
        // the fuel meter counts nothing, and the switch each way is in the ledger. The two watchdogs rest,
        // because a silent fuel meter is expected while the hose isn't carrying fuel.
        var claimant = _stages.FirstOrDefault(s => s.ClaimsHose(_pouredFor));
        if (claimant is not null)
        {
            if (!ReferenceEquals(_hoseOwner, claimant))
            {
                hydraulics.Close();
                ledger.Append("hose.switch", $"{armed.SaleId} fuel -> {claimant.Name} after {Volume} fuel {Money}, {_pouredFor.TotalSeconds:0.0} s open");
                _hoseOwner = claimant;
            }
            var moved = claimant.Pour(dt, hydraulics.NominalFlow);
            _noFlowFor = TimeSpan.Zero;
            _noPulseFor = TimeSpan.Zero;
            _tickPhase = claimant.Name;
            return Current(ceilingReached: false, stopped: false, hoseFlow: moved > Volume.Zero ? hydraulics.NominalFlow : FlowRate.Zero);
        }
        if (_hoseOwner is { } released)
        {
            ledger.Append("hose.switch", $"{armed.SaleId} {released.Name} -> fuel after {Volume} fuel {Money}, {_pouredFor.TotalSeconds:0.0} s open");
            _hoseOwner = null;
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

        // Move fuel. The flow switch is the fact; the pulser is the claim.
        var delivered = hydraulics.Deliver(dt);
        if (!hydraulics.FlowSwitch)
        {
            // Valve open, nothing moved. A supply fault after the window; before it, wait.
            _noFlowFor += dt;
            if (_noFlowFor >= NoFlowWindow)
            {
                EmergencyStop(DiagnosticCodes.PumpSupplyNoFlow, $"valve open at {opening:0.00}, no flow for {_noFlowFor.TotalSeconds:0.0} s");
                return Current(ceilingReached: false, stopped: true);
            }
            return Current(ceilingReached: false, stopped: false);
        }
        _noFlowFor = TimeSpan.Zero;

        // Creep floor: at least one pulse per tick once the taper is near its floor, so the last cent lands.
        if (delivered == Volume.Zero && cap > Volume.Zero) delivered = new Volume(1);
        bool ceilingReached = false;
        if (delivered >= cap)
        {
            delivered = cap;
            ceilingReached = true;
        }

        meter.Register(delivered);
        var pulses = meter.TakePulseCount();

        // Fuel moved and the pulser said nothing: a dead pulser, and every tick of it is unmetered fuel.
        if (pulses == 0)
        {
            _noPulseFor += dt;
            if (_noPulseFor >= NoPulseWindow)
            {
                EmergencyStop(DiagnosticCodes.PumpMeterNoPulse, $"flow switch closed, no pulses for {_noPulseFor.TotalMilliseconds:0} ms, {meter.UnmeteredVolume} moved unmetered");
                return Current(ceilingReached: false, stopped: true);
            }
            return Current(ceilingReached: false, stopped: false);
        }
        _noPulseFor = TimeSpan.Zero;

        // Pulser sanity. The valve at full open over one tick cannot pass more than this.
        if (pulses > authority.Limits.MaxPulsesPerTick * Math.Max(1.0, dt.TotalMilliseconds / 100.0))
        {
            EmergencyStop(DiagnosticCodes.PumpMeterOverPulse, $"pulser reported {pulses} pulses in one tick, valve could pass at most {authority.Limits.MaxPulsesPerTick}");
            return Current(ceilingReached: false, stopped: true);
        }

        totalizer.Add(delivered);
        foreach (var stage in _stages) stage.OnDelivered(grade, delivered);
        Money = pricing.CentsFor(grade, Volume);
        Lines = pricing.Lines(grade, Volume);

        if (Money > armed.Ceiling)
        {
            // Backstop. MaxVolumeFor makes this unreachable. If it ever happens, the ledger records it.
            EmergencyStop(DiagnosticCodes.PumpCeilingOverrun, $"money {Money} exceeded ceiling {armed.Ceiling}");
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
        if (Phase is PumpPhase.Stopped or PumpPhase.Faulted or PumpPhase.Disarmed or PumpPhase.Closed) return;
        hydraulics.Close();
        Phase = PumpPhase.Stopped;
        ledger.Append("valve.close", $"{_armed?.SaleId} {reason} {Volume} {Money} totalizer {totalizer.Total}");
        RecordTransaction(reason);
        ClearSale();
        // A close the operator asked for during the delivery takes effect now that it is over.
        if (_closedForSales) { Phase = PumpPhase.Closed; ledger.Append("pump.closed", DiagnosticCodes.PumpClosed.Format("pending close applied after the delivery")); }
    }

    /// <summary>The delivery is over. Drop what the driver chose so the next driver starts from nothing. Volume and Money stay for the receipt.</summary>
    private void ClearSale()
    {
        _armed = null;
        _grade = null;
        _poured = false;
        _leverHeld = false;
    }

    public void Disarm()
    {
        hydraulics.Close();
        ClearSale();
        Phase = RestingPhase;
    }

    /// <summary>
    /// The power is going. The valve is already shut by the loss itself; what
    /// matters is that the delivery so far is written to the transaction buffer
    /// before the lights go out, the way a dispenser's battery-backed memory
    /// keeps the running sale. No fault is recorded: the island did nothing wrong.
    /// </summary>
    public void PowerLost()
    {
        if (Phase is not (PumpPhase.Dispensing or PumpPhase.Paused or PumpPhase.Armed)) return;
        hydraulics.Close();
        ledger.Append("valve.close", $"{_armed?.SaleId} power lost {Volume} {Money} totalizer {totalizer.Total}");
        RecordTransaction("power lost");
        ClearSale();
        Phase = RestingPhase;
    }

    /// <summary>The stage holding the hose right now, or null while fuel flows.</summary>
    public IDeliveryStage? HoseOwner => _hoseOwner;

    private PumpTick Current(bool ceilingReached, bool stopped, FlowRate? hoseFlow = null)
    {
        var phaseName = Phase == PumpPhase.Dispensing ? _tickPhase : Phase.ToString().ToLowerInvariant();
        return new PumpTick(Phase, Volume, Money, hoseFlow ?? hydraulics.CurrentFlow, phaseName, ceilingReached, stopped, LastFault, LastFaultCode, Lines);
    }
}
