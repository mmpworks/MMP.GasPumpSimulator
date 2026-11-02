using GasPump.Core;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Security;

namespace GasPump.Dispatch;

/// <summary>
/// One fuelling position as the station sees it: the sale on its screen, the
/// timers that sale armed, the link to its island, the commands waiting for an
/// answer, the island's deliveries still to settle, and the driver's hands.
/// A station has one of these per pump and one of everything else.
///
/// The point owns its sale worker's mailbox and the sale state machine. Only
/// its sale worker changes sale state. The payment worker belongs to the
/// station and is reached through <see cref="StationContext.PaymentBox"/>;
/// its answers come back as <see cref="SaleEvent"/>s posted into
/// <see cref="SaleBox"/> by the station, routed on the sale id's pump prefix.
/// </summary>
public sealed class FuellingPoint : IDisposable
{
    private readonly StationContext _ctx;
    private readonly IDispenserLink _link;
    private readonly Dispenser? _local;
    private readonly List<LogLine> _dispenserLog = [];
    private readonly List<LedgerEntry> _dispenserLedger = [];
    private string? _dispenserLedgerRun;
    private readonly Lock _gate = new();
    private SaleStateMachine _sale;
    /// <summary>The sale's armed timers by tag: the state's own timer, and side timers such as the idle timer while flow is paused.</summary>
    private readonly Dictionary<string, (IDisposable Timer, DateTimeOffset Deadline)> _saleTimers = new();
    /// <summary>Set by a hacked-EPS attack: the next authorization request is answered with this forged token instead of a bank call.</summary>
    private (Money Money, Volume Volume)? _forgeNextApproval;
    private int _saleCounter;

    /// <param name="link">The island, in this process or over a socket. The point owns it from here.</param>
    /// <param name="local">The in-process dispenser behind <paramref name="link"/>, when there is one, so the journal can restore its counters.</param>
    internal FuellingPoint(int number, string pumpId, StationContext ctx, IDispenserLink link, Dispenser? local)
    {
        Number = number;
        PumpId = pumpId;
        _ctx = ctx;
        _link = link;
        _local = local;
        Log = new TaggedLog(ctx.Log, ("Pump", number));
        _sale = NewSale();
        SaleBox = ctx.Dispatch.CreateMailbox<SaleEvent>($"sale-{number}", 1024);

        if (link is SocketDispenserLink socket)
        {
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
        }
        _link.Event += e => SaleBox.Post(e);
        _link.ViewChanged += v => { OnServiceView(v.Service); DispenserViewChanged?.Invoke(v); ctx.Capabilities.Publish(v); };
        _link.Answered += OnAnswered;
        _link.PeerAlive += OnPeerAlive;
        _link.TransactionsReported += OnTransactionsReported;
        _link.TotalsReported += OnTotalsReported;
        _link.LateEvent += OnLateEvent;
    }

    /// <summary>The pump number, 1-based. The wire node is 2.n and the sale ids are P{n}-S{k}.</summary>
    public int Number { get; }
    public string PumpId { get; }
    /// <summary>The station log with this pump's number on every line, so one log still says which pump wrote what.</summary>
    public IEventLog Log { get; }
    /// <summary>The sale worker's mailbox. The station starts the worker; the link and the payment worker post into it.</summary>
    internal IMailbox<SaleEvent> SaleBox { get; }

    /// <summary>The link to the pump: in-process mailbox or socket. Read-only facts about it for the host contract.</summary>
    public IDispenserLink Link => _link;

    /// <summary>The pump as the station sees it. Works on both sides of the seam.</summary>
    public DispenserView View => _link.View;

    /// <summary>True when the pump is in this process and <see cref="LocalPump"/> and friends can be touched directly.</summary>
    public bool DispenserIsLocal => _local is not null;

    public event Action<SaleSnapshot>? SaleChanged;
    public event Action<DispenserView>? DispenserViewChanged;
    /// <summary>Log lines from a dispenser in another process. In-process the shared log carries them with Origin and Pump properties.</summary>
    public event Action<LogLine>? DispenserLog;
    /// <summary>Ledger entries from a dispenser in another process. In-process the shared ledger carries them.</summary>
    public event Action<LedgerEntry>? DispenserLedger;
    /// <summary>The island answered a read of its totals.</summary>
    public event Action<PumpTotals>? TotalsRead;

    public IReadOnlyList<LogLine> DispenserLogSnapshot() { lock (_dispenserLog) return _dispenserLog.ToArray(); }
    public IReadOnlyList<LedgerEntry> DispenserLedgerSnapshot() { lock (_dispenserLedger) return _dispenserLedger.ToArray(); }

