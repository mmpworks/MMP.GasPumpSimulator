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
    Action<MMP.Herald.Quick.QuickLogBuilder>? LogSinks = null,
    /// <summary>Where the dispenser is. Null builds it in this process behind a mailbox. A host and port reach a <see cref="DispenserServer"/> over TCP.</summary>
    DispenserLinkOptions? Dispenser = null)
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
/// The station: two workers, a link to the dispenser, and the wires between them.
///
///   sale worker     owns the SaleStateMachine. The only thing that changes sale state.
///   payment worker  owns the PaymentGateway. The only thing that talks to the host.
///   dispenser link  carries PumpMessages to the pump worker and SaleEvents back.
///                   In-process the pump worker is a third mailbox here; over a
///                   socket it lives in a DispenserServer in another process.
///
/// Every arrow between workers is a mailbox. The dispatch strategy decides how
/// the workers run. The code in this file doesn't depend on the strategy, or
/// on which side of a process boundary the pump is on.
/// </summary>
public sealed class Simulation : IDisposable
{
    private readonly IDispatchStrategy _dispatch;
    private readonly SimulationOptions _options;
    private readonly TimeProvider _clock;
    private readonly IMailbox<SaleEvent> _saleBox;
    private readonly IMailbox<PaymentMessage> _paymentBox;
    private readonly List<IDisposable> _owned = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly NetworkSimulator _networkSim;
    private readonly IDispenserLink _link;
    private readonly Dispenser? _local;
    private readonly List<LogLine> _dispenserLog = [];
    private readonly List<LedgerEntry> _dispenserLedger = [];
    private string? _dispenserLedgerRun;
    private SaleStateMachine _sale;
    private IDisposable? _saleTimer;
    private DateTimeOffset? _saleDeadline;
    private int _saleCounter;
    private int _inflightPayments;

