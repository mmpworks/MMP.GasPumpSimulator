using GasPump.Core;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Security;
using MMP.Herald.Compliance.Audit;
using MMP.Herald.Events;

namespace GasPump.Dispatch;

public sealed record SimulationOptions(
    string PumpId,
    /// <summary>The site's starting credit hold ceiling. The admin panel can change it at run time.</summary>
    Money HoldAmount,
    /// <summary>The site's starting debit hold ceiling. Debit holds are real money out of a checking account, so sites keep them low.</summary>
    Money DebitHold,
    string AdminPin,
    TimeSpan TickPeriod,
    FlowRate NominalFlow,
    HardLimits HardLimits,
    TaperPolicy Taper,
    Pricing Pricing,
    SaleTimeouts Timeouts,
    PaymentGatewayOptions Gateway,
    /// <summary>Where the state journal and the TesseraSeal custody file go. Null runs with neither, as the tests do by default.</summary>
    JournalOptions? Journal = null,
    /// <summary>Extra Herald sinks for the debug log (a file, the console). The UI bridge and the custody bridge are always wired.</summary>
    Action<MMP.Herald.Quick.QuickLogBuilder>? LogSinks = null)
{
    public static SimulationOptions Default => new(
        PumpId: "PUMP-07",
        HoldAmount: Money.FromDollars(75),
        DebitHold: Money.FromDollars(50),
        AdminPin: "1234",
        TickPeriod: TimeSpan.FromMilliseconds(100),
        NominalFlow: FlowRate.GallonsPerMinute(10),
        HardLimits: HardLimits.Default,
        Taper: TaperPolicy.Default,
        Pricing: Pricing.Default,
        Timeouts: SaleTimeouts.Default,
        Gateway: PaymentGatewayOptions.Default);
}

/// <summary>
/// Three workers and the wires between them.
///
///   sale worker     owns the SaleStateMachine. The only thing that changes sale state.
///   pump worker     owns the PumpController. The only thing that touches the valve.
///   payment worker  owns the PaymentGateway. The only thing that talks to the host.
///
/// Every arrow between them is a mailbox. The dispatch strategy decides how
/// the workers run. The code in this file does not know or care.
/// </summary>
public sealed class Simulation : IDisposable
{
    private readonly IDispatchStrategy _dispatch;
    private readonly SimulationOptions _options;
    private readonly TimeProvider _clock;
    private readonly IMailbox<SaleEvent> _saleBox;
    private readonly IMailbox<PumpMessage> _pumpBox;
    private readonly IMailbox<PaymentMessage> _paymentBox;
    private readonly List<IDisposable> _owned = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly NetworkSimulator _networkSim;
    private readonly Hydraulics _hydraulics;
    private SaleStateMachine _sale;
    private IDisposable? _saleTimer;
    private DateTimeOffset? _saleDeadline;
    private int _saleCounter;
    private int _inflightPayments;