    /// <summary>The dispenser's chain, from the header on, then live, with the run id the lines belong to.</summary>
    public IDisposable SubscribeDispenserCustody(Action<string, string> onLine) => _link.SubscribeCustody(onLine);
    public long DispenserCustodyLines => _link.CustodyLines;

    // The pump's parts, reachable only while the dispenser is in this process. Over a socket they are another
    // process's memory, and View is the whole truth the station has.
    /// <summary>The in-process dispenser. Throws when the pump is in another process.</summary>
    public Dispenser LocalDispenser => _local ?? throw new InvalidOperationException($"pump {Number} is remote ({_link.Kind}); read View instead");
    public PumpController LocalPump => LocalDispenser.Pump;

    public SaleSnapshot Snapshot() { lock (_gate) return _sale.Snapshot(NextDeadline()); }
    public string CurrentSaleId { get { lock (_gate) return _sale.SaleId; } }
    public string? LastToken { get { lock (_gate) return _sale.Token; } }
    /// <summary>What this run did about the sale the previous run left open on this pump. Set once by the station after the journal is read.</summary>
    public RecoveryOutcome LastRecovery { get; internal set; } = RecoveryPlan.Build(null, 0, DateTimeOffset.MinValue, TimeSpan.Zero);

    /// <summary>True when nothing is queued for this pump's sale worker or its island.</summary>
    internal bool IsQuiet => SaleBox.Count == 0 && _link.Backlog == 0;
    internal string Counters => $"sale{Number}={SaleBox.Count} pump{Number}={_link.Backlog} link{Number}={_link.Kind}:{(_link.Connected ? "up" : "down")}";

    private EventLedger Ledger => _ctx.Ledger;

    // ----- The link

    /// <summary>
    /// The link came up or went down. Down mid-sale is a fault the station
    /// owns: close the sale for what was recorded, name the part, send the
    /// driver to the attendant. Up means a dispenser is listening; the first
    /// thing it hears is Stop, so a valve left open by a station restart shuts.
    /// </summary>
    private void OnDispenserConnection(bool up)
    {
        if (_ctx.IsFrozen()) return;   // the station cut this link itself; see Simulation.EmergencyStop
        var code = DiagnosticCodes.LinkDispenserLost;
        if (up)
        {
            Ledger.Append("link.up", $"{PumpId} dispenser {_link.RemoteRunId} pid {_link.RemotePid}");
            Command(new PumpMessage.Stop("station connected"), CurrentSaleId);
            // What the island still holds for us: deliveries whose money this station never settled.
            _link.RequestTransactions();
            // The controller owns the price board. Write it, so the island never bills by a stale one, then read the island's counters.
            Command(new PumpMessage.SetPrices(_ctx.Prices().TenthCentsPerGallon, "station"), CurrentSaleId);
            _link.RequestTotals();
            return;
        }
        var s = Snapshot();
        Ledger.Append("link.down", code.Format($"{PumpId} sale {s.SaleId} {s.State} {s.Volume} {s.Money}"));
        Log.Error("station", "{Code} dispenser link lost during {State}", ("Code", code.Code), ("State", s.State));
        if (s.State is SaleState.Authorizing or SaleState.ReadyToPump or SaleState.Fueling)
            SaleBox.Post(new SaleEvent.Fault(s.SaleId, "dispenser link lost", code.Code));
    }

    /// <summary>The heartbeat watchdog. The socket is open and the device stopped talking, which is a fault of its own.</summary>
    private void OnPeerAlive(bool alive)
    {
        if (_ctx.IsFrozen()) return;
        if (alive) { Ledger.Append("link.alive", $"{PumpId} dispenser heartbeats resumed"); return; }
        var s = Snapshot();
        Ledger.Append("link.silent", DiagnosticCodes.LinkDispenserSilent.Format($"{PumpId} sale {s.SaleId} {s.State} {s.Volume} {s.Money}"));
        Log.Error("station", "{Code} dispenser silent during {State}", ("Code", DiagnosticCodes.LinkDispenserSilent.Code), ("State", s.State));
        if (s.State is SaleState.Authorizing or SaleState.ReadyToPump or SaleState.Fueling)
            SaleBox.Post(new SaleEvent.Fault(s.SaleId, "dispenser silent", DiagnosticCodes.LinkDispenserSilent.Code));
    }

    /// <summary>The station cuts this pump's power. Over a socket the island ends; in-process nothing happens.</summary>
    internal void PowerOff() => _link.PowerOff();

    // ----- Commands and their answers

    private readonly Dictionary<string, (PumpMessage M, string SaleId, ITimer Timer)> _commands = new();
    private readonly HashSet<string> _earlyAnswers = [];

