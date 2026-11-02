using GasPump.Core;
using GasPump.Pump;
using GasPump.Security;

namespace GasPump.Dispatch;

/// <summary>What the dispenser is built from. The same record configures the in-process dispenser and the separate process.</summary>
public sealed record DispenserOptions(
    string PumpId,
    HardLimits HardLimits,
    Pricing Pricing,
    TaperPolicy Taper,
    FlowRate NominalFlow,
    TimeSpan TickPeriod)
{
    public static DispenserOptions From(SimulationOptions o) => new(o.PumpId, o.HardLimits, o.Pricing, o.Taper, o.NominalFlow, o.TickPeriod);
}

/// <summary>
/// The pump's state as the station sees it: enough for the screen, the
/// journal, and the host contract, and nothing the station could act on
/// directly. It crosses the wire after every handled message.
/// </summary>
public sealed record DispenserView(
    string PumpId,
    string RunId,
    int Pid,
    string Phase,
    string? Fault,
    string? FaultCode,
    long TotalizerMilliGallons,
    bool NozzleLifted,
    bool SpoutInserted,
    bool LeverHeld,
    Grade? SelectedGrade,
    bool Poured,
    bool SupplyAvailable,
    bool BreakawayOpen,
    bool PulserDead,
    ArmedAuthorization? Armed,
    bool LedgerIntact,
    string? CustodyPath,
    /// <summary>The conditions holding the pump out of service. The station tells the sale whenever this changes.</summary>
    ServiceStatus Service,
    /// <summary>Delivered transactions the island still holds for the station. Empty is the normal resting state.</summary>
    IReadOnlyList<PumpTransaction>? OpenTransactions = null,
    /// <summary>The board the pump bills by, in mills per gallon. Null until a dispenser has answered, or when it bills by a schedule that is not a plain board.</summary>
    IReadOnlyDictionary<Grade, long>? Prices = null)
{
    /// <summary>The pump is out of service from the station's side: no dispenser has answered, or the link dropped.</summary>
    private static readonly ServiceStatus LinkLost = new([DiagnosticCodes.LinkDispenserLost.Code]);

    /// <summary>What the station shows while no dispenser has answered yet.</summary>
    public static DispenserView Offline(string pumpId) =>
        new(pumpId, "-", 0, "Offline", null, null, 0, false, false, false, null, false, true, false, false, null, true, null, LinkLost);
}

/// <summary>
/// The dispenser side of the seam: the authority, the controller, the wet
/// parts, the sealed counter, and the nonce store, with one entry point. The
/// pump worker calls <see cref="Handle"/> for every message and posts the
/// events it returns to the sale worker. Nothing in here knows whether the
/// sale worker is a mailbox away or a socket away.
/// </summary>
public sealed class Dispenser
{
    /// <param name="prices">The price schedule the pump bills by. Null means the posted price board in <paramref name="options"/>.</param>
    /// <param name="stages">Parts on the delivery path downstream of the meter. Null means none.</param>
    public Dispenser(DispenserOptions options, byte[] tokenKey, EventLedger ledger, IEventLog log, TimeProvider clock,
        IPriceSchedule? prices = null, IReadOnlyList<IDeliveryStage>? stages = null)
    {
        Options = options;
        Ledger = ledger;
        Log = log;
        Nonces = new NonceStore();
        Totalizer = new Totalizer();
        Meter = new Meter();
        Hydraulics = new Hydraulics(options.NominalFlow);
        Authority = new PumpAuthority(new HmacAuthorizationVerifier(tokenKey, options.PumpId, Nonces, TimeSpan.FromSeconds(5)), options.HardLimits, ledger, clock);
        Pump = new PumpController(Authority, Hydraulics, Meter, Totalizer, ledger, prices ?? options.Pricing, options.Taper, clock, stages);
    }