    /// <param name="log">A log to use instead of the default Herald pipeline. Leave null in the host; tests may pass a plain EventLog. When null, the simulation builds a Herald.OSS pipeline with only the bridge back to itself, and the host adds file and console sinks.</param>
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
        Issuer = issuer ?? IssuerSimulator.WithTestCards();
        Network = new NetworkFaultPolicy();
        // The token key is device configuration: it must be the same key after a reboot, or every token issued
        // before the reboot fails on signature instead of on the nonce it already burned. With a journal, derive
        // it from the custody IKM under its own label; without one, a fresh random key is fine for a one-off run.
        // A dispenser in another process derives the same key from the same IKM (CustodyKeyFile.TokenKey).
        var key = hmacKey
            ?? (_options.Journal is { } jk ? CustodyKeyFile.TokenKey(jk.Ikm) : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Signer = new HmacAuthorizationSigner(key);
        _networkSim = new NetworkSimulator(Network, clock);
        Site = new SiteLimits(clock, _options.HoldAmount, _options.DebitHold, _options.AdminPin);
        Drawer = new CashDrawer();
        Gateway = new PaymentGateway(Issuer, _networkSim, Signer, clock, _options.PumpId, _options.Gateway, Site, Drawer);
        // The compensation log reaches disk the moment it changes, so an intent is journaled before the wire is tried.
        Gateway.PendingChanged += () => SaveJournal();
        // Every admin attempt, good or bad, goes in the ledger. The back office is part of the audit trail.
        Site.Changed += (kind, detail) =>
        {
            Ledger.Append(kind, detail);
            if (kind == "admin.limits") Log.Write("admin", "{Detail}", ("Detail", detail));
            else Log.Warn("admin", "{Kind}: {Detail}", ("Kind", kind), ("Detail", detail));
        };
        // The ledger is mirrored into Herald under its own category, which is how it reaches the custody file.
        Ledger.Appended += e => Log.Write("ledger", "#{Seq} {Kind} {Detail}", ("Seq", e.Seq), ("Kind", e.Kind), ("Detail", e.Detail), ("Hash", e.Hash), ("Prev", e.PrevHash));
        if (CustodyPath is not null)
        {
            // Custody is written before the UI bridge fires, so pumping the tail after each line publishes it right away.
            Custody = new CustodyStream(CustodyPath);
            Log.Written += _ => Custody.Pump();
            Ledger.Appended += _ => Custody.Pump();
        }

        _sale = NewSale();
        _saleBox = dispatch.CreateMailbox<SaleEvent>("sale", 1024);
        _paymentBox = dispatch.CreateMailbox<PaymentMessage>("payment", 64);

        // The dispenser: here, behind a mailbox on the same strategy, or in another process behind a socket.
        if (_options.Dispenser is { } where)
        {
            var socket = new SocketDispenserLink(where, _options.PumpId, Log);
            socket.RemoteLog += l =>
            {
                lock (_dispenserLog)
                {
                    // A reconnect replays the dispenser's recent log; keep each line once.
                    if (_dispenserLog.Count > 0 && _dispenserLog.Any(x => x.At == l.At && x.Message == l.Message)) return;
                    _dispenserLog.Add(l);
                    if (_dispenserLog.Count > 2000) _dispenserLog.RemoveAt(0);
                }
                DispenserLog?.Invoke(l);
            };
            socket.RemoteLedger += e =>
            {
                lock (_dispenserLedger)
                {
                    // A new dispenser run starts its ledger over; a reconnect to the same run replays entries we hold.
                    var run = socket.RemoteRunId;
                    if (run != _dispenserLedgerRun) { _dispenserLedgerRun = run; _dispenserLedger.Clear(); }
                    if (_dispenserLedger.Count > 0 && e.Seq <= _dispenserLedger[^1].Seq) return;
                    _dispenserLedger.Add(e);
                }
                DispenserLedger?.Invoke(e);
            };
            socket.ConnectionChanged += OnDispenserConnection;
            _link = socket;
        }
        else
        {
            _local = new Dispenser(DispenserOptions.From(_options), key, Ledger, new OriginTaggedLog(Log, Origins.Dispenser), clock);
            _link = new LocalDispenserLink(_local, dispatch, _options.TickPeriod, () => CurrentSaleId, Custody, RunId, CustodyPath);
        }
        _link.Event += e => _saleBox.Post(e);
        _link.ViewChanged += v => { OnServiceView(v.Service); DispenserViewChanged?.Invoke(v); };

        // Restore from the journal before any worker runs, then decide what to do about the sale that was open.
        var snapshot = _options.Journal is { } jo ? StateJournal.Load(jo.StatePath) : null;
        if (snapshot is not null)
        {
            Restore(snapshot);
            // Restore sets the sale counter, but the placeholder sale above was numbered before that. Create the first real sale after the restore.
            lock (_gate) _sale = NewSale();
        }
        LastRecovery = RecoveryPlan.Build(snapshot, clock.GetUtcNow(), _options.Journal?.Period ?? TimeSpan.FromSeconds(1));
        // A dispenser not yet answering, or one that started with a part already broken, is out of service from the first sale.
        OnServiceView(_link.View.Service);

        // Capabilities ride beside the sale on their own workers. A capability that throws is switched off and logged, and the sale is unaffected.
        Capabilities = new CapabilityHost(dispatch, Log);
        _link.ViewChanged += v => Capabilities.Publish(v);
        _owned.Add(dispatch.StartWorker("sale", _saleBox, HandleSaleEvent));
        _owned.Add(dispatch.StartWorker("payment", _paymentBox, HandlePaymentMessage));

        Log.Write("sim", "started with {Dispatch} dispatch, pump {PumpId} via {Link}, hold {Hold}, run {RunId}", ("Dispatch", dispatch.Name), ("PumpId", _options.PumpId), ("Link", _link.Kind), ("Hold", _options.HoldAmount), ("RunId", RunId));

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
    /// The station's own custody file, as a stream. Every complete line the
    /// TesseraSeal sink writes is published here byte for byte. In-process the
    /// dispenser writes into this same chain; over a socket the dispenser has
    /// its own, reached through <see cref="SubscribeDispenserCustody"/>. Null
    /// when the journal is off.
    /// </summary>
    public CustodyStream? Custody { get; private set; }

    /// <summary>The link to the pump: in-process mailbox or socket. Read-only facts about it for the host contract.</summary>
    public IDispenserLink Link => _link;

    /// <summary>The pump as the station sees it. Works on both sides of the seam.</summary>
    public DispenserView View => _link.View;

    /// <summary>True when the pump is in this process and <see cref="Pump"/>, <see cref="Meter"/> and friends can be touched directly.</summary>
    public bool DispenserIsLocal => _local is not null;

    public event Action<DispenserView>? DispenserViewChanged;
    /// <summary>Log lines from a dispenser in another process. In-process the shared log carries them with an Origin property.</summary>
    public event Action<LogLine>? DispenserLog;
    /// <summary>Ledger entries from a dispenser in another process. In-process the shared ledger carries them.</summary>
    public event Action<LedgerEntry>? DispenserLedger;

    public IReadOnlyList<LogLine> DispenserLogSnapshot() { lock (_dispenserLog) return _dispenserLog.ToArray(); }
    public IReadOnlyList<LedgerEntry> DispenserLedgerSnapshot() { lock (_dispenserLedger) return _dispenserLedger.ToArray(); }

    /// <summary>
    /// The dispenser's chain, from the header on, then live, with the run id
    /// the lines belong to. In-process that is this run's own file; over a
    /// socket it is what the dispenser sent, byte for byte.
    /// </summary>
    public IDisposable SubscribeDispenserCustody(Action<string, string> onLine) => _link.SubscribeCustody(onLine);
    public long DispenserCustodyLines => _link.CustodyLines;

    /// <summary>
    /// The link came up or went down. Down mid-sale is a fault the station
    /// owns: close the sale for what was recorded, name the part, send the
    /// driver to the attendant. Up means a dispenser is listening; the first
    /// thing it hears is Stop, so a valve left open by a station restart shuts.
    /// </summary>
    private void OnDispenserConnection(bool up)
    {
        if (_frozen) return;   // the station cut this link itself; see EmergencyStop
        var code = DiagnosticCodes.LinkDispenserLost;
        if (up)
        {
            Ledger.Append("link.up", $"dispenser {_link.RemoteRunId} pid {_link.RemotePid}");
            _link.Post(new PumpMessage.Stop("station connected"), CurrentSaleId);
            return;
        }
        var s = Snapshot();
        Ledger.Append("link.down", code.Format($"sale {s.SaleId} {s.State} {s.Volume} {s.Money}"));
        Log.Error("station", "{Code} dispenser link lost during {State}", ("Code", code.Code), ("State", s.State));
        if (s.State is SaleState.Authorizing or SaleState.ReadyToPump or SaleState.Fueling)
            _saleBox.Post(new SaleEvent.Fault(s.SaleId, "dispenser link lost", code.Code));
    }

    /// <summary>
    /// The pump's service status, as the dispenser's view carries it. The
    /// station tells the sale once per change. That one path covers a part
    /// that broke or was fixed on the dispenser, the link dropping (the
    /// offline view names the link), and the link coming back.
    /// </summary>
    private void OnServiceView(ServiceStatus service)
    {
        // One lock from compare to post, so two reporters (the link thread and the constructor) can't cross and leave
        // the sale holding one status while the station holds another. The sale worker drains the box in post order.
        lock (_serviceGate)
        {
            if (_frozen || service == _service) return;
            _service = service;
            string saleId;
            lock (_gate) saleId = _sale.SaleId;
            Ledger.Append("service.status", $"pump {service}");
            Log.Write("station", "pump {Service}", ("Service", service.ToString()), ("Codes", string.Join(",", service.Codes)));
            _saleBox.Post(new SaleEvent.PumpService(saleId, service));
        }
    }

    private readonly Lock _serviceGate = new();
    /// <summary>What the station last heard about the pump; every new sale starts from it. Written under _serviceGate, read under _gate.</summary>
    private volatile ServiceStatus _service = ServiceStatus.Normal;

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
        // A dispenser in another process keeps its own counter and nonces across its own restarts.
        if (_local is { } d)
        {
            d.Totalizer.Restore(new Volume(s.TotalizerMilliGallons));
            d.Nonces.Import(s.Nonces);
            if (s.DispenserConditions is { Count: > 0 } c) d.RestoreConditions(c);
        }
        Issuer.Import(s.Issuer);
        Site.Restore(new Money(s.CreditHoldCents), new Money(s.DebitHoldCents), s.FailedPins, s.LockedUntil);
        Drawer.Import(
            s.CashOpenCents.ToDictionary(kv => kv.Key, kv => new Money(kv.Value)),
            s.CashChangeDueCents.ToDictionary(kv => kv.Key, kv => new Money(kv.Value)));
        Gateway.Restore(
            s.Pending.Select(p => new PendingAdvice(p.SaleId, Enum.Parse<PendingKind>(p.Kind), new Money(p.FinalCents), JournalLine.To(p.Lines))),
            s.Tenders.ToDictionary(kv => kv.Key, kv => Enum.Parse<Tender>(kv.Value)));
        _saleCounter = s.SaleCounter;
    }