    /// <summary>
    /// Send a command the station needs an answer to, and start the clock on it.
    /// A command that is refused comes back as a <see cref="SaleEvent.PumpRefused"/>
    /// too, so here the answer is bookkeeping. An answer that never comes, or a
    /// Stale answer, is the dispenser failing to do what the sale needs, and the
    /// sale is faulted for it when it was waiting on this command.
    /// </summary>
    private void Command(PumpMessage m, string saleId)
    {
        var ttl = _ctx.Options.CommandDeadline;
        lock (_commands)
        {
            var corr = _link.Command(m, saleId, ttl);
            // The in-process link can answer before this line runs; that answer is waiting in _earlyAnswers.
            if (_earlyAnswers.Remove(corr)) return;
            var timer = _ctx.Clock.CreateTimer(_ => Unanswered(corr), null, ttl, Timeout.InfiniteTimeSpan);
            _commands[corr] = (m, saleId, timer);
        }
    }

    private void OnAnswered(WireAnswer a)
    {
        (PumpMessage M, string SaleId, ITimer Timer) cmd;
        lock (_commands)
        {
            if (!_commands.Remove(a.CorrelationId, out cmd)) { _earlyAnswers.Add(a.CorrelationId); return; }
        }
        cmd.Timer.Dispose();
        Ledger.Append("cmd.answer", $"{cmd.M.GetType().Name} {cmd.SaleId} {a.Code}{(a.Detail is { } d ? $" {d}" : "")}");
        if (a.Code == AnswerCode.Stale) FaultIfWaitingOn(cmd.M, cmd.SaleId, $"{cmd.M.GetType().Name} refused as stale by the dispenser");
        // A refused board means the pump bills by the old one. The screen shows the pump's prices, so the operator sees it; say why here.
        if (cmd.M is PumpMessage.SetPrices && a.Code != AnswerCode.Done) Log.Error("station", "price board {Code} by the dispenser: {Reason}", ("Code", a.Code), ("Reason", a.Detail));
    }

    private void Unanswered(string corr)
    {
        (PumpMessage M, string SaleId, ITimer Timer) cmd;
        lock (_commands) { if (!_commands.Remove(corr, out cmd)) return; }
        cmd.Timer.Dispose();
        Ledger.Append("cmd.unanswered", $"{cmd.M.GetType().Name} {cmd.SaleId} no answer in {_ctx.Options.CommandDeadline.TotalSeconds:0.#} s");
        Log.Error("station", "{Code} {Message} for {SaleId} unanswered after {Deadline}", ("Code", DiagnosticCodes.LinkDispenserSilent.Code), ("Message", cmd.M.GetType().Name), ("SaleId", cmd.SaleId), ("Deadline", _ctx.Options.CommandDeadline));
        FaultIfWaitingOn(cmd.M, cmd.SaleId, $"{cmd.M.GetType().Name} unanswered by the dispenser");
    }

    /// <summary>Arm and Close are the two commands a sale cannot go on without. Anything else failing is logged and the sale is left alone.</summary>
    private void FaultIfWaitingOn(PumpMessage m, string saleId, string reason)
    {
        if (m is not (PumpMessage.Arm or PumpMessage.Close)) return;
        var s = Snapshot();
        if (s.SaleId != saleId || s.State is not (SaleState.Authorizing or SaleState.ReadyToPump or SaleState.Fueling)) return;
        SaleBox.Post(new SaleEvent.Fault(saleId, reason, DiagnosticCodes.LinkDispenserSilent.Code));
    }

    /// <summary>
    /// A frame that filled a gap after the station had moved on. It never
    /// touches the sale, which has already decided without it. The one thing
    /// still owed is the island's transaction, if the frame names one.
    /// </summary>
    private void OnLateEvent(SaleEvent e)
    {
        Ledger.Append("link.late", $"{e.GetType().Name} for {e.SaleId} arrived out of order, recorded only");
        var tx = e switch
        {
            SaleEvent.NozzleHung h => h.TxSeq,
            SaleEvent.CeilingReached c => c.TxSeq,
            SaleEvent.Fault f => f.TxSeq,
            _ => null,
        };
        if (tx is { } t) ClearOrphanTransaction(e.SaleId, t, "named by a late frame");
    }

    // ----- The island's transactions, reconciled against what this station knows

    private RecoveryOutcome? _awaitingCloseout;
    private ITimer? _closeoutTimer;