    public DispenserOptions Options { get; }
    public EventLedger Ledger { get; }
    public IEventLog Log { get; }
    public NonceStore Nonces { get; }
    public Totalizer Totalizer { get; }
    public Meter Meter { get; }
    public Hydraulics Hydraulics { get; }
    public PumpAuthority Authority { get; }
    public PumpController Pump { get; }

    /// <summary>
    /// Why the last message was refused, when the refusal is not a sale event.
    /// A refused arm reaches the sale as <see cref="SaleEvent.PumpRefused"/>; a
    /// refused price board concerns no sale, so it is reported here for the
    /// answer. Set by <see cref="Handle"/>, on the pump worker only.
    /// </summary>
    public string? LastRefusal { get; private set; }

    /// <summary>A note for a Done answer, when the pump did what was asked with a caveat: a price board that waits for the delivery to end, or one that changed nothing.</summary>
    public string? LastNote { get; private set; }

    /// <summary>
    /// One message in, zero or more sale events out. Runs on the pump worker
    /// only. The sale id is the station's current sale, which the pump needs
    /// only to address the events it sends back.
    /// </summary>
    public IReadOnlyList<SaleEvent> Handle(PumpMessage m, string saleId)
    {
        var events = new List<SaleEvent>(2);
        LastRefusal = null;
        LastNote = null;
        switch (m)
        {
            case PumpMessage.Arm arm:
                var (ok, reason, armed) = Pump.Arm(arm.SaleId, arm.Token, arm.Grade);
                if (ok)
                {
                    Log.Write("pump", "armed {Ceiling} max {MaxVolume} ({ClampNote})", ("Ceiling", armed!.Ceiling), ("MaxVolume", armed.MaxVolume), ("ClampNote", armed.ClampNote));
                    events.Add(new SaleEvent.PumpArmed(arm.SaleId, armed.Ceiling, armed.MaxVolume));
                    // A pump that was calling already has the nozzle out, and maybe the spout in. The new sale needs to hear both.
                    if (Pump.NozzleLifted) events.Add(new SaleEvent.NozzleLifted(arm.SaleId));
                    if (Pump.SpoutInserted) events.Add(new SaleEvent.SpoutInserted(arm.SaleId));
                }
                else { Log.Warn("pump", "REFUSED arm for {SaleId}: {Reason}", ("SaleId", arm.SaleId), ("Reason", reason)); events.Add(new SaleEvent.PumpRefused(arm.SaleId, reason)); }
                break;

            case PumpMessage.SelectGrade sg:
                var (gradeOk, gradeWhy) = Pump.SelectGrade(sg.Grade);
                if (gradeOk) events.Add(new SaleEvent.GradeSelected(saleId, sg.Grade));
                else Log.Write("pump", "grade {Grade} refused: {Reason}", ("Grade", sg.Grade), ("Reason", gradeWhy));
                break;

            case PumpMessage.Lift:
                if (Pump.LiftNozzle()) events.Add(new SaleEvent.NozzleLifted(saleId));
                else Log.Write("pump", "nozzle lifted while {Phase}, nothing happens", ("Phase", Pump.Phase));
                break;

            case PumpMessage.InsertSpout:
                if (Pump.InsertSpout()) events.Add(new SaleEvent.SpoutInserted(saleId));
                break;

            case PumpMessage.WithdrawSpout:
                var paused = Pump.WithdrawSpout();
                if (Pump.Armed is not null || paused) events.Add(new SaleEvent.SpoutWithdrawn(saleId));
                if (paused) events.Add(new SaleEvent.FlowPaused(saleId, "spout withdrawn from the fill pipe"));
                break;

            case PumpMessage.Squeeze:
                var (flowOk, flowWhy, firstPour) = Pump.SqueezeLever();
                if (!flowOk) Log.Write("pump", "lever ignored: {Reason}", ("Reason", flowWhy));
                else events.Add(firstPour ? new SaleEvent.FlowStarted(saleId) : new SaleEvent.FlowResumed(saleId));
                break;

            case PumpMessage.Release:
                if (Pump.ReleaseLever()) events.Add(new SaleEvent.FlowPaused(saleId, "lever released"));
                break;

            case PumpMessage.Hang:
                var armedBefore = Pump.Armed is not null;
                var wasCalling = Pump.Phase == PumpPhase.Calling;
                var ended = Pump.HangNozzle();
                // After a pour this ends the delivery. Before a pour, or while the pump was only calling, the sale machine notes it.
                if (ended || armedBefore || wasCalling) events.Add(new SaleEvent.NozzleHung(saleId, ended ? Pump.TransactionFor(saleId) : null));
                break;

            case PumpMessage.CloseForSales close:
                Pump.CloseForSales(close.By);
                Log.Write("pump", "{Code} closed for sales by {By}; {Phase}", ("Code", DiagnosticCodes.PumpClosed.Code), ("By", close.By), ("Phase", Pump.Phase));
                break;

            case PumpMessage.OpenForSales open:
                Pump.OpenForSales(open.By);
                Log.Write("pump", "{Code} opened for sales by {By}", ("Code", DiagnosticCodes.PumpClosed.Code), ("By", open.By));
                break;

            case PumpMessage.Close close:
                // A close that interrupts a pour ends the delivery, and the sale must hear it to settle for the fuel.
                var pouring = Pump.Phase is PumpPhase.Dispensing or PumpPhase.Paused;
                Pump.CloseValve(close.Reason);
                if (pouring) events.Add(new SaleEvent.NozzleHung(close.SaleId, Pump.TransactionFor(close.SaleId)));
                break;

            case PumpMessage.Lock lk:
                var locked = Pump.Transactions.Lock(lk.TxSeq, lk.By);
                if (!locked.Ok) Log.Warn("pump", "lock #{TxSeq} refused: {Reason}", ("TxSeq", lk.TxSeq), ("Reason", locked.Reason));
                break;

            case PumpMessage.Clear clr:
                var cleared = Pump.Transactions.Clear(clr.TxSeq, clr.By);
                if (!cleared.Ok) Log.Warn("pump", "clear #{TxSeq} refused: {Reason}", ("TxSeq", clr.TxSeq), ("Reason", cleared.Reason));
                break;

            case PumpMessage.SetPrices sp:
                var (priced, priceWhy, deferred) = Pump.SetPrices(new Pricing(sp.TenthCentsPerGallon), sp.By);
                if (!priced) { LastRefusal = priceWhy; Log.Warn("pump", "price board from {By} refused: {Reason}", ("By", sp.By), ("Reason", priceWhy)); break; }
                LastNote = priceWhy.Length == 0 ? null : priceWhy;
                if (deferred) Log.Write("pump", "price board from {By} waits for the delivery to end: {Board}", ("By", sp.By), ("Board", Pump.PendingPrices?.Describe()));
                else if (priceWhy == "unchanged") Log.Write("pump", "price board from {By} unchanged", ("By", sp.By));
                else Log.Write("pump", "price board from {By}: {Board}", ("By", sp.By), ("Board", (Pump.Prices as Pricing)?.Describe()));
                break;

            case PumpMessage.Stop stop:
                Pump.Disarm();
                Log.Write("pump", "disarmed: {Reason}", ("Reason", stop.Reason));
                break;

            case PumpMessage.SpoofPulses sp:
                Log.Warn("attack", "spoofing {Pulses} pulses", ("Pulses", sp.Pulses));
                Meter.InjectPulses(sp.Pulses);
                break;

            case PumpMessage.Supply s:
                Hydraulics.SupplyAvailable = s.Available;
                break;

            case PumpMessage.PulserDead pd:
                Meter.PulserDead = pd.Dead;
                Log.Warn("pump", "{Code} pulser {State}", ("Code", DiagnosticCodes.PumpMeterNoPulse.Code), ("State", pd.Dead ? "stopped counting" : "repaired"));
                break;

            case PumpMessage.DriveOff:
                Hydraulics.BreakawaySeparated = true;
                Log.Error("pump", "{Code} breakaway coupling separated", ("Code", DiagnosticCodes.PumpHoseBreakaway.Code));
                if (Pump.Phase is PumpPhase.Dispensing or PumpPhase.Paused)
                {
                    // Mid-delivery, lever held or released: the delivery ends here and the sale must hear it now,
                    // because a paused pump has no tick that would notice the coupling.
                    Pump.EmergencyStop(DiagnosticCodes.PumpHoseBreakaway, "breakaway coupling separated during delivery");
                    Log.Error("pump", "FAULT {Code} {Fault}", ("Code", Pump.LastFaultCode), ("Part", DiagnosticCodes.PumpHoseBreakaway.Part), ("Fault", Pump.LastFault));
                    events.Add(new SaleEvent.Fault(saleId, Pump.LastFault!, Pump.LastFaultCode, Pump.TransactionFor(saleId)));
                }
                else
                {
                    // Outside a delivery the sensor still records it, and the next arm is refused until the attendant resets.
                    Ledger.Append("hose.breakaway", DiagnosticCodes.PumpHoseBreakaway.Format($"separated while {Pump.Phase}"));
                    Pump.HangNozzle();
                }
                break;

            case PumpMessage.ResetBreakaway:
                Pump.ResetBreakaway();
                Log.Write("pump", "{Code} cleared by the attendant", ("Code", DiagnosticCodes.PumpHoseBreakaway.Code));
                break;

            case PumpMessage.Tick tick:
                var before = Pump.Phase;
                var t = Pump.Tick(tick.Dt);
                if (t.Fault is not null && before != PumpPhase.Faulted)
                {
                    var part = t.FaultCode is { } fc ? DiagnosticCodes.Find(fc)?.Part : null;
                    Log.Error("pump", "FAULT {Code} {Fault}", ("Code", t.FaultCode), ("Part", part), ("Fault", t.Fault));
                    events.Add(new SaleEvent.Fault(saleId, t.Fault, t.FaultCode, Pump.TransactionFor(saleId)));
                }
                else if (t.CeilingReached)
                {
                    events.Add(new SaleEvent.CeilingReached(saleId, t.Volume, t.Money, t.Lines, Pump.TransactionFor(saleId)));
                }
                else if ((before is PumpPhase.Dispensing or PumpPhase.Paused) && t.Stopped)
                {
                    // Stopped for a non-ceiling reason inside Tick (max duration). Report as hung so the sale completes.
                    events.Add(new SaleEvent.NozzleHung(saleId, Pump.TransactionFor(saleId)));
                }
                else if (t.Phase == PumpPhase.Dispensing)
                {
                    events.Add(new SaleEvent.Progress(saleId, t.Volume, t.Money, t.Flow, t.TaperPhase, t.Lines));
                }
                break;
        }

        // Only a message can change a part, so only a message can put a faulted pump back in service.
        if (m is not PumpMessage.Tick && Pump.ReturnToService())
            Log.Write("pump", "back in service, {Service}", ("Service", Pump.Service.ToString()));
        return events;
    }