    /// <summary>
    /// Close out what the last run left open. The pump is already disarmed (a
    /// new process has no armed authorization), so the only work is money, and
    /// the only volume to report is what was recorded.
    /// </summary>
    private void ApplyRecovery(RecoveryOutcome r, JournalSnapshot? snapshot)
    {
        if (snapshot is null) return;
        Ledger.Append("recovery.restart", $"run {RunId} started from journal of {r.PreviousRunId}, saved {r.SnapshotAge.TotalSeconds:0.0} s before, totalizer {new Volume(View.TotalizerMilliGallons)}");
        switch (r.Kind)
        {
            case RecoveryKind.ClosedOut:
                Ledger.Append("recovery.closeout", $"{r.SaleId} {r.Tender} {r.Grade} {r.Volume} {r.Money} DELIVERY LIKELY UNDER-RECORDED by up to {r.UnrecordedUpTo}");
                Log.Error("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Tender", r.Tender), ("Grade", r.Grade), ("Volume", r.Volume), ("Money", r.Money), ("UnrecordedUpTo", r.UnrecordedUpTo), ("DeliveryConfidence", "under-recorded"));
                _paymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money, r.Lines));
                break;
            case RecoveryKind.Reversed:
                Ledger.Append("recovery.reversed", $"{r.SaleId} {r.Tender} no fuel recorded, hold or cash returned");
                Log.Warn("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Tender", r.Tender));
                _paymentBox.Post(new PaymentMessage.Reverse(r.SaleId!));
                break;
            case RecoveryKind.CompletionResent:
                Ledger.Append("recovery.completion", $"{r.SaleId} {r.Tender} {r.Volume} {r.Money} completion sent again");
                Log.Warn("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Money", r.Money));
                _paymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money, r.Lines));
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
        var view = View;
        return new JournalSnapshot(
            JournalSnapshot.CurrentVersion, _clock.GetUtcNow(), RunId, _options.PumpId, counter,
            new JournalSale(s.SaleId, s.State.ToString(), s.Tender.ToString(), s.Grade.ToString(), s.GradeSelected, s.CardName,
                s.Ceiling.Cents, s.Volume.MilliGallons, s.Money.Cents, s.Flow.MilliGallonsPerSecond, s.Hold.ToString(), s.CashPrepaid.Cents,
                JournalLine.From(s.Lines)),
            view.Phase, view.SelectedGrade?.ToString(),
            Gateway.Pending.Select(p => new JournalPending(p.SaleId, p.Kind.ToString(), p.Final.Cents, JournalLine.From(p.Lines))).ToList(),
            Gateway.Tenders.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
            Drawer.Open.ToDictionary(kv => kv.Key, kv => kv.Value.Cents),
            Drawer.ChangeDue.ToDictionary(kv => kv.Key, kv => kv.Value.Cents),
            Site.CreditHold.Cents, Site.DebitHold.Cents, Site.FailedPins, Site.LockedUntil,
            view.TotalizerMilliGallons, _local?.Nonces.Export().ToList() ?? [], Issuer.Export(), stopReason,
            _local?.ConditionCodes.ToList());
    }

    /// <summary>Write the snapshot. Called by the one-second timer and on every sale change, serialized so two never interleave.</summary>
    public void SaveJournal(string? stopReason = null)
    {
        if (_options.Journal is not { } j) return;
        lock (_journalGate)
        {
            if (_frozen) return;
            try { StateJournal.Save(j.StatePath, CaptureSnapshot(stopReason)); }
            catch (Exception ex) { Log.Error("journal", "snapshot failed: {Message}", ("Message", ex.Message)); }
        }
    }

    private readonly bool _ownsLog;
    public IEventLog Log { get; }
    public EventLedger Ledger { get; }
    public IssuerSimulator Issuer { get; }
    public NetworkFaultPolicy Network { get; }
    public PaymentGateway Gateway { get; }
    public SiteLimits Site { get; }
    public CashDrawer Drawer { get; }
    public HmacAuthorizationSigner Signer { get; }
    public string StrategyName => _dispatch.Name;
    public SimulationOptions Options => _options;
    public TimeProvider Clock => _clock;

    // The pump's parts, reachable only while the dispenser is in this process. Over a socket they are another
    // process's memory, and View is the whole truth the station has.
    private Dispenser Local => _local ?? throw new InvalidOperationException($"the dispenser is remote ({_link.Kind}); read View instead");
    public PumpAuthority Authority => Local.Authority;
    public PumpController Pump => Local.Pump;
    public Meter Meter => Local.Meter;
    public NonceStore Nonces => Local.Nonces;
    public Totalizer Totalizer => Local.Totalizer;

    /// <summary>Counters for a harness or a debugger: mailbox backlogs, host calls in flight, host calls parked on the clock.</summary>
    public string Counters => $"sale={_saleBox.Count} pump={_link.Backlog} payment={_paymentBox.Count} inflight={Volatile.Read(ref _inflightPayments)} waiting={_networkSim.WaitingOnClock} link={_link.Kind}:{(_link.Connected ? "up" : "down")}";

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
            if (_saleBox.Count > 0 || _link.Backlog > 0) return false;
            var inflight = Volatile.Read(ref _inflightPayments);
            if (_paymentBox.Count == 0 && inflight == 0) return true;
            // Every in-flight call must be parked on the clock. One that is not is a continuation still landing.
            return _networkSim.WaitingOnClock >= inflight && inflight > 0;
        }
    }

    // ----- Driver-facing actions. Each one posts a message to a worker. None calls into a worker's state directly.

    /// <summary>A grade given here is a preselection at the reader. The normal path leaves it null and the driver picks at the pump.</summary>
    public string PresentCard(string cardName, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert)
    {
        var (ok, id, reason) = TryPresentCard(cardName, grade, tender, source);
        if (!ok) Log.Warn("sale", "card {Card} ignored: {Reason}", ("Card", cardName), ("Reason", reason));
        return id;
    }

    /// <summary>
    /// A card at the reader or the counter. If the sale on the screen is approved
    /// and untouched (the last driver left without lifting the nozzle), this card
    /// cancels it, reverses its hold, and starts over. If fuel is moving or the
    /// nozzle is in someone's hand, the pump is in use and the card is refused.
    /// </summary>
    public (bool Ok, string SaleId, string Reason) TryPresentCard(string cardName, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert)
    {
        if (tender == Tender.Cash) throw new ArgumentException("use PayCash for a cash sale", nameof(tender));
        var (ok, id, reason) = BeginSale($"new card {cardName} {SaleStateMachine.SourceText(source)}");
        if (!ok) return (false, id, reason);
        _saleBox.Post(new SaleEvent.CardPresented(id, cardName, grade, tender, source));
        return (true, id, "");
    }

    /// <summary>The driver paid the cashier. The amount is the ceiling, to the cent. No bank is called.</summary>
    public string PayCash(Money amount, Grade? grade = null)
    {
        var (ok, id, reason) = BeginSale($"cash {amount} at the station");
        if (!ok) { Log.Warn("sale", "cash sale refused: {Reason}", ("Reason", reason)); return id; }
        _saleBox.Post(new SaleEvent.CashPaid(id, amount, grade));
        return id;
    }

    /// <summary>The cashier handed over the change or refund for the current sale.</summary>
    public void CollectChange() => Drawer.ChangeCollected(CurrentSaleId);

    /// <summary>The back office. Every attempt lands in the ledger through Site.Changed.</summary>
    public (bool Ok, string Reason) AdminSetHold(string pin, Tender tender, Money amount) => Site.TrySet(pin, tender, amount);

    /// <summary>
    /// Start a sale record for a new payment. A pump out of service refuses
    /// every payment, at the pump and at the counter, before any money moves.
    /// Otherwise the sale already on the screen falls into one of three cases.
    /// Finished: replace it. Approved but untouched (the last driver drove off
    /// before lifting the nozzle): cancel it, reverse its hold, and replace it
    /// in one step. In someone's hands: refuse the new payment.
    /// </summary>
    private (bool Ok, string SaleId, string Reason) BeginSale(string why)
    {
        lock (_gate)
        {
            var oldId = _sale.SaleId;
            if (!_service.InService)
                return (false, oldId, $"pump {_service}");
            if (_sale.State is SaleState.Fueling or SaleState.Completing || (_sale.State == SaleState.ReadyToPump && _sale.NozzleOut))
                return (false, oldId, $"pump in use by {oldId} ({_sale.State}, nozzle {(_sale.NozzleOut ? "out" : "on harness")})");
            if (!_sale.IsOpenAndUntouched)
            {
                if (_sale.State is not SaleState.Idle) ReplaceSale();
                return (true, _sale.SaleId, "");
            }
        }
        CancelOpenSaleAndReplace($"superseded: {why}");
        return (true, CurrentSaleId, "");
    }

    /// <summary>
    /// End a sale that holds money and has moved no fuel (Authorizing or
    /// ReadyToPump), the way the sale worker would: cancel it, which reverses
    /// the hold through the payment worker, record the close in the ledger,
    /// and put a fresh sale on the screen. Returns false and changes nothing
    /// when the sale on the screen is in any other state.
    /// </summary>
    private bool CancelOpenSaleAndReplace(string why)
    {
        IReadOnlyList<SaleCommand> commands;
        SaleSnapshot closed;
        SaleState wasIn;
        string oldId;
        lock (_gate)
        {
            if (_sale.State is not (SaleState.Authorizing or SaleState.ReadyToPump)) return false;
            oldId = _sale.SaleId;
            wasIn = _sale.State;
            commands = _sale.Apply(new SaleEvent.Cancel(oldId, why));
            closed = _sale.Snapshot();
            ReplaceSale();
        }
        // Outside the lock, as the sale worker does: the reversal goes to the payment worker, the close goes to the ledger.
        Ledger.Append("sale.superseded", $"{oldId} {wasIn} hold {closed.Hold} cancelled: {why}");
        Log.Warn("sale", "{Old} ended by the station: {Why}; hold {Hold} being reversed", ("Old", oldId), ("Why", why), ("Hold", closed.Hold));
        foreach (var c in commands) Execute(c);
        RecordSaleBoundary(new SaleEvent.Cancel(oldId, why), wasIn, closed.State, closed);
        return true;
    }

    /// <summary>Every driver action at the pump goes down the link as a message, addressed to the current sale.</summary>
    private void ToPump(PumpMessage m) => _link.Post(m, CurrentSaleId);

    public void SelectGrade(Grade grade) => ToPump(new PumpMessage.SelectGrade(grade));
    public void LiftNozzle() => ToPump(new PumpMessage.Lift());
    public void SeatNozzle() => ToPump(new PumpMessage.Seat());
    public void UnseatNozzle() => ToPump(new PumpMessage.Unseat());
    public void SqueezeTrigger() => ToPump(new PumpMessage.Squeeze());
    public void ReleaseTrigger() => ToPump(new PumpMessage.Release());
    public void HangNozzle() => ToPump(new PumpMessage.Hang());

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
        // The operator's "new sale" never loses money in either direction. Fuel flowing: close the valve, and the
        // pump's next tick reports the stop, which settles the sale for the metered fuel. Money held with no fuel:
        // cancel and reverse. Otherwise the record is finished and a fresh one replaces it.
        var s = Snapshot();
        if (s.State is SaleState.Fueling)
        {
            Log.Warn("sale", "{SaleId} reset while fueling: valve closed, settling for the fuel delivered", ("SaleId", s.SaleId));
            _link.Post(new PumpMessage.Close(s.SaleId, "reset by the station"), s.SaleId);
            return;
        }
        if (s.State is SaleState.Completing) return;   // the bank is being told; nothing to reset yet
        if (!CancelOpenSaleAndReplace("reset by the station"))
            lock (_gate) ReplaceSale();
        ToPump(new PumpMessage.Stop("reset"));
        Publish();
    }

    /// <summary>Under _gate. Starts a fresh sale and drops the old sale's timer so it can't fire into the new one.</summary>
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
        ToPump(new PumpMessage.Arm(CurrentSaleId, token, Snapshot().Grade));
    }

    /// <summary>Take the last real token and raise its ceiling without re-signing. The HMAC should refuse it.</summary>
    public void AttackTamperCeiling(Money newCeiling)
    {
        var token = LastToken;
        if (token is null) { Log.Write("attack", "tamper: no token yet"); return; }
        var parsed = AuthorizationToken.Parse(token);
        var forged = (parsed with { CeilingCents = newCeiling.Cents }).Serialize();
        ToPump(new PumpMessage.Arm(CurrentSaleId, forged, Snapshot().Grade));
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
    public void AttackSpoofPulses(long pulses) => ToPump(new PumpMessage.SpoofPulses(pulses));

    /// <summary>Arm with no token at all. No code path accepts this.</summary>
    public void AttackArmWithoutToken() => ToPump(new PumpMessage.Arm(CurrentSaleId, "", Grade.Regular));

    // ----- Forecourt faults. Each is a message to the pump worker, as a sensor or the tank gauge would deliver it.

    /// <summary>The underground tank ran out, or the gauge shut the line. The pump's valve position means nothing until the supply is back.</summary>
    public void TankRunsDry()
    {
        Log.Warn("tank", "{Code} product below the fuel-out level, supply to the pump stops", ("Code", DiagnosticCodes.TankFuelOut.Code));
        Ledger.Append("tank.fuelout", DiagnosticCodes.TankFuelOut.Format("fuel out, supply stopped"));
        ToPump(new PumpMessage.Supply(false));
    }

    public void TankRefilled()
    {
        Log.Write("tank", "{Code} cleared, supply restored", ("Code", DiagnosticCodes.TankFuelOut.Code));
        Ledger.Append("tank.refilled", DiagnosticCodes.TankFuelOut.Format("cleared, supply restored"));
        ToPump(new PumpMessage.Supply(true));
    }

    /// <summary>The pulser stops counting while fuel keeps moving. The controller catches it by the flow switch.</summary>
    public void KillPulser() => ToPump(new PumpMessage.PulserDead(true));
    public void FixPulser() => ToPump(new PumpMessage.PulserDead(false));

    /// <summary>A drive-off: the breakaway coupling separates and both halves seal.</summary>
    public void DriveOff() => ToPump(new PumpMessage.DriveOff());

    /// <summary>The attendant reconnected the coupling.</summary>
    public void ResetBreakaway() => ToPump(new PumpMessage.ResetBreakaway());

    /// <summary>
    /// The station emergency stop. It models the power going away under the
    /// pump, the STPs, and every signal circuit at once, and sends no message
    /// to the pump. The site controller outside the classified area writes the
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
        // The final snapshot is the last journal write. After it, nothing may rewrite the journal or turn the station's own
        // power cut into a dispenser fault on the open sale. The link is about to drop because the station is dropping it.
        lock (_journalGate)
        {
            SaveJournal(stopReason: "ESTOP");
            _frozen = true;
        }
        // The forecourt goes dark: a dispenser in another process is told the power is off and ends. In-process it ends with us.
        _link.PowerOff();
        TearDown();
    }

    /// <summary>True from the E-stop's final snapshot on. The journal and the link reactions are done; only the teardown remains.</summary>
    private volatile bool _frozen;

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
        Capabilities.Publish(e);
    }

    /// <summary>
    /// Telemetry listeners that run beside the sale: a video that plays when
    /// fueling starts, a loyalty prompt, a forecourt display. Each one has its
    /// own worker and mailbox. Register one before the first sale.
    /// </summary>
    public CapabilityHost Capabilities { get; }

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
    /// is gone or on a new sale, so the machine can't carry it. A late approval
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
                _link.Post(new PumpMessage.Arm(ap.SaleId, ap.Token, ap.Grade), ap.SaleId);
                break;
            case SaleCommand.CloseValve cv:
                _link.Post(new PumpMessage.Close(cv.SaleId, "sale closed"), cv.SaleId);
                break;
            // Money owed to the bank is written to the compensation log here, on the deciding thread, before the
            // payment worker ever sees the message. A crash between this line and the bank's answer is recoverable.
            case SaleCommand.SendCompletion sc:
                Gateway.Intend(new PendingAdvice(sc.SaleId, PendingKind.Completion, sc.Final, sc.Lines));
                _paymentBox.Post(new PaymentMessage.Complete(sc.SaleId, sc.Final, sc.Lines));
                break;
            case SaleCommand.Reverse rv:
                Gateway.Intend(new PendingAdvice(rv.SaleId, PendingKind.Reversal, Money.Zero));
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

    // The pump worker's handler lives in Dispenser.Handle. The link posts its events into _saleBox.

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
                    var completion = await Gateway.CompleteAsync(cp.SaleId, cp.Final, cp.Lines, _cts.Token);
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
        return new SaleStateMachine(id, _options.Timeouts, _service);
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
    /// Simulates a power loss. Stops the workers with no close-out and no final
    /// snapshot, the same state a crash leaves behind. The next Simulation built
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
        _link.Dispose();
        _cts.Dispose();
        if (_ownsLog && Log is IDisposable d) d.Dispose();
        _custody?.Dispose();
    }
}