    /// <summary>
    /// The dispenser told us which deliveries it still holds. Each one is either
    /// the sale this station restarted in the middle of (settle from the
    /// dispenser's numbers, which are the true ones), the sale in progress now
    /// (the normal flow clears it), or an orphan this station has no record of
    /// (put the money on the ledger for the attendant and release the pump).
    /// </summary>
    private void OnTransactionsReported(IReadOnlyList<PumpTransaction> reported)
    {
        var open = reported.Where(t => t.State != TransactionState.Cleared).ToList();
        if (open.Count == 0) return;
        Ledger.Append("tx.reported", $"{PumpId} " + string.Join("; ", open.Select(t => $"#{t.TxSeq} {t.SaleId} {t.Volume} {t.Money} {t.State}")));
        foreach (var tx in open)
        {
            RecoveryOutcome? awaiting;
            string liveSale;
            SaleState liveState;
            long? liveTx;
            lock (_gate)
            {
                awaiting = _awaitingCloseout;
                liveSale = _sale.SaleId;
                liveState = _sale.State;
                liveTx = _sale.TxSeq;
                if (awaiting?.SaleId == tx.SaleId) _awaitingCloseout = null;
            }
            if (awaiting?.SaleId == tx.SaleId)
            {
                _closeoutTimer?.Dispose();
                Ledger.Append("recovery.reconciled", $"{tx.SaleId} #{tx.TxSeq} dispenser delivered {tx.Volume} {tx.Money}; journal had {awaiting.Volume} {awaiting.Money}");
                Log.Warn("recovery", "settling {SaleId} from the dispenser's transaction: {Volume} {Money} (journal had {JournalVolume} {JournalMoney})", ("SaleId", tx.SaleId), ("Volume", tx.Volume), ("Money", tx.Money), ("JournalVolume", awaiting.Volume), ("JournalMoney", awaiting.Money), ("DeliveryConfidence", "dispenser"));
                _ctx.PaymentBox.Post(new PaymentMessage.Complete(tx.SaleId, tx.Money, tx.Lines));
                ClearOrphanTransaction(tx.SaleId, tx.TxSeq, "settled from the dispenser's numbers after a station restart");
                continue;
            }
            if (tx.SaleId == liveSale)
            {
                // The sale in progress clears its own transaction when the money lands.
                if (liveTx == tx.TxSeq || liveState is SaleState.Fueling or SaleState.Completing) continue;
                // This station already finished that sale, so the clear was lost on the way. Send it again.
                ClearOrphanTransaction(tx.SaleId, tx.TxSeq, $"sale already {liveState} on this station, clear sent again");
                continue;
            }
            if (_ctx.Gateway.Pending.Any(p => p.SaleId == tx.SaleId))
            {
                ClearOrphanTransaction(tx.SaleId, tx.TxSeq, "money for it is queued on this station");
                continue;
            }
            Ledger.Append("tx.orphan", $"{PumpId} #{tx.TxSeq} {tx.SaleId} {tx.Grade} {tx.Volume} {tx.Money} ended {tx.EndedAt:HH:mm:ss} {tx.EndReason}: no station record, attendant to settle");
            Log.Error("recovery", "ORPHAN transaction #{TxSeq} for {SaleId}: {Volume} {Money} delivered, no record of the sale on this station", ("TxSeq", tx.TxSeq), ("SaleId", tx.SaleId), ("Volume", tx.Volume), ("Money", tx.Money));
            ClearOrphanTransaction(tx.SaleId, tx.TxSeq, "orphan, money on the ledger for the attendant");
        }
    }

    private void ClearOrphanTransaction(string saleId, long txSeq, string why)
    {
        Ledger.Append("tx.clear", $"{saleId} #{txSeq} {why}");
        Log.Write("sale", "clearing transaction #{TxSeq} for earlier sale {SaleId}: {Why}", ("TxSeq", txSeq), ("SaleId", saleId), ("Why", why));
        Command(new PumpMessage.Lock(txSeq, "station"), saleId);
        Command(new PumpMessage.Clear(txSeq, "station"), saleId);
    }