    /// <summary>
    /// The broken parts as the sensors read them, for a state file. Restarting
    /// the software repairs nothing on the island, so the next process has to
    /// start with the same hose, tank, and pulser it was left with.
    /// </summary>
    public IReadOnlyList<string> ConditionCodes => Pump.Service.Codes;

    /// <summary>Set the part sensors back to a saved state. The inverse of <see cref="ConditionCodes"/>. A code no part owns is ignored.</summary>
    public void RestoreConditions(IEnumerable<string> codes)
    {
        foreach (var code in codes)
        {
            if (code == DiagnosticCodes.PumpHoseBreakaway.Code) Hydraulics.BreakawaySeparated = true;
            else if (code == DiagnosticCodes.PumpSupplyNoFlow.Code) Hydraulics.SupplyAvailable = false;
            else if (code == DiagnosticCodes.PumpMeterNoPulse.Code) Meter.PulserDead = true;
            else if (code == DiagnosticCodes.PumpClosed.Code) Pump.CloseForSales("restored from the saved state");
            else foreach (var stage in Pump.Stages) if (stage.TryRestoreCondition(code)) break;
        }
    }

    public DispenserView View(string runId, int pid, string? custodyPath) => new(
        Options.PumpId, runId, pid, Pump.Phase.ToString(), Pump.LastFault, Pump.LastFaultCode, Totalizer.Total.MilliGallons,
        Pump.NozzleLifted, Pump.SpoutInserted, Pump.LeverHeld, Pump.SelectedGrade, Pump.Poured,
        Pump.SupplyAvailable, Pump.BreakawayOpen, Meter.PulserDead, Pump.Armed, Ledger.Verify() == 0, custodyPath, Pump.Service, Pump.Transactions.Open,
        (Pump.Prices as Pricing)?.TenthCentsPerGallon);
}