    /// <param name="log">A log to use instead of the default Herald pipeline. Leave null in the host; tests may pass a plain EventLog. Default is a Herald.OSS pipeline with only the bridge back to this simulation; the host adds file and console sinks.</param>
    public Simulation(IDispatchStrategy dispatch, TimeProvider clock, SimulationOptions? options = null, IssuerSimulator? issuer = null, byte[]? hmacKey = null, IEventLog? log = null)
    {
        _dispatch = dispatch;
        _clock = clock;
        _options = options ?? SimulationOptions.Default;
        _ownsLog = log is null;
        // Two files with two jobs. The Herald debug log takes everything. The TesseraSeal custody file takes the
        // evidence: what the pump did, what the sale did, where the money went, and who touched the admin panel.
        if (_options.Journal is { } j)
        {
            Directory.CreateDirectory(j.Directory);
            RunId = j.RunId ?? $"RUN-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            _custody = new ImmutableAuditFileSink(j.CustodyPath(RunId), j.TenantId, RunId, j.Ikm);
            CustodyPath = j.CustodyPath(RunId);
        }
        else RunId = "RUN-unjournaled";
        Log = log ?? new HeraldEventLog(clock, _options.LogSinks, _custody is null ? null : (_custody, IsEvidence));
        Ledger = new EventLedger(clock);
        Totalizer = new Totalizer();
        Issuer = issuer ?? IssuerSimulator.WithTestCards();
        Network = new NetworkFaultPolicy();
        // The token key is device configuration: it must be the same key after a reboot, or every token issued
        // before the reboot fails on signature instead of on the nonce it already burned. With a journal, derive
        // it from the custody IKM under its own label; without one, a fresh random key is fine for a one-off run.
        var key = hmacKey
            ?? (_options.Journal is { } jk
                ? System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, jk.Ikm, 32, info: System.Text.Encoding.UTF8.GetBytes("gaspump.token-key.v1"))
                : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Signer = new HmacAuthorizationSigner(key);
        _networkSim = new NetworkSimulator(Network, clock);
        Site = new SiteLimits(clock, _options.HoldAmount, _options.DebitHold, _options.AdminPin);
        Drawer = new CashDrawer();
        Gateway = new PaymentGateway(Issuer, _networkSim, Signer, clock, _options.PumpId, _options.Gateway, Site, Drawer);
        // Every admin attempt, good or bad, goes in the ledger. The back office is part of the audit trail.
        Site.Changed += (kind, detail) =>
        {
            Ledger.Append(kind, detail);
            if (kind == "admin.limits") Log.Write("admin", "{Detail}", ("Detail", detail));
            else Log.Warn("admin", "{Kind}: {Detail}", ("Kind", kind), ("Detail", detail));
        };
        Nonces = new NonceStore();
        Authority = new PumpAuthority(new HmacAuthorizationVerifier(key, _options.PumpId, Nonces, TimeSpan.FromSeconds(5)), _options.HardLimits, Ledger, clock);
        Meter = new Meter();
        _hydraulics = new Hydraulics(_options.NominalFlow);
        Pump = new PumpController(Authority, _hydraulics, Meter, Totalizer, Ledger, _options.Pricing, _options.Taper, clock);

        // The ledger is mirrored into Herald under its own category, which is how it reaches the custody file.
        Ledger.Appended += e => Log.Write("ledger", "#{Seq} {Kind} {Detail}", ("Seq", e.Seq), ("Kind", e.Kind), ("Detail", e.Detail), ("Hash", e.Hash), ("Prev", e.PrevHash));
        if (CustodyPath is not null)
        {
            // Custody is written before the UI bridge fires, so a pump of the tail after each line publishes it at once.
            Custody = new CustodyStream(CustodyPath);
            Log.Written += _ => Custody.Pump();
            Ledger.Appended += _ => Custody.Pump();
        }

        // Restore from the journal before any worker runs, then decide what to do about the sale that was open.
        var snapshot = _options.Journal is { } jo ? StateJournal.Load(jo.StatePath) : null;
        if (snapshot is not null) Restore(snapshot);
        LastRecovery = RecoveryPlan.Build(snapshot, clock.GetUtcNow(), _options.Journal?.Period ?? TimeSpan.FromSeconds(1));

        _sale = NewSale();

        _saleBox = dispatch.CreateMailbox<SaleEvent>("sale", 1024);
        _pumpBox = dispatch.CreateMailbox<PumpMessage>("pump", 1024);
        _paymentBox = dispatch.CreateMailbox<PaymentMessage>("payment", 64);

        _owned.Add(dispatch.StartWorker("sale", _saleBox, HandleSaleEvent));
        _owned.Add(dispatch.StartWorker("pump", _pumpBox, HandlePumpMessage));
        _owned.Add(dispatch.StartWorker("payment", _paymentBox, HandlePaymentMessage));
        _owned.Add(dispatch.StartTicker("pump-tick", _options.TickPeriod, _pumpBox, () => new PumpMessage.Tick(_options.TickPeriod)));

        Log.Write("sim", "started with {Dispatch} dispatch, pump {PumpId}, hold {Hold}, run {RunId}", ("Dispatch", dispatch.Name), ("PumpId", _options.PumpId), ("Hold", _options.HoldAmount), ("RunId", RunId));

        ApplyRecovery(LastRecovery, snapshot);

        if (_options.Journal is { } period)
        {
            _journalTimer = clock.CreateTimer(_ => SaveJournal(), null, period.Period, period.Period);
            SaveJournal();
        }
    }

    private readonly ImmutableAuditFileSink? _custody;
    private ITimer? _journalTimer;
    private readonly Lock _journalGate = new();

    /// <summary>Which categories are evidence for the custody file. The rest is debugging and stays in the Herald file.</summary>
    private static bool IsEvidence(LogEvent e) => e.Category.Value is "ledger" or "sale" or "payment" or "admin" or "recovery" or "attack" or "station" or "tank";

    /// <summary>
    /// The custody file, as a stream. Every complete line the TesseraSeal sink
    /// writes is published here byte for byte, so a station server, a message
    /// bus, or a WebSocket client can hold the same chain and verify it with
    /// the same verifier. Null when the journal is off.
    /// </summary>
    public CustodyStream? Custody { get; private set; }

    public string RunId { get; }
    public string? CustodyPath { get; }
    public string? JournalPath => _options.Journal?.StatePath;
    /// <summary>What this run did about the sale the previous run left open. CleanStart when there was none.</summary>
    public RecoveryOutcome LastRecovery { get; }