    /// <summary>The grace period ran out with no word from the dispenser. Close out from what the journal recorded, as before.</summary>
    private void CloseOutFromJournal()
    {
        RecoveryOutcome? r;
        lock (_gate) { r = _awaitingCloseout; _awaitingCloseout = null; }
        if (r is null) return;
        Ledger.Append("recovery.closeout.journal", $"{r.SaleId} no transaction from the dispenser in {_ctx.Options.CloseoutGrace.TotalSeconds:0.#} s, settling {r.Money} from the journal");
        _ctx.PaymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money, r.Lines));
    }

    // ----- The island's totals and the price board

    private PumpTotals? _lastTotals;

    /// <summary>The island's counters as last read. Null until the first read answers.</summary>
    public PumpTotals? LastTotals { get { lock (_gate) return _lastTotals; } }

    /// <summary>Ask the island for its sealed counter and per-grade totals. The answer lands in <see cref="LastTotals"/> and the ledger.</summary>
    public void ReadTotals() => _link.RequestTotals();

    private void OnTotalsReported(PumpTotals t)
    {
        lock (_gate) _lastTotals = t;
        var byGrade = t.ByGrade.Count == 0 ? "no deliveries" : string.Join("; ", t.ByGrade.Select(g => $"{g.Grade} {g.Volume} {g.Money} in {g.Deliveries}"));
        Ledger.Append("totals.read", $"{PumpId} totalizer {t.Totalizer}; {byGrade}; last tx #{t.LastTxSeq}");
        Log.Write("station", "island totals: {Totalizer}, {ByGrade}, last tx #{LastTxSeq}", ("Totalizer", t.Totalizer), ("ByGrade", byGrade), ("LastTxSeq", t.LastTxSeq));
        TotalsRead?.Invoke(t);
    }

    /// <summary>The station changed its posted board. Write the whole board to this island; the pump answers.</summary>
    internal void WritePrices(Pricing board, string by) => Command(new PumpMessage.SetPrices(board.TenthCentsPerGallon, by), CurrentSaleId);

    // ----- Service status

    private readonly Lock _serviceGate = new();
    /// <summary>What the station last heard about the pump; every new sale starts from it. Written under _serviceGate, read under _gate.</summary>
    private volatile ServiceStatus _service = ServiceStatus.Normal;

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
            if (_ctx.IsFrozen() || service == _service) return;
            _service = service;
            string saleId;
            lock (_gate) saleId = _sale.SaleId;
            Ledger.Append("service.status", $"{PumpId} pump {service}");
            Log.Write("station", "pump {Service}", ("Service", service.ToString()), ("Codes", string.Join(",", service.Codes)));
            SaleBox.Post(new SaleEvent.PumpService(saleId, service));
        }
    }

    /// <summary>Read the island's current status once, before the first sale. A dispenser not yet answering is out of service from the start.</summary>
    internal void SyncServiceFromView() => OnServiceView(_link.View.Service);

    // ----- The journal

    internal JournalPoint CaptureJournal()
    {
        SaleSnapshot s;
        int counter;
        lock (_gate) { s = _sale.Snapshot(NextDeadline()); counter = _saleCounter; }
        var view = View;
        return new JournalPoint(Number, PumpId, counter,
            new JournalSale(s.SaleId, s.State.ToString(), s.Tender.ToString(), s.Grade.ToString(), s.GradeSelected, s.CardName,
                s.Ceiling.Cents, s.Volume.MilliGallons, s.Money.Cents, s.Flow.MilliGallonsPerSecond, s.Hold.ToString(), s.CashPrepaid.Cents,
                JournalLine.From(s.Lines)),
            view.Phase, view.SelectedGrade?.ToString(),
            view.TotalizerMilliGallons, _local?.Nonces.Export().ToList() ?? [],
            _local?.ConditionCodes.ToList());
    }

    /// <summary>
    /// Bring back what the journal held for this pump. An in-process dispenser
    /// gets its counter, nonces, broken parts, and the station's board; a
    /// dispenser in its own process keeps those itself. The sale counter
    /// carries over so the next sale is numbered after the last one.
    /// </summary>
    internal void Restore(JournalPoint p, Pricing prices)
    {
        if (_local is { } d)
        {
            d.Totalizer.Restore(new Volume(p.TotalizerMilliGallons));
            d.Nonces.Import(p.Nonces);
            if (p.DispenserConditions is { Count: > 0 } c) d.RestoreConditions(c);
            d.Pump.RestorePrices(prices);
        }
        lock (_gate)
        {
            _saleCounter = p.SaleCounter;
            // The placeholder sale was numbered before the restore. The first real sale comes after it.
            _sale = NewSale();
        }
    }

    /// <summary>
    /// Close out what the last run left open on this pump. The pump is already
    /// disarmed (a new process has no armed authorization), so the only work is
    /// money, and the only volume to report is what was recorded.
    /// </summary>
    internal void ApplyRecovery(RecoveryOutcome r)
    {
        switch (r.Kind)
        {
            case RecoveryKind.ClosedOut:
                Ledger.Append("recovery.closeout", $"{r.SaleId} {r.Tender} {r.Grade} {r.Volume} {r.Money} DELIVERY LIKELY UNDER-RECORDED by up to {r.UnrecordedUpTo}");
                Log.Error("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Tender", r.Tender), ("Grade", r.Grade), ("Volume", r.Volume), ("Money", r.Money), ("UnrecordedUpTo", r.UnrecordedUpTo), ("DeliveryConfidence", "under-recorded"));
                if (_local is null)
                {
                    // The dispenser in the other process still holds the true numbers. Ask, and settle from those when they come.
                    lock (_gate) _awaitingCloseout = r;
                    _closeoutTimer = _ctx.Clock.CreateTimer(_ => CloseOutFromJournal(), null, _ctx.Options.CloseoutGrace, Timeout.InfiniteTimeSpan);
                    Ledger.Append("recovery.await", $"{r.SaleId} waiting up to {_ctx.Options.CloseoutGrace.TotalSeconds:0.#} s for the dispenser's transaction");
                    if (_link.Connected) _link.RequestTransactions();
                    break;
                }
                _ctx.PaymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money, r.Lines));
                break;
            case RecoveryKind.Reversed:
                Ledger.Append("recovery.reversed", $"{r.SaleId} {r.Tender} no fuel recorded, hold or cash returned");
                Log.Warn("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Tender", r.Tender));
                _ctx.PaymentBox.Post(new PaymentMessage.Reverse(r.SaleId!));
                break;
            case RecoveryKind.CompletionResent:
                Ledger.Append("recovery.completion", $"{r.SaleId} {r.Tender} {r.Volume} {r.Money} completion sent again");
                Log.Warn("recovery", "{Note}", ("Note", r.Note), ("SaleId", r.SaleId), ("Money", r.Money));
                _ctx.PaymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money, r.Lines));
                break;
            default:
                Log.Write("recovery", "{Note}", ("Note", r.Note));
                break;
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
        SaleBox.Post(new SaleEvent.CardPresented(id, cardName, grade, tender, source));
        return (true, id, "");
    }

    /// <summary>
    /// The keypad. A debit card wants its PIN, a credit card wants the billing
    /// ZIP code. The digits go to the sale worker and from there to the bank
    /// on the authorization request. They are never logged.
    /// </summary>
    public void EnterCode(string code) => SaleBox.Post(new SaleEvent.CodeEntered(CurrentSaleId, code));

    /// <summary>The driver paid the cashier. The amount is the ceiling, to the cent. No bank is called.</summary>
    public string PayCash(Money amount, Grade? grade = null)
    {
        var (ok, id, reason) = BeginSale($"cash {amount} at the station");
        if (!ok) { Log.Warn("sale", "cash sale refused: {Reason}", ("Reason", reason)); return id; }
        SaleBox.Post(new SaleEvent.CashPaid(id, amount, grade));
        return id;
    }

    /// <summary>The cashier handed over the change or refund for the current sale.</summary>
    public void CollectChange() => _ctx.Gateway.Drawer.ChangeCollected(CurrentSaleId);

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
                return (false, oldId, $"pump in use by {oldId} ({_sale.State}, nozzle {(_sale.NozzleOut ? "out" : "in boot")})");
            // A sale that ended with fuel delivered and its completion still in flight: the island holds that transaction until
            // the money lands, so a new card would only be refused by the pump. Say so here instead, before the bank is asked.
            if (SaleStateMachine.IsTerminal(_sale.State) && _sale.Volume > Volume.Zero && _sale.Hold is HoldStatus.Requested or HoldStatus.Held)
                return (false, oldId, $"pump in use by {oldId} ({_sale.State}, settling {_sale.Money})");
            if (!_sale.IsOpenAndUntouched)
            {
                // A record that has already carried money or an ending is finished, even when its screen has cleared to Idle.
                // The next driver gets a fresh id, so a late answer for the old sale (an unanswered Close, a bank reply) can
                // never land on the new one, and the bank never sees two sales under one id.
                if (_sale.State is not SaleState.Idle || _sale.End != EndReason.None || _sale.Hold != HoldStatus.None) ReplaceSale();
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
            if (_sale.State is not (SaleState.CardRead or SaleState.Authorizing or SaleState.ReadyToPump)) return false;
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
    public void InsertSpout() => ToPump(new PumpMessage.InsertSpout());
    public void WithdrawSpout() => ToPump(new PumpMessage.WithdrawSpout());
    public void SqueezeLever() => ToPump(new PumpMessage.Squeeze());
    public void ReleaseLever() => ToPump(new PumpMessage.Release());
    public void HangNozzle() => ToPump(new PumpMessage.Hang());

    /// <summary>The whole hand sequence a driver does at the pump: pick a grade, lift, insert, squeeze. For tests and scripted scenarios.</summary>
    public void StartFueling(Grade grade = Grade.Regular)
    {
        SelectGrade(grade);
        LiftNozzle();
        InsertSpout();
        SqueezeLever();
    }

    public void Cancel(string reason = "driver cancelled") => SaleBox.Post(new SaleEvent.Cancel(CurrentSaleId, reason));

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

    /// <summary>Under _gate. The soonest armed deadline, which is what the screen counts down to. Null when no timer is armed.</summary>
    private DateTimeOffset? NextDeadline() => _saleTimers.Count == 0 ? null : _saleTimers.Values.Min(t => t.Deadline);

    /// <summary>Under _gate. Stops one timer by tag, or all of them.</summary>
    private void DropTimers(string? tag)
    {
        if (tag is null)
        {
            foreach (var t in _saleTimers.Values) t.Timer.Dispose();
            _saleTimers.Clear();
        }
        else if (_saleTimers.Remove(tag, out var one)) one.Timer.Dispose();
    }

    /// <summary>Under _gate. Starts a fresh sale and drops the old sale's timers so none can fire into the new one.</summary>
    private void ReplaceSale()
    {
        DropTimers(null);
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
        lock (_gate) { ReplaceSale(); id = _sale.SaleId; _forgeNextApproval = (money, volume); }
        // The driver's side looks normal: card in, ZIP typed. The hacked EPS then answers the request itself and skips the bank.
        SaleBox.Post(new SaleEvent.CardPresented(id, "Good", Grade.Regular));
        SaleBox.Post(new SaleEvent.CodeEntered(id, TestCards.Zip));
        return id;
    }

    /// <summary>A pulser reporting fuel that did not move. The controller should stop on the first bad tick.</summary>
    public void AttackSpoofPulses(long pulses) => ToPump(new PumpMessage.SpoofPulses(pulses));

    /// <summary>Arm with any token at all: empty, forged, or one signed for another pump. The authority decides.</summary>
    public void AttackArm(string token) => ToPump(new PumpMessage.Arm(CurrentSaleId, token, Grade.Regular));

    /// <summary>Arm with no token at all. No code path accepts this.</summary>
    public void AttackArmWithoutToken() => AttackArm("");

    // ----- Forecourt faults. Each is a message to the pump worker, as a sensor or the tank gauge would deliver it.

    /// <summary>The tank gauge's word reaches this pump's supply line. The site raises the alarm once (<see cref="Simulation.TankRunsDry"/>) and tells every pump.</summary>
    internal void SetSupply(bool available) => ToPump(new PumpMessage.Supply(available));

    /// <summary>The pulser stops counting while fuel keeps moving. The controller catches it by the flow switch.</summary>
    public void KillPulser() => ToPump(new PumpMessage.PulserDead(true));
    public void FixPulser() => ToPump(new PumpMessage.PulserDead(false));

    /// <summary>A drive-off: the breakaway coupling separates and both halves seal.</summary>
    public void DriveOff() => ToPump(new PumpMessage.DriveOff());

    /// <summary>The attendant reconnected the coupling.</summary>
    public void ResetBreakaway() => ToPump(new PumpMessage.ResetBreakaway());

    /// <summary>The operator takes this pump out of sales. It keeps working, refuses every card, and finishes any delivery in progress first.</summary>
    public void ClosePump(string by = "operator") => Command(new PumpMessage.CloseForSales(by), CurrentSaleId);
    /// <summary>The operator puts the pump back into sales.</summary>
    public void OpenPump(string by = "operator") => Command(new PumpMessage.OpenForSales(by), CurrentSaleId);

    // ----- The sale worker. One message at a time, on this pump's own worker.

    internal void HandleSaleEvent(SaleEvent e)
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
        _ctx.Capabilities.Publish(e);
    }

    /// <summary>
    /// The session between the driver and the pump, as ledger entries: one at
    /// the open, one at the close. With the pump's own entries between them,
    /// the custody file tells the whole sale without the debug log.
    /// </summary>
    private void RecordSaleBoundary(SaleEvent e, SaleState before, SaleState after, SaleSnapshot s)
    {
        if (before == SaleState.Idle && after is SaleState.CardRead or SaleState.Authorizing)
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
                _ctx.PaymentBox.Post(new PaymentMessage.Reverse(a.SaleId));
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
            // The dispenser's end-of-delivery report for a sale this station already finished. The money was dealt with
            // when the sale ended (or there was none to deal with), so the island's transaction can be cleared now.
            case SaleEvent.NozzleHung { TxSeq: { } tx1 }:
                ClearOrphanTransaction(e.SaleId, tx1, "hang reported after the sale ended");
                break;
            case SaleEvent.CeilingReached { TxSeq: { } tx2 }:
                ClearOrphanTransaction(e.SaleId, tx2, "ceiling reported after the sale ended");
                break;
            case SaleEvent.Fault { TxSeq: { } tx3 }:
                ClearOrphanTransaction(e.SaleId, tx3, "fault reported after the sale ended");
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
                (Money, Volume)? forge;
                lock (_gate) { forge = _forgeNextApproval; _forgeNextApproval = null; }
                if (forge is { } f)
                {
                    // A compromised EPS answers the pump itself. The bank is never called; the token is signed with the real key.
                    var token = _ctx.Signer.Issue(ra.SaleId, PumpId, f.Item1, f.Item2, _ctx.Clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();
                    Log.Warn("attack", "forged approval for {Money} and {Volume} pushed to the sale, bank skipped", ("Money", f.Item1), ("Volume", f.Item2));
                    SaleBox.Post(new SaleEvent.Approved(ra.SaleId, f.Item1, token));
                    break;
                }
                var card = TestCards.ByName.TryGetValue(ra.CardName, out var known) ? known : new CardData("0000000000000000", 1, 2000, CardKind.Credit);
                _ctx.PaymentBox.Post(new PaymentMessage.PreAuthorize(ra.SaleId, PumpId, card, ra.Tender, ra.Code));
                break;
            case SaleCommand.RequestCashAuthorization rc:
                _ctx.PaymentBox.Post(new PaymentMessage.CashAuthorize(rc.SaleId, PumpId, rc.Amount));
                break;
            case SaleCommand.ArmPump ap:
                Command(new PumpMessage.Arm(ap.SaleId, ap.Token, ap.Grade), ap.SaleId);
                break;
            case SaleCommand.CloseValve cv:
                Command(new PumpMessage.Close(cv.SaleId, "sale closed"), cv.SaleId);
                break;
            // Money owed to the bank is written to the compensation log here, on the deciding thread, before the
            // payment worker ever sees the message. A crash between this line and the bank's answer is recoverable.
            case SaleCommand.SendCompletion sc:
                _ctx.Gateway.Intend(new PendingAdvice(sc.SaleId, PendingKind.Completion, sc.Final, sc.Lines));
                _ctx.PaymentBox.Post(new PaymentMessage.Complete(sc.SaleId, sc.Final, sc.Lines));
                break;
            case SaleCommand.Reverse rv:
                _ctx.Gateway.Intend(new PendingAdvice(rv.SaleId, PendingKind.Reversal, Money.Zero));
                _ctx.PaymentBox.Post(new PaymentMessage.Reverse(rv.SaleId));
                break;
            // The money is settled or safely queued. Claim the island's transaction and clear it, so the pump can arm again.
            case SaleCommand.ClearTransaction ct:
                Ledger.Append("tx.clear", $"{ct.SaleId} #{ct.TxSeq}");
                Command(new PumpMessage.Lock(ct.TxSeq, "station"), ct.SaleId);
                Command(new PumpMessage.Clear(ct.TxSeq, "station"), ct.SaleId);
                break;
            case SaleCommand.ArmTimer at:
                lock (_gate)
                {
                    DropTimers(at.Tag);
                    var timer = _ctx.Dispatch.StartTimer($"sale{Number}-timeout-{at.Tag}", at.After, SaleBox, () => new SaleEvent.Timeout(at.SaleId, at.State, at.Tag));
                    _saleTimers[at.Tag] = (timer, _ctx.Clock.GetUtcNow() + at.After);
                }
                break;
            case SaleCommand.CancelTimer ct:
                lock (_gate) DropTimers(ct.Tag);
                break;
            case SaleCommand.Log lg:
                Log.Write("sale", lg.Message);
                break;
        }
    }

    private SaleStateMachine NewSale()
    {
        var id = SaleIds.For(Number, Interlocked.Increment(ref _saleCounter));
        return new SaleStateMachine(id, _ctx.Options.Timeouts, _service);
    }

    private void Publish()
    {
        SaleSnapshot snap;
        lock (_gate) snap = _sale.Snapshot(NextDeadline());
        SaleChanged?.Invoke(snap);
        _ctx.SaveJournal();
    }

    /// <summary>Stops every timer this pump armed: the sale's, the command deadlines, the close-out wait. Called before the workers stop, so none fires into a station going dark.</summary>
    internal void StopTimers()
    {
        _closeoutTimer?.Dispose();
        lock (_gate) DropTimers(null);
        lock (_commands) { foreach (var c in _commands.Values) c.Timer.Dispose(); _commands.Clear(); }
    }

    /// <summary>Stops the timers and the link. The dispatch strategy owns the worker and disposes it with the station.</summary>
    public void Dispose()
    {
        StopTimers();
        _link.Dispose();
    }
}