/// <summary>
/// Which side of the seam a record came from, when both sides share one
/// process and one ledger. Over the socket the origin is a fact of transport;
/// in-process it is read off the record. Ledger kinds are named by the part
/// that wrote them, so the prefix is enough.
/// </summary>
public static class Origins
{
    public const string Dispenser = "dispenser";
    public const string Station = "station";

    private static readonly string[] DispenserLedgerPrefixes = ["valve.", "nozzle.", "grade.", "lever.", "arm.", "pump.", "hose.", "totalizer", "price."];

    public static string OfLedger(string kind) => DispenserLedgerPrefixes.Any(kind.StartsWith) ? Dispenser : Station;

    public static string OfLog(LogLine l)
    {
        foreach (var p in l.Props) if (p.Key == "Origin" && p.Value is string s) return s;
        return l.Source == "pump" ? Dispenser : Station;
    }

    /// <summary>Which pump a log line is about, from its Pump property. Null for a line about the site (admin, network, E-stop).</summary>
    public static int? PumpOfLog(LogLine l)
    {
        foreach (var p in l.Props) if (p.Key == "Pump") return p.Value switch { int n => n, string s when int.TryParse(s, out var n) => n, _ => null };
        return null;
    }

    /// <summary>
    /// Which pump a ledger line is about. Every line a fuelling point writes
    /// opens with a sale id (P1-S0001) or the pump id (PUMP-01); the station's
    /// own lines (admin, E-stop, recovery of the site) open with neither and
    /// belong to every pump. The dispenser's ledger lines over the wire are
    /// known by the link that carried them and never come through here.
    /// </summary>
    public static int? PumpOfLedger(string detail)
    {
        foreach (var token in detail.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries))
        {
            if (SaleIds.PumpOf(token) is { } fromSale) return fromSale;
            if (token.StartsWith("PUMP-", StringComparison.Ordinal) && int.TryParse(token.AsSpan(5), out var n) && n > 0) return n;
        }
        return null;
    }
}

/// <summary>
/// Stamps every line with fixed properties, so one shared log still says
/// which side (Origin) and which pump (Pump) wrote it. The in-process
/// dispenser of pump 2 writes with both tags; the station's own lines about
/// pump 2 carry only the pump.
/// </summary>
public sealed class TaggedLog(IEventLog inner, params (string Name, object? Value)[] tags) : IEventLog
{
    public event Action<LogLine>? Written { add => inner.Written += value; remove => inner.Written -= value; }
    public IReadOnlyList<LogLine> Snapshot() => inner.Snapshot();
    public void Write(string source, string message) => inner.Write(source, message, tags);
    public void Write(string source, string template, params (string Name, object? Value)[] props) => inner.Write(source, template, Tag(props));
    public void Warn(string source, string template, params (string Name, object? Value)[] props) => inner.Warn(source, template, Tag(props));
    public void Error(string source, string template, params (string Name, object? Value)[] props) => inner.Error(source, template, Tag(props));
    private (string, object?)[] Tag((string Name, object? Value)[] props) => [.. props, .. tags];
}