    /// <summary>
    /// Run TesseraSeal's verifier over this run's custody file. The sink still
    /// holds the file open for append, so the verifier reads a copy taken under
    /// shared access, which is also what an auditor does with a live file.
    /// </summary>
    public (bool Ok, long? FailureAtSeq, string? Reason, int Lines) VerifyCustody()
    {
        if (_options.Journal is not { } j || CustodyPath is null) return (false, null, "no custody file: journal is off", 0);
        var copy = Path.Combine(Path.GetTempPath(), $"custody-verify-{Guid.NewGuid():N}.ndjson");
        try
        {
            using (var src = new FileStream(CustodyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var dst = File.Create(copy)) src.CopyTo(dst);
            var ok = ImmutableAuditFileSink.VerifyChain(copy, j.Ikm, out var seq, out var reason);
            var lines = File.ReadLines(copy).Count();
            return (ok, seq, reason, lines);
        }
        finally { try { File.Delete(copy); } catch (IOException) { } }
    }

    private void Restore(JournalSnapshot s)
    {
        Totalizer.Restore(new Volume(s.TotalizerMilliGallons));
        Nonces.Import(s.Nonces);
        Issuer.Import(s.Issuer);
        Site.Restore(new Money(s.CreditHoldCents), new Money(s.DebitHoldCents), s.FailedPins, s.LockedUntil);
        Drawer.Import(
            s.CashOpenCents.ToDictionary(kv => kv.Key, kv => new Money(kv.Value)),
            s.CashChangeDueCents.ToDictionary(kv => kv.Key, kv => new Money(kv.Value)));
        Gateway.Restore(
            s.Pending.Select(p => new PendingAdvice(p.SaleId, Enum.Parse<PendingKind>(p.Kind), new Money(p.FinalCents))),
            s.Tenders.ToDictionary(kv => kv.Key, kv => Enum.Parse<Tender>(kv.Value)));
        _saleCounter = s.SaleCounter;
    }

    /// <summary>
    /// Close out what the last run left open. The pump is already disarmed (a
    /// new process has no armed authorization), so the only work is money, and
    /// the only honest thing to say about volume is what was recorded.
    /// </summary>
    private void ApplyRecovery(RecoveryOutcome r, JournalSnapshot? snapshot)
    {
        if (snapshot is null) return;
        Ledger.Append("recovery.restart", $"run {RunId} started from journal of {r.PreviousRunId}, saved {r.SnapshotAge.TotalSeconds:0.0} s before, totalizer {Totalizer.Total}");
        switch (r.Kind)
        {
            case RecoveryKind.ClosedOut:
                Ledger.Append("recovery.closeout", $"{r.SaleId} {r.Tender} {r.Grade} {r.Volume} {r.Money} DELIVERY LIKELY UNDER-RECORDED by up to {r.UnrecordedUpTo}");
                Log.Error("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Tender", r.Tender), ("Grade", r.Grade), ("Volume", r.Volume), ("Money", r.Money), ("UnrecordedUpTo", r.UnrecordedUpTo), ("DeliveryConfidence", "under-recorded"));
                _paymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money));
                break;
            case RecoveryKind.Reversed:
                Ledger.Append("recovery.reversed", $"{r.SaleId} {r.Tender} no fuel recorded, hold or cash returned");
                Log.Warn("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Tender", r.Tender));
                _paymentBox.Post(new PaymentMessage.Reverse(r.SaleId!));
                break;
            case RecoveryKind.CompletionResent:
                Ledger.Append("recovery.completion", $"{r.SaleId} {r.Tender} {r.Volume} {r.Money} completion sent again");
                Log.Warn("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Money", r.Money));
                _paymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money));
                break;
            default:
                Log.Write("recovery", "{Note}", ("Note", r.Note));
                break;
        }
        if (Gateway.Pending.Count > 0) _paymentBox.Post(new PaymentMessage.DrainPending(CurrentSaleId));
    }

    private JournalSnapshot CaptureSnapshot(string? stopReason = null)
    {
        SaleSnapshot s;
        int counter;
        lock (_gate) { s = _sale.Snapshot(_saleDeadline); counter = _saleCounter; }
        return new JournalSnapshot(
            JournalSnapshot.CurrentVersion, _clock.GetUtcNow(), RunId, _options.PumpId, counter,
            new JournalSale(s.SaleId, s.State.ToString(), s.Tender.ToString(), s.Grade.ToString(), s.GradeSelected, s.CardName,
                s.Ceiling.Cents, s.Volume.MilliGallons, s.Money.Cents, s.Flow.MilliGallonsPerSecond, s.Hold.ToString(), s.CashPrepaid.Cents),
            Pump.Phase.ToString(), Pump.SelectedGrade?.ToString(),
            Gateway.Pending.Select(p => new JournalPending(p.SaleId, p.Kind.ToString(), p.Final.Cents)).ToList(),
            Gateway.Tenders.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            Drawer.Open.ToDictionary(kv => kv.Key, kv => kv.Value.Cents),
            Drawer.ChangeDue.ToDictionary(kv => kv.Key, kv => kv.Value.Cents),
            Site.CreditHold.Cents, Site.DebitHold.Cents, Site.FailedPins, Site.LockedUntil,
            Totalizer.Total.MilliGallons, Nonces.Export().ToList(), Issuer.Export(), stopReason);
    }

    /// <summary>Write the snapshot. Called by the one-second timer and on every sale change, serialized so two never interleave.</summary>
    public void SaveJournal(string? stopReason = null)
    {
        if (_options.Journal is not { } j) return;
        lock (_journalGate)
        {
            try { StateJournal.Save(j.StatePath, CaptureSnapshot(stopReason)); }
            catch (Exception ex) { Log.Error("journal", "snapshot failed: {Message}", ("Message", ex.Message)); }
        }
    }

    private readonly bool _ownsLog;
    public IEventLog Log { get; }
    public EventLedger Ledger { get; }
    public Totalizer Totalizer { get; }
    public IssuerSimulator Issuer { get; }
    public NetworkFaultPolicy Network { get; }
    public PaymentGateway Gateway { get; }
    public SiteLimits Site { get; }
    public CashDrawer Drawer { get; }
    public PumpAuthority Authority { get; }
    public PumpController Pump { get; }
    public Meter Meter { get; }
    public NonceStore Nonces { get; }
    public HmacAuthorizationSigner Signer { get; }
    public string StrategyName => _dispatch.Name;
    public SimulationOptions Options => _options;
    public TimeProvider Clock => _clock;

    /// <summary>Counters for a harness or a debugger: mailbox backlogs, host calls in flight, host calls parked on the clock.</summary>
    public string Counters => $"sale={_saleBox.Count} pump={_pumpBox.Count} payment={_paymentBox.Count} inflight={Volatile.Read(ref _inflightPayments)} waiting={_networkSim.WaitingOnClock}";

    public event Action<SaleSnapshot>? SaleChanged;

    public SaleSnapshot Snapshot() { lock (_gate) return _sale.Snapshot(_saleDeadline); }
    public string CurrentSaleId { get { lock (_gate) return _sale.SaleId; } }
    public string? LastToken { get { lock (_gate) return _sale.Token; } }

    /// <summary>True when every mailbox is empty, no handler is running, and no host call is in flight.</summary>
    public bool IsIdle => _dispatch.IsIdle && Volatile.Read(ref _inflightPayments) == 0;

    /// <summary>
    /// True when nothing can move until the clock does: the sale and pump boxes
    /// are empty, and the only work left is a host call waiting on the network.
    /// A test harness advances time when it sees this.
    /// </summary>
    public bool IsQuiet
    {
        get
        {
            if (_saleBox.Count > 0 || _pumpBox.Count > 0) return false;
            var inflight = Volatile.Read(ref _inflightPayments);
            if (_paymentBox.Count == 0 && inflight == 0) return true;
            // Every in-flight call must be parked on the clock. One that is not is a continuation still landing.
            return _networkSim.WaitingOnClock >= inflight && inflight > 0;
        }
    }

    // ----- Driver-facing actions. Each one is a message, never a direct call into a worker's state.

    /// <summary>A grade given here is a preselection at the reader. The normal path leaves it null and the driver picks at the pump.</summary>
    public string PresentCard(string cardName, Grade? grade = null, Tender tender = Tender.Credit)
    {
        if (tender == Tender.Cash) throw new ArgumentException("use PayCash for a cash sale", nameof(tender));
        var id = BeginSale();
        _saleBox.Post(new SaleEvent.CardPresented(id, cardName, grade, tender));
        return id;
    }

    /// <summary>The driver paid the cashier. The amount is the ceiling, to the cent. No bank is called.</summary>
    public string PayCash(Money amount, Grade? grade = null)
    {
        var id = BeginSale();
        _saleBox.Post(new SaleEvent.CashPaid(id, amount, grade));
        return id;
    }

    /// <summary>The cashier handed over the change or refund for the current sale.</summary>
    public void CollectChange() => Drawer.ChangeCollected(CurrentSaleId);

    /// <summary>The back office. Every attempt lands in the ledger through Site.Changed.</summary>
    public (bool Ok, string Reason) AdminSetHold(string pin, Tender tender, Money amount) => Site.TrySet(pin, tender, amount);

    private string BeginSale()
    {
        lock (_gate)
        {
            if (_sale.State is not SaleState.Idle) ReplaceSale();
            return _sale.SaleId;
        }
    }

    public void SelectGrade(Grade grade) => _pumpBox.Post(new PumpMessage.SelectGrade(grade));
    public void LiftNozzle() => _pumpBox.Post(new PumpMessage.Lift());
    public void SeatNozzle() => _pumpBox.Post(new PumpMessage.Seat());
    public void UnseatNozzle() => _pumpBox.Post(new PumpMessage.Unseat());
    public void SqueezeTrigger() => _pumpBox.Post(new PumpMessage.Squeeze());
    public void ReleaseTrigger() => _pumpBox.Post(new PumpMessage.Release());
    public void HangNozzle() => _pumpBox.Post(new PumpMessage.Hang());

    /// <summary>The whole hand sequence a driver does at the pump: pick a grade, lift, seat, squeeze. For tests and scripted scenarios.</summary>
    public void StartFueling(Grade grade = Grade.Regular)
    {
        SelectGrade(grade);
        LiftNozzle();
        SeatNozzle();
        SqueezeTrigger();
    }
    public void Cancel(string reason = "driver cancelled") => _saleBox.Post(new SaleEvent.Cancel(CurrentSaleId, reason));
    public void DrainPendingCompletions() => _paymentBox.Post(new PaymentMessage.DrainPending(CurrentSaleId));

    /// <summary>Start a fresh sale record. The old one stays in the log and the ledger.</summary>
    public void ResetSale()
    {
        lock (_gate) ReplaceSale();
        _pumpBox.Post(new PumpMessage.Stop("reset"));
        Publish();
    }

    /// <summary>Under _gate. Starts a fresh sale and drops the old sale's timer so it cannot fire into the new one.</summary>
    private void ReplaceSale()
    {
        _saleTimer?.Dispose();
        _saleTimer = null;
        _saleDeadline = null;
        _sale = NewSale();
    }

    // ----- Attack surface, for the tests and the UI. Every one of these goes through the same mailboxes an attacker would have to reach.

    /// <summary>Send a token the pump already consumed. The nonce store should refuse it.</summary>
    public void AttackReplayLastToken()
    {
        var token = LastToken;
        if (token is null) { Log.Write("attack", "replay: no token to replay yet"); return; }
        _pumpBox.Post(new PumpMessage.Arm(CurrentSaleId, token, Snapshot().Grade));
    }

    /// <summary>Take the last real token and raise its ceiling without re-signing. The HMAC should refuse it.</summary>
    public void AttackTamperCeiling(Money newCeiling)
    {
        var token = LastToken;
        if (token is null) { Log.Write("attack", "tamper: no token yet"); return; }
        var parsed = AuthorizationToken.Parse(token);
        var forged = (parsed with { CeilingCents = newCeiling.Cents }).Serialize();
        _pumpBox.Post(new PumpMessage.Arm(CurrentSaleId, forged, Snapshot().Grade));
    }

    /// <summary>
    /// The Detroit case. A token signed with the real key, for 600 gallons and $2,000.
    /// Upstream was fully compromised. The pump's hard limits should clamp it to 50 gallons and $500.
    /// </summary>
    public string AttackSignedOverLimit(Volume volume, Money money)
    {
        string id;
        lock (_gate) { ReplaceSale(); id = _sale.SaleId; }
        var token = Signer.Issue(id, _options.PumpId, money, volume, _clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();
        // Skip the issuer entirely: push Approved straight into the sale, as a hacked EPS would.
        _saleBox.Post(new SaleEvent.CardPresented(id, "Good", Grade.Regular));
        _saleBox.Post(new SaleEvent.Approved(id, money, token));
        return id;
    }

    /// <summary>A pulser reporting fuel that did not move. The controller should stop on the first bad tick.</summary>
    public void AttackSpoofPulses(long pulses) => _pumpBox.Post(new PumpMessage.SpoofPulses(pulses));

    /// <summary>Arm with no token at all. There is no code path that accepts this, which is the point.</summary>
    public void AttackArmWithoutToken() => _pumpBox.Post(new PumpMessage.Arm(CurrentSaleId, "", Grade.Regular));

    // ----- Forecourt faults. Each is a message to the pump worker, as a sensor or the tank gauge would deliver it.

    /// <summary>The underground tank ran out, or the gauge shut the line. The pump's valve position means nothing until the supply is back.</summary>
    public void TankRunsDry()
    {
        Log.Warn("tank", "{Code} product below the fuel-out level, supply to the pump stops", ("Code", DiagnosticCodes.TankFuelOut.Code));
        Ledger.Append("tank.fuelout", DiagnosticCodes.TankFuelOut.Format("fuel out, supply stopped"));
        _pumpBox.Post(new PumpMessage.Supply(false));
    }

    public void TankRefilled()
    {
        Log.Write("tank", "{Code} cleared, supply restored", ("Code", DiagnosticCodes.TankFuelOut.Code));
        Ledger.Append("tank.refilled", DiagnosticCodes.TankFuelOut.Format("cleared, supply restored"));
        _pumpBox.Post(new PumpMessage.Supply(true));
    }

    /// <summary>The pulser stops counting while fuel keeps moving. The controller catches it by the flow switch.</summary>
    public void KillPulser() => _pumpBox.Post(new PumpMessage.PulserDead(true));
    public void FixPulser() => _pumpBox.Post(new PumpMessage.PulserDead(false));

    /// <summary>A drive-off: the breakaway coupling separates and both halves seal.</summary>
    public void DriveOff() => _pumpBox.Post(new PumpMessage.DriveOff());

    /// <summary>The attendant reconnected the coupling.</summary>
    public void ResetBreakaway() => _pumpBox.Post(new PumpMessage.ResetBreakaway());

    /// <summary>
    /// The station emergency stop. It is not a message to the pump: it is the
    /// power going away under the pump, the STPs, and every signal circuit, all
    /// at once. The site controller outside the classified area writes the
    /// record first, saves one last snapshot so the restart knows this was an
    /// E-stop and not a crash, then the power drops. Reset is a cashier action
    /// that builds a new Simulation on the same journal.
    /// </summary>
    public void EmergencyStop(string pressedBy = "forecourt button")
    {
        var code = DiagnosticCodes.StationEStop;
        Log.Error("station", "{Code} emergency stop pressed by {By}: power removed from every dispenser and STP", ("Code", code.Code), ("By", pressedBy));
        Ledger.Append("station.estop", code.Format($"pressed by {pressedBy}; sale {CurrentSaleId} {Snapshot().State} {Snapshot().Volume} {Snapshot().Money}"));
        Custody?.Pump();
        SaveJournal(stopReason: "ESTOP");
        TearDown();
    }

    /// <summary>
    /// Guess the admin PIN from 0000 upward. The panel locks after three wrong
    /// tries, and every try is in the ledger. Returns how many guesses ran.
    /// </summary>
    public int AttackBruteForceAdminPin(int guesses = 10)
    {
        Log.Warn("attack", "brute-forcing the admin PIN with {Guesses} guesses", ("Guesses", guesses));
        int tried = 0;
        for (int i = 0; i < guesses; i++)
        {
            tried++;
            var (ok, reason) = Site.TrySet(i.ToString("0000"), Tender.Debit, Money.FromDollars(5000));
            if (ok) { Log.Error("attack", "PIN {Pin} accepted, debit hold raised to $5000", ("Pin", i.ToString("0000"))); return tried; }
            if (reason.Contains("locked", StringComparison.Ordinal)) break;
        }
        Log.Write("attack", "brute force stopped after {Tried} guesses: {State}", ("Tried", tried), ("State", Site.LockedUntil is null ? "panel open" : "panel locked"));
        return tried;
    }

    // ----- Workers. Each handler runs one message at a time on its own worker.

    private void HandleSaleEvent(SaleEvent e)
    {
        IReadOnlyList<SaleCommand> commands;
        SaleState before, after;
        SaleSnapshot snap;
        lock (_gate)
        {
            if (e.SaleId != _sale.SaleId) { HandleOrphanEvent(e); return; }
            before = _sale.State;
            commands = _sale.Apply(e);
            after = _sale.State;
            snap = _sale.Snapshot();
        }
        foreach (var c in commands) Execute(c);
        RecordSaleBoundary(e, before, after, snap);
        Publish();
    }

    /// <summary>
    /// The session between the driver and the pump, as ledger entries: one at
    /// the open, one at the close. With the pump's own entries between them,
    /// the custody file tells the whole sale without the debug log.
    /// </summary>
    private void RecordSaleBoundary(SaleEvent e, SaleState before, SaleState after, SaleSnapshot s)
    {
        if (before == SaleState.Idle && after == SaleState.Authorizing)
            Ledger.Append("sale.open", $"{s.SaleId} {s.Tender} {(s.Tender == Tender.Cash ? s.CashPrepaid.ToString() : s.CardName)} grade {(s.GradeSelected ? s.Grade.ToString() : "unselected")}");
        else if (before != after && (after == SaleState.Idle || SaleStateMachine.IsTerminal(after)) && before != SaleState.Idle)
            Ledger.Append("sale.close", $"{s.SaleId} {after} {s.End} {s.Volume} {s.Money} hold {s.Hold}{(s.Tender == Tender.Cash ? $" change {s.ChangeDue}" : "")}");
    }

    /// <summary>
    /// A money event for a sale the screen has already moved past. The driver
    /// is gone or on a new sale, so the machine cannot carry it. A late approval
    /// is the one that costs money: the bank holds funds for a sale nobody will
    /// finish, so the reversal goes straight to the payment worker.
    /// </summary>
    private void HandleOrphanEvent(SaleEvent e)
    {
        switch (e)
        {
            case SaleEvent.Approved a:
                Log.Warn("sale", "LATE approval {Ceiling} for finished sale {SaleId}; reversing the orphan hold (saga compensation)", ("Ceiling", a.Ceiling), ("SaleId", a.SaleId));
                _paymentBox.Post(new PaymentMessage.Reverse(a.SaleId));
                break;
            case SaleEvent.Reversed:
                Log.Write("sale", "hold for earlier sale {SaleId} released", ("SaleId", e.SaleId));
                break;
            case SaleEvent.CompletionSent cs:
                Log.Write("sale", "completion {Final} for earlier sale {SaleId} posted", ("Final", cs.Final), ("SaleId", e.SaleId));
                break;
            case SaleEvent.ReversalQueued or SaleEvent.CompletionQueued or SaleEvent.Declined or SaleEvent.AuthorizationTimedOut:
                Log.Write("sale", "{Event} for earlier sale {SaleId} noted", ("Event", e.GetType().Name), ("SaleId", e.SaleId));
                break;
            default:
                Log.Write("sale", "dropped {Event} for other sale {SaleId}", ("Event", e.GetType().Name), ("SaleId", e.SaleId));
                break;
        }
    }

    private void Execute(SaleCommand c)
    {
        switch (c)
        {
            case SaleCommand.RequestAuthorization ra:
                var card = TestCards.ByName.TryGetValue(ra.CardName, out var known) ? known : new CardData("0000000000000000", 1, 2000, CardKind.Credit);
                _paymentBox.Post(new PaymentMessage.PreAuthorize(ra.SaleId, card, ra.Tender));
                break;
            case SaleCommand.RequestCashAuthorization rc:
                _paymentBox.Post(new PaymentMessage.CashAuthorize(rc.SaleId, rc.Amount));
                break;
            case SaleCommand.ArmPump ap:
                _pumpBox.Post(new PumpMessage.Arm(ap.SaleId, ap.Token, ap.Grade));
                break;
            case SaleCommand.CloseValve cv:
                _pumpBox.Post(new PumpMessage.Close(cv.SaleId, "sale closed"));
                break;
            case SaleCommand.SendCompletion sc:
                _paymentBox.Post(new PaymentMessage.Complete(sc.SaleId, sc.Final));
                break;
            case SaleCommand.Reverse rv:
                _paymentBox.Post(new PaymentMessage.Reverse(rv.SaleId));
                break;
            case SaleCommand.ArmTimer at:
                lock (_gate)
                {
                    _saleTimer?.Dispose();
                    _saleTimer = _dispatch.StartTimer("sale-timeout", at.After, _saleBox, () => new SaleEvent.Timeout(at.SaleId, at.State));
                    _saleDeadline = _clock.GetUtcNow() + at.After;
                }
                break;
            case SaleCommand.CancelTimer:
                lock (_gate) { _saleTimer?.Dispose(); _saleTimer = null; _saleDeadline = null; }
                break;
            case SaleCommand.Log lg:
                Log.Write("sale", lg.Message);
                break;
        }
    }

    private void HandlePumpMessage(PumpMessage m)
    {
        var saleId = CurrentSaleId;
        switch (m)
        {
            case PumpMessage.Arm arm:
                var (ok, reason, armed) = Pump.Arm(arm.SaleId, arm.Token, arm.Grade);
                if (ok) { Log.Write("pump", "armed {Ceiling} max {MaxVolume} ({ClampNote})", ("Ceiling", armed!.Ceiling), ("MaxVolume", armed.MaxVolume), ("ClampNote", armed.ClampNote)); _saleBox.Post(new SaleEvent.PumpArmed(arm.SaleId, armed.Ceiling, armed.MaxVolume)); }
                else { Log.Warn("pump", "REFUSED arm for {SaleId}: {Reason}", ("SaleId", arm.SaleId), ("Reason", reason)); _saleBox.Post(new SaleEvent.PumpRefused(arm.SaleId, reason)); }
                break;

            case PumpMessage.SelectGrade sg:
                var (gradeOk, gradeWhy) = Pump.SelectGrade(sg.Grade);
                if (gradeOk) _saleBox.Post(new SaleEvent.GradeSelected(saleId, sg.Grade));
                else Log.Write("pump", "grade {Grade} refused: {Reason}", ("Grade", sg.Grade), ("Reason", gradeWhy));
                break;

            case PumpMessage.Lift:
                if (Pump.LiftNozzle()) _saleBox.Post(new SaleEvent.NozzleLifted(saleId));
                else Log.Write("pump", "nozzle lifted while {Phase}, nothing happens", ("Phase", Pump.Phase));
                break;

            case PumpMessage.Seat:
                if (Pump.SeatNozzle()) _saleBox.Post(new SaleEvent.NozzleSeated(saleId));
                break;

            case PumpMessage.Unseat:
                var paused = Pump.UnseatNozzle();
                if (Pump.Armed is not null || paused) _saleBox.Post(new SaleEvent.NozzleUnseated(saleId));
                if (paused) _saleBox.Post(new SaleEvent.FlowPaused(saleId, "nozzle removed from fill point"));
                break;

            case PumpMessage.Squeeze:
                var (flowOk, flowWhy, firstPour) = Pump.SqueezeTrigger();
                if (!flowOk) Log.Write("pump", "trigger ignored: {Reason}", ("Reason", flowWhy));
                else _saleBox.Post(firstPour ? new SaleEvent.FlowStarted(saleId) : new SaleEvent.FlowResumed(saleId));
                break;

            case PumpMessage.Release:
                if (Pump.ReleaseTrigger()) _saleBox.Post(new SaleEvent.FlowPaused(saleId, "trigger released"));
                break;

            case PumpMessage.Hang:
                var armedBefore = Pump.Armed is not null;
                var ended = Pump.HangNozzle();
                // Ends the delivery after a pour; before a pour the sale machine just notes it.
                if (ended || armedBefore) _saleBox.Post(new SaleEvent.NozzleHung(saleId));
                break;

            case PumpMessage.Close close:
                Pump.CloseValve(close.Reason);
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
                _hydraulics.SupplyAvailable = s.Available;
                break;

            case PumpMessage.PulserDead pd:
                Meter.PulserDead = pd.Dead;
                Log.Warn("pump", "{Code} pulser {State}", ("Code", DiagnosticCodes.PumpMeterNoPulse.Code), ("State", pd.Dead ? "stopped counting" : "repaired"));
                break;

            case PumpMessage.DriveOff:
                _hydraulics.BreakawaySeparated = true;
                Log.Error("pump", "{Code} breakaway coupling separated", ("Code", DiagnosticCodes.PumpHoseBreakaway.Code));
                if (Pump.Phase is not PumpPhase.Dispensing)
                {
                    // Not mid-pour: the sensor still records it, and the next arm is refused until the attendant resets.
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
                    _saleBox.Post(new SaleEvent.Fault(saleId, t.Fault, t.FaultCode));
                }
                else if (t.CeilingReached)
                {
                    _saleBox.Post(new SaleEvent.CeilingReached(saleId, t.Volume, t.Money));
                }
                else if ((before is PumpPhase.Dispensing or PumpPhase.Paused) && t.Stopped)
                {
                    // Stopped for a non-ceiling reason inside Tick (max duration). Report as hung so the sale completes.
                    _saleBox.Post(new SaleEvent.NozzleHung(saleId));
                }
                else if (t.Phase == PumpPhase.Dispensing)
                {
                    _saleBox.Post(new SaleEvent.Progress(saleId, t.Volume, t.Money, t.Flow, t.TaperPhase));
                }
                break;
        }
    }

    private void HandlePaymentMessage(PaymentMessage m)
    {
        Interlocked.Increment(ref _inflightPayments);
        var work = RunPaymentAsync(m);
        if (_dispatch.PrefersBlockingIo)
        {
            // The 2008 way: this worker has its own thread, and the host call blocks it. Nobody else waits.
            work.GetAwaiter().GetResult();
        }
        else
        {
            // The modern way: let the call continue on a callback. The worker moves on. Results post back as events.
            _ = work;
        }
    }

    private async Task RunPaymentAsync(PaymentMessage m)
    {
        try
        {
            switch (m)
            {
                case PaymentMessage.PreAuthorize pa:
                    Log.Write("payment", "pre-auth {Tender} {Hold} on {Last4}", ("Tender", pa.Tender), ("Hold", Site.HoldFor(pa.Tender)), ("Last4", pa.Card.Last4));
                    var result = await Gateway.PreAuthorizeAsync(pa.SaleId, pa.Card, pa.Tender, _cts.Token);
                    if (result.Approved) { Log.Write("payment", "{ReasonCode} ceiling {Ceiling}", ("ReasonCode", result.ReasonCode), ("Ceiling", result.Ceiling)); _saleBox.Post(new SaleEvent.Approved(pa.SaleId, result.Ceiling, result.Token!)); }
                    else if (result.NetworkFailure)
                    {
                        Log.Warn("payment", "network: {Message}", ("Message", result.Message));
                        // Refused means the request never left: no hold, nothing to reverse. Timed out means the bank may have heard us.
                        if (result.ReasonCode == nameof(NetworkOutcome.ConnectionRefused)) _saleBox.Post(new SaleEvent.AuthorizationUnreachable(pa.SaleId, "connection refused"));
                        else _saleBox.Post(new SaleEvent.AuthorizationTimedOut(pa.SaleId));
                    }
                    else { Log.Write("payment", "declined {ReasonCode}: {Message}", ("ReasonCode", result.ReasonCode), ("Message", result.Message)); _saleBox.Post(new SaleEvent.Declined(pa.SaleId, result.ReasonCode, result.Message)); }
                    break;

                case PaymentMessage.CashAuthorize ca:
                    var cash = Gateway.CashAuthorize(ca.SaleId, ca.Amount);
                    Log.Write("payment", "{Message}", ("Message", cash.Message));
                    if (cash.Approved) _saleBox.Post(new SaleEvent.Approved(ca.SaleId, cash.Ceiling, cash.Token!));
                    else _saleBox.Post(new SaleEvent.Declined(ca.SaleId, cash.ReasonCode, cash.Message));
                    break;

                case PaymentMessage.Complete cp:
                    var completion = await Gateway.CompleteAsync(cp.SaleId, cp.Final, _cts.Token);
                    Log.Write("payment", completion.Message);
                    if (completion.Sent) _saleBox.Post(new SaleEvent.CompletionSent(cp.SaleId, cp.Final));
                    else _saleBox.Post(new SaleEvent.CompletionQueued(cp.SaleId, cp.Final));
                    break;

                case PaymentMessage.Reverse rv:
                    var reversed = await Gateway.ReverseAsync(rv.SaleId, _cts.Token);
                    Log.Write("payment", reversed.Message);
                    _saleBox.Post(reversed.Sent ? new SaleEvent.Reversed(rv.SaleId) : new SaleEvent.ReversalQueued(rv.SaleId));
                    break;

                case PaymentMessage.DrainPending:
                    Log.Write("payment", "draining {Queued} queued advice(s)", ("Queued", Gateway.Pending.Count));
                    var sent = await Gateway.DrainPendingAsync(_cts.Token);
                    Log.Write("payment", "drained {Sent} queued advice(s), {Left} left", ("Sent", sent.Count), ("Left", Gateway.Pending.Count));
                    // Tell the sale where its money landed. The machine updates Hold; an orphan is logged.
                    foreach (var advice in sent)
                        _saleBox.Post(advice.Kind == PendingKind.Reversal
                            ? new SaleEvent.Reversed(advice.SaleId)
                            : new SaleEvent.CompletionSent(advice.SaleId, advice.Final));
                    break;
            }
        }
        catch (OperationCanceledException) { Log.Write("payment", "cancelled while handling {Message}", ("Message", m.GetType().Name)); }
        catch (Exception ex)
        {
            Log.Error("payment", "ERROR {Type}: {Message}", ("Type", ex.GetType().Name), ("Message", ex.Message));
        }
        finally
        {
            Interlocked.Decrement(ref _inflightPayments);
        }
    }

    private SaleStateMachine NewSale()
    {
        var id = $"S{Interlocked.Increment(ref _saleCounter):0000}";
        return new SaleStateMachine(id, _options.Timeouts);
    }

    private void Publish()
    {
        SaleSnapshot snap;
        lock (_gate) snap = _sale.Snapshot(_saleDeadline);
        SaleChanged?.Invoke(snap);
        SaveJournal();
    }

    private int _torndown;

    public void Dispose() => TearDown();

    /// <summary>
    /// Power loss, on purpose. Stops the workers with no close-out and no final
    /// snapshot, which is what a crash leaves behind. The next Simulation built
    /// on the same journal directory recovers from the last periodic snapshot.
    /// Dispose does the same work; neither writes a final snapshot, because a
    /// graceful stop has nothing open and a crash gets no chance to.
    /// </summary>
    public void SimulatePowerLoss() => TearDown();

    private void TearDown()
    {
        if (Interlocked.Exchange(ref _torndown, 1) == 1) return;
        _journalTimer?.Dispose();
        _journalTimer = null;
        _cts.Cancel();
        lock (_gate) _saleTimer?.Dispose();
        // The strategy owns the workers, mailboxes, and tickers. One Dispose, in reverse creation order.
        _dispatch.Dispose();
        _cts.Dispose();
        if (_ownsLog && Log is IDisposable d) d.Dispose();
        _custody?.Dispose();
    }
}
