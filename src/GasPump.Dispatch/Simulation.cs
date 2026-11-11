using GasPump.Bank;
using GasPump.Core;
using GasPump.Custody;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Reader;
using GasPump.Security;
using MMP.Herald.Events;

namespace GasPump.Dispatch;

public sealed record SimulationOptions(
    /// <summary>The first pump's id. Pump n is named by <see cref="PumpIdFor"/>; PUMP-01 for one that is numbered.</summary>
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
    /// <summary>Where the first pump's dispenser is. Null builds every pump in this process behind a mailbox. A host and port reach a <see cref="DispenserServer"/> over TCP; pump n is on the port plus n minus 1.</summary>
    DispenserLinkOptions? Dispenser = null,
    /// <summary>How long a command to the pump may go unanswered before the station treats the dispenser as silent. Also the command's time to live on the wire.</summary>
    TimeSpan? CommandTimeout = null,
    /// <summary>After a restart over a socket, how long to wait for the dispenser's own transaction before closing out from the journal's numbers.</summary>
    TimeSpan? CloseoutWait = null,
    /// <summary>How many fuelling points the station drives. Each is its own pump: its own sale, its own link, its own dispenser. The library's default is one; the host asks for two (GASPUMP_PUMPS).</summary>
    int PumpCount = 1)
{
    public TimeSpan CommandDeadline => CommandTimeout ?? TimeSpan.FromSeconds(3);
    /// <summary>10 s: after an E-stop reset every island restarts at once, and the slower one must still be heard before the journal's number is used.</summary>
    public TimeSpan CloseoutGrace => CloseoutWait ?? TimeSpan.FromSeconds(10);
    public static SimulationOptions Default => new(
        PumpId: "PUMP-01",
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

    /// <summary>
    /// The id of pump <paramref name="number"/>. When <see cref="PumpId"/> ends in digits (PUMP-01), the number
    /// replaces them, so a one-pump station named PUMP-07 keeps its name and a two-pump one is PUMP-01, PUMP-02.
    /// </summary>
    public string PumpIdFor(int number)
    {
        if (PumpCount == 1) return PumpId;
        return $"{PumpId.TrimEnd("0123456789".ToCharArray())}{number:00}";
    }

    /// <summary>Where pump <paramref name="number"/>'s dispenser listens: the first port, then one up per pump.</summary>
    public DispenserLinkOptions? DispenserFor(int number) => Dispenser is { } d ? d with { Port = d.Port + number - 1 } : null;
}

/// <summary>
/// The station, the forecourt controller: one payment worker, one bank, one
/// journal, one ledger, one custody chain, one admin panel, one price board,
/// one E-stop, and a <see cref="FuellingPoint"/> per pump.
///
///   fuelling point  owns one sale and its SaleStateMachine, one link to one island, one sale worker.
///   payment worker  owns the PaymentGateway. The only thing that talks to the host. Serves every point.
///   dispenser link  carries PumpMessages to a pump worker and SaleEvents back, one per point.
///
/// Every arrow between workers is a mailbox. The dispatch strategy decides how
/// the workers run. The code in this file doesn't depend on the strategy, or
/// on which side of a process boundary the pumps are on. The payment worker's
/// answers find their point by the pump prefix on the sale id.
/// </summary>
public sealed class Simulation : IDisposable
{
    private readonly IDispatchStrategy _dispatch;
    private readonly SimulationOptions _options;
    private readonly TimeProvider _clock;
    private readonly IMailbox<PaymentMessage> _paymentBox;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly NetworkSimulator _networkSim;
    private readonly FuellingPoint[] _points;
    private int _inflightPayments;

    /// <param name="log">A log to use instead of the default Herald pipeline. Leave null in the host; tests may pass a plain EventLog. When null, the simulation builds a Herald.OSS pipeline with only the bridge back to itself, and the host adds file and console sinks.</param>
    public Simulation(IDispatchStrategy dispatch, TimeProvider clock, SimulationOptions? options = null, IssuerSimulator? issuer = null, byte[]? hmacKey = null, IEventLog? log = null)
    {
        _dispatch = dispatch;
        _clock = clock;
        _options = options ?? SimulationOptions.Default;
        if (_options.PumpCount < 1) throw new ArgumentOutOfRangeException(nameof(options), "a station drives at least one pump");
        _prices = _options.Pricing;
        _ownsLog = log is null;
        // Two files with two jobs. The Herald debug log takes everything. The TesseraSeal custody file takes the
        // evidence: what the pumps did, what the sales did, where the money went, and who touched the admin panel.
        if (_options.Journal is { } j)
        {
            Directory.CreateDirectory(j.Directory);
            RunId = j.RunId ?? $"RUN-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            _custody = CustodyFile.Open(j.CustodyPath(RunId), j.TenantId, RunId, j.Ikm);
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
        Gateway = new PaymentGateway(Issuer, _networkSim, Signer, clock, _options.Gateway, Site, Drawer);
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

        _paymentBox = dispatch.CreateMailbox<PaymentMessage>("payment", 64);

        // The journal is read here, before any link exists: a socket link connects at once and writes the price board on
        // connect, so the board must be the journaled one by then or a restart would push the built-in prices onto the island.
        var snapshot = _options.Journal is { } jo ? StateJournal.Load(jo.StatePath, w => Log.Warn("journal", "{Message}", ("Message", w))) : null;
        // The in-process bank keeps its own books, card numbers and all, in its own file beside the journal.
        if (_options.Journal is { } jb && BankBooks.Load(jb.BankPath) is { } books) { Issuer.Import(books); _bankBooksLoaded = true; }
        if (snapshot?.Prices is { Count: > 0 } journaled && new Pricing(journaled) is { } board && board.Validate().Ok) _prices = board;

        // Capabilities ride beside the sales on their own workers. A capability that throws is switched off and logged, and the sales are unaffected.
        Capabilities = new CapabilityHost(dispatch, Log);
        var context = new StationContext(dispatch, clock, _options, Log, Ledger, Gateway, Signer, _paymentBox, Capabilities, () => Prices, () => SaveJournal(), () => _frozen);

        // One fuelling point per pump. The dispenser is here, behind a mailbox on the same strategy, or in another process behind a socket.
        _points = new FuellingPoint[_options.PumpCount];
        for (var n = 1; n <= _options.PumpCount; n++)
        {
            var pumpId = _options.PumpIdFor(n);
            FuellingPoint point;
            if (_options.DispenserFor(n) is { } where)
            {
                point = new FuellingPoint(n, pumpId, context, new SocketDispenserLink(where, pumpId, new TaggedLog(Log, ("Pump", n))), null, InjectReader(n));
            }
            else
            {
                var local = new Dispenser(DispenserOptions.From(_options) with { PumpId = pumpId }, key, Ledger, new TaggedLog(Log, ("Origin", Origins.Dispenser), ("Pump", n)), clock);
                // The link's ticker starts in its constructor, before the point exists, so the sale id is read late and is "-" until then.
                FuellingPoint? owner = null;
                var link = new LocalDispenserLink(local, dispatch, _options.TickPeriod, () => owner?.CurrentSaleId ?? "-", Custody, RunId, CustodyPath, n);
                point = owner = new FuellingPoint(n, pumpId, context, link, local, InjectReader(n));
            }
            point.SaleChanged += s => SaleChanged?.Invoke(s);
            _points[n - 1] = point;
        }

        // Restore the rest of the journal before any worker runs, then decide what to do about the sales that were open.
        if (snapshot is not null) Restore(snapshot);
        var period = _options.Journal?.Period ?? TimeSpan.FromSeconds(1);
        foreach (var p in _points) p.LastRecovery = RecoveryPlan.Build(snapshot, p.Number, clock.GetUtcNow(), period);
        // A dispenser not yet answering, or one that started with a part already broken, is out of service from the first sale.
        foreach (var p in _points) p.SyncServiceFromView();

        foreach (var p in _points) dispatch.StartWorker($"sale-{p.Number}", p.SaleBox, p.HandleSaleEvent);
        dispatch.StartWorker("payment", _paymentBox, HandlePaymentMessage);

        Log.Write("sim", "started with {Dispatch} dispatch, {PumpCount} pump(s) {Pumps} via {Link}, hold {Hold}, run {RunId}",
            ("Dispatch", dispatch.Name), ("PumpCount", _points.Length), ("Pumps", string.Join(",", _points.Select(p => p.PumpId))), ("Link", _points[0].Link.Kind), ("Hold", _options.HoldAmount), ("RunId", RunId));

        ApplyRecovery(snapshot);

        if (_options.Journal is { } jp)
        {
            _journalTimer = clock.CreateTimer(_ => SaveJournal(), null, jp.Period, jp.Period);
            SaveJournal();
        }
    }

    private readonly ICustodySink? _custody;
    private ITimer? _journalTimer;
    private readonly Lock _journalGate = new();

    /// <summary>Which categories are evidence for the custody file. The rest is debugging and stays in the Herald file.</summary>
    private static bool IsEvidence(LogEvent e) => e.Category.Value is "ledger" or "sale" or "payment" or "admin" or "recovery" or "attack" or "station" or "tank";

    /// <summary>
    /// The station's own custody file, as a stream. Every complete line the
    /// TesseraSeal sink writes is published here byte for byte. In-process the
    /// dispensers write into this same chain; over a socket each has its own,
    /// reached through its point. Null when the journal is off.
    /// </summary>
    public CustodyStream? Custody { get; private set; }

    // ----- The fuelling points

    /// <summary>Every pump the station drives, in pump order.</summary>
    public IReadOnlyList<FuellingPoint> Points => _points;

    /// <summary>Pump <paramref name="number"/>, 1-based.</summary>
    public FuellingPoint Point(int number) =>
        number >= 1 && number <= _points.Length ? _points[number - 1] : throw new ArgumentOutOfRangeException(nameof(number), $"this station has pumps 1 to {_points.Length}");

    /// <summary>The point a sale id belongs to, by its pump prefix. Null for an id in another form.</summary>
    public FuellingPoint? PointOf(string saleId) => SaleIds.PumpOf(saleId) is { } n && n >= 1 && n <= _points.Length ? _points[n - 1] : null;

    /// <summary>
    /// Pump 1. The station began with one pump, and the tests, scripts, and
    /// hosts written then address it through these forwards. New code names
    /// its pump with <see cref="Point"/>.
    /// </summary>
    public FuellingPoint First => _points[0];

    public IDispenserLink Link => First.Link;
    public DispenserView View => First.View;
    public bool DispenserIsLocal => First.DispenserIsLocal;
    public IReadOnlyList<LogLine> DispenserLogSnapshot() => First.DispenserLogSnapshot();
    public IReadOnlyList<LedgerEntry> DispenserLedgerSnapshot() => First.DispenserLedgerSnapshot();
    public IDisposable SubscribeDispenserCustody(Action<string, string> onLine) => First.SubscribeDispenserCustody(onLine);
    public long DispenserCustodyLines => First.DispenserCustodyLines;
    public RecoveryOutcome LastRecovery => First.LastRecovery;
    public PumpTotals? LastTotals => First.LastTotals;
    public void ReadTotals() => First.ReadTotals();
    public PumpAuthority Authority => First.LocalDispenser.Authority;
    public PumpController Pump => First.LocalPump;
    public Meter Meter => First.LocalDispenser.Meter;
    public NonceStore Nonces => First.LocalDispenser.Nonces;
    public Totalizer Totalizer => First.LocalDispenser.Totalizer;
    public SaleSnapshot Snapshot() => First.Snapshot();
    public string CurrentSaleId => First.CurrentSaleId;
    public string? LastToken => First.LastToken;
    public string PresentCard(string cardName, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert) => First.PresentCard(cardName, grade, tender, source);
    public (bool Ok, string SaleId, string Reason) TryPresentCard(string cardName, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert) => First.TryPresentCard(cardName, grade, tender, source);
    public void EnterCode(string code) => First.EnterCode(code);
    public string PayCash(Money amount, Grade? grade = null) => First.PayCash(amount, grade);
    public void CollectChange() => First.CollectChange();
    public void SelectGrade(Grade grade) => First.SelectGrade(grade);
    public void LiftNozzle() => First.LiftNozzle();
    public void InsertSpout() => First.InsertSpout();
    public void WithdrawSpout() => First.WithdrawSpout();
    public void SqueezeLever() => First.SqueezeLever();
    public void ReleaseLever() => First.ReleaseLever();
    public void HangNozzle() => First.HangNozzle();
    public void StartFueling(Grade grade = Grade.Regular) => First.StartFueling(grade);
    public void Cancel(string reason = "driver cancelled") => First.Cancel(reason);
    public void ResetSale() => First.ResetSale();
    public void AttackReplayLastToken() => First.AttackReplayLastToken();
    public void AttackTamperCeiling(Money newCeiling) => First.AttackTamperCeiling(newCeiling);
    public string AttackSignedOverLimit(Volume volume, Money money) => First.AttackSignedOverLimit(volume, money);
    public void AttackSpoofPulses(long pulses) => First.AttackSpoofPulses(pulses);
    public void AttackArmWithoutToken() => First.AttackArmWithoutToken();
    public void KillPulser() => First.KillPulser();
    public void FixPulser() => First.FixPulser();
    public void DriveOff() => First.DriveOff();
    public void ResetBreakaway() => First.ResetBreakaway();
    public void ClosePump(string by = "operator") => First.ClosePump(by);
    public void OpenPump(string by = "operator") => First.OpenPump(by);

    /// <summary>Pump 1's view changed. Each point raises its own; the host listens to every point.</summary>
    public event Action<DispenserView>? DispenserViewChanged { add => First.DispenserViewChanged += value; remove => First.DispenserViewChanged -= value; }
    public event Action<LogLine>? DispenserLog { add => First.DispenserLog += value; remove => First.DispenserLog -= value; }
    public event Action<LedgerEntry>? DispenserLedger { add => First.DispenserLedger += value; remove => First.DispenserLedger -= value; }
    public event Action<PumpTotals>? TotalsRead { add => First.TotalsRead += value; remove => First.TotalsRead -= value; }

    /// <summary>What every run did about the sales the previous run left open, one outcome per pump in pump order.</summary>
    public IReadOnlyList<RecoveryOutcome> Recoveries => _points.Select(p => p.LastRecovery).ToArray();

    // ----- The price board

    private Pricing _prices;
    /// <summary>Held from the read of the old board through the write of the new one and the fan-out to every island, so two admin posts at once apply in one order everywhere and neither is lost.</summary>
    private readonly Lock _priceGate = new();

    /// <summary>The station's posted prices, in mills per gallon. Written to every island on connect and on every change.</summary>
    public Pricing Prices { get { lock (_priceGate) return _prices; } }

    /// <summary>
    /// The back office changes one posted price. The PIN rule is the admin
    /// panel's. The new board goes to every island as a write, which each pump
    /// answers; a change during a delivery takes effect at the next one. The
    /// board is journaled, so a station restart writes the operator's prices
    /// back and never the built-in ones.
    /// </summary>
    public (bool Ok, string Reason) AdminSetPrice(string pin, Grade grade, long tenthCentsPerGallon)
    {
        var (admitted, why) = Site.TryAdmit(pin);
        if (!admitted) return (false, why);
        Pricing board;
        string before;
        // One lock from the read to the fan-out: a second admin post waits, starts from this board, and every island hears the two in the same order.
        lock (_priceGate)
        {
            board = _prices.With(grade, tenthCentsPerGallon);
            before = _prices.Display(grade);
            var (valid, invalid) = board.Validate();
            if (!valid)
            {
                Ledger.Append("admin.refused", $"price {grade} {tenthCentsPerGallon} mills: {invalid}");
                return (false, invalid);
            }
            _prices = board;
            Ledger.Append("admin.prices", $"{grade} {before} -> {board.Display(grade)}");
            Log.Write("admin", "price {Grade} {Before} -> {After}", ("Grade", grade), ("Before", before), ("After", board.Display(grade)));
            foreach (var p in _points) p.WritePrices(board, "admin");
        }
        SaveJournal();
        return (true, $"{grade} {before} -> {board.Display(grade)}");
    }

    // ----- The journal

    public string RunId { get; }
    public string? CustodyPath { get; }
    public string? JournalPath => _options.Journal?.StatePath;

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
            var ok = CustodyFile.Verify(copy, j.Ikm, out var seq, out var reason);
            var lines = File.ReadLines(copy).Count();
            return (ok, seq, reason, lines);
        }
        finally { try { File.Delete(copy); } catch (IOException) { } }
    }

    private bool _bankBooksLoaded;

    /// <summary>
    /// Key injection for one pump's reader at station start: a new initial key ID (the key set ID, the pump number, 3 random
    /// bytes) and its initial key from the bank's base key. A new ID at every start keeps every KSN new, so the bank's saved
    /// KSN ledger still refuses a message recorded before a restart.
    /// </summary>
    private CardReader InjectReader(int pumpNumber)
    {
        var initialKeyId = BankKeys.NewInitialKeyId(pumpNumber);
        var reader = new CardReader(Issuer.Keys.InitialKey(initialKeyId), initialKeyId);
        Log.Write("reader", "pump {Number} reader injected, initial key id {InitialKeyId}", ("Number", pumpNumber), ("InitialKeyId", initialKeyId));
        return reader;
    }

    private void Restore(JournalSnapshot s)
    {
        // The price board was taken from the snapshot in the constructor, before the links were built.
        foreach (var p in _points)
            if (s.Point(p.Number) is { } jp) p.Restore(jp, _prices);
        // Books from before the card reader sat in the station journal. They load once; from then on the bank keeps its own file.
        if (!_bankBooksLoaded && s.Issuer is { } carried) Issuer.Import(carried);
        Site.Restore(new Money(s.CreditHoldCents), new Money(s.DebitHoldCents), s.FailedPins, s.LockedUntil);
        Drawer.Import(
            s.CashOpenCents.ToDictionary(kv => kv.Key, kv => new Money(kv.Value)),
            s.CashChangeDueCents.ToDictionary(kv => kv.Key, kv => new Money(kv.Value)));
        Gateway.Restore(
            s.Pending.Select(p => new PendingAdvice(p.SaleId, Enum.Parse<PendingKind>(p.Kind), new Money(p.FinalCents), JournalLine.To(p.Lines))),
            s.Tenders.ToDictionary(kv => kv.Key, kv => Enum.Parse<Tender>(kv.Value)));
    }

    /// <summary>Close out what the last run left open on every pump, then drain whatever the bank is still owed.</summary>
    private void ApplyRecovery(JournalSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var ages = _points.Select(p => p.LastRecovery.SnapshotAge).Where(a => a > TimeSpan.Zero).DefaultIfEmpty(TimeSpan.Zero).Max();
        var totals = string.Join(", ", _points.Select(p => $"{p.PumpId} {new Volume(p.View.TotalizerMilliGallons)}"));
        Ledger.Append("recovery.restart", $"run {RunId} started from journal of {snapshot.RunId}, saved {ages.TotalSeconds:0.0} s before, totalizers {totals}");
        foreach (var p in _points) p.ApplyRecovery(p.LastRecovery);
        // A journal from a station with more pumps than this one (GASPUMP_PUMPS lowered): the missing pumps' sales still hold money
        // and still close out. There is no point to carry it out, so the site does the money part here. The pump itself is gone.
        var period = _options.Journal?.Period ?? TimeSpan.FromSeconds(1);
        foreach (var orphan in snapshot.Points.Where(jp => jp.Number > _points.Length))
        {
            var r = RecoveryPlan.Build(snapshot, orphan.Number, _clock.GetUtcNow(), period);
            Ledger.Append("recovery.orphan-pump", $"{orphan.PumpId} (pump {orphan.Number}) is not on this station; {r.Kind} {r.SaleId} {r.Money}");
            Log.Error("recovery", "pump {Number} ({PumpId}) is in the journal and not on this station: {Note}", ("Number", orphan.Number), ("PumpId", orphan.PumpId), ("Note", r.Note));
            switch (r.Kind)
            {
                case RecoveryKind.ClosedOut or RecoveryKind.CompletionResent:
                    _paymentBox.Post(new PaymentMessage.Complete(r.SaleId!, r.Money, r.Lines));
                    break;
                case RecoveryKind.Reversed:
                    _paymentBox.Post(new PaymentMessage.Reverse(r.SaleId!));
                    break;
            }
        }
        if (Gateway.Pending.Count > 0) _paymentBox.Post(new PaymentMessage.DrainPending("-"));
    }

    private JournalSnapshot CaptureSnapshot(string? stopReason = null) => new(
        JournalSnapshot.CurrentVersion, _clock.GetUtcNow(), RunId,
        _points.Select(p => p.CaptureJournal()).ToList(),
        Gateway.Pending.Select(p => new JournalPending(p.SaleId, p.Kind.ToString(), p.Final.Cents, JournalLine.From(p.Lines))).ToList(),
        Gateway.Tenders.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()),
        Drawer.Open.ToDictionary(kv => kv.Key, kv => kv.Value.Cents),
        Drawer.ChangeDue.ToDictionary(kv => kv.Key, kv => kv.Value.Cents),
        Site.CreditHold.Cents, Site.DebitHold.Cents, Site.FailedPins, Site.LockedUntil,
        null, stopReason,
        Prices.TenthCentsPerGallon.ToDictionary(kv => kv.Key, kv => kv.Value));

    /// <summary>Write the snapshot. Called by the one-second timer and on every sale change on any pump, serialized so two never interleave.</summary>
    public void SaveJournal(string? stopReason = null)
    {
        if (_options.Journal is not { } j) return;
        lock (_journalGate)
        {
            if (_frozen) return;
            try
            {
                StateJournal.Save(j.StatePath, CaptureSnapshot(stopReason));
                BankBooks.Save(j.BankPath, Issuer.Export());
            }
            catch (Exception ex) { Log.Error("journal", "snapshot failed: {Message}", ("Message", ex.Message)); }
        }
    }

    // ----- The site's shared parts

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

    /// <summary>
    /// Telemetry listeners that run beside the sales: a video that plays when
    /// fueling starts, a loyalty prompt, a forecourt display. Each one has its
    /// own worker and mailbox. Register one before the first sale.
    /// </summary>
    public CapabilityHost Capabilities { get; }

    /// <summary>A sale changed on any pump. The snapshot's sale id says which.</summary>
    public event Action<SaleSnapshot>? SaleChanged;

    /// <summary>Counters for a harness or a debugger: mailbox backlogs per pump, host calls in flight, host calls parked on the clock.</summary>
    public string Counters => $"{string.Join(" ", _points.Select(p => p.Counters))} payment={_paymentBox.Count} inflight={Volatile.Read(ref _inflightPayments)} waiting={_networkSim.WaitingOnClock} drainwait={Gateway.DrainWaiters}";

    /// <summary>True when every mailbox is empty, no handler is running, and no host call is in flight.</summary>
    public bool IsIdle => _dispatch.IsIdle && Volatile.Read(ref _inflightPayments) == 0;

    /// <summary>
    /// True when nothing can move until the clock does: every sale and pump box
    /// is empty, and the only work left is a host call waiting on the network.
    /// A test harness advances time when it sees this.
    /// </summary>
    public bool IsQuiet
    {
        get
        {
            foreach (var p in _points) if (!p.IsQuiet) return false;
            var inflight = Volatile.Read(ref _inflightPayments);
            if (_paymentBox.Count == 0 && inflight == 0) return true;
            // Every in-flight call must be parked on the clock, or behind the one drain that is. One that is neither is a continuation still landing.
            return _networkSim.WaitingOnClock + Gateway.DrainWaiters >= inflight && inflight > 0;
        }
    }

    /// <summary>The back office. Every attempt lands in the ledger through Site.Changed.</summary>
    public (bool Ok, string Reason) AdminSetHold(string pin, Tender tender, Money amount) => Site.TrySet(pin, tender, amount);

    // ----- The tank. One underground tank feeds every pump, so its alarm is the site's and reaches every island.

    /// <summary>The underground tank ran out, or the gauge shut the line. Every pump's valve position means nothing until the supply is back.</summary>
    public void TankRunsDry()
    {
        Log.Warn("tank", "{Code} product below the fuel-out level, supply to every pump stops", ("Code", DiagnosticCodes.TankFuelOut.Code));
        Ledger.Append("tank.fuelout", DiagnosticCodes.TankFuelOut.Format("fuel out, supply stopped"));
        foreach (var p in _points) p.SetSupply(false);
    }

    public void TankRefilled()
    {
        Log.Write("tank", "{Code} cleared, supply restored to every pump", ("Code", DiagnosticCodes.TankFuelOut.Code));
        Ledger.Append("tank.refilled", DiagnosticCodes.TankFuelOut.Format("cleared, supply restored"));
        foreach (var p in _points) p.SetSupply(true);
    }

    public void DrainPendingCompletions() => _paymentBox.Post(new PaymentMessage.DrainPending("-"));

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

    /// <summary>
    /// The station emergency stop. It models the power going away under every
    /// pump, the STPs, and every signal circuit at once, and sends no message
    /// to a pump. The site controller outside the classified area writes the
    /// record first, saves one last snapshot so the restart knows this was an
    /// E-stop and not a crash, then the power drops. Reset is a cashier action
    /// that builds a new Simulation on the same journal.
    /// </summary>
    public void EmergencyStop(string pressedBy = "forecourt button")
    {
        var code = DiagnosticCodes.StationEStop;
        Log.Error("station", "{Code} emergency stop pressed by {By}: power removed from every dispenser and STP", ("Code", code.Code), ("By", pressedBy));
        var sales = string.Join("; ", _points.Select(p => { var s = p.Snapshot(); return $"sale {s.SaleId} {s.State} {s.Volume} {s.Money}"; }));
        Ledger.Append("station.estop", code.Format($"pressed by {pressedBy}; {sales}"));
        Custody?.Pump();
        // The final snapshot is the last journal write. After it, nothing may rewrite the journal or turn the station's own
        // power cut into a dispenser fault on an open sale. The links are about to drop because the station is dropping them.
        lock (_journalGate)
        {
            SaveJournal(stopReason: "ESTOP");
            _frozen = true;
        }
        // The forecourt goes dark: every dispenser in another process is told the power is off and ends. In-process they end with us.
        foreach (var p in _points) p.PowerOff();
        TearDown();
    }

    /// <summary>True from the E-stop's final snapshot on. The journal and the link reactions are done; only the teardown remains.</summary>
    private volatile bool _frozen;

    // ----- The payment worker. One message at a time, for every pump. Its answers go back to the pump the sale is on.

    /// <summary>
    /// Hand a sale event to the sale worker of the pump its sale id names. An
    /// id that names no pump of this station is a fault of the id, never of
    /// the money: an approval for it still put a hold at the bank, so the hold
    /// is reversed from here; anything else is logged and dropped.
    /// </summary>
    private void PostToSale(SaleEvent e)
    {
        if (PointOf(e.SaleId) is { } point) { point.SaleBox.Post(e); return; }
        Ledger.Append("sale.unroutable", $"{e.SaleId} {e.GetType().Name} names no pump of this station");
        if (e is SaleEvent.Approved a)
        {
            Log.Error("payment", "approval {Ceiling} for {SaleId} names no pump of this station; reversing the hold", ("Ceiling", a.Ceiling), ("SaleId", a.SaleId));
            _paymentBox.Post(new PaymentMessage.Reverse(a.SaleId));
            return;
        }
        Log.Warn("payment", "{Event} for {SaleId} names no pump of this station; dropped", ("Event", e.GetType().Name), ("SaleId", e.SaleId));
    }

    private void HandlePaymentMessage(PaymentMessage m)
    {
        Interlocked.Increment(ref _inflightPayments);
        var work = RunPaymentAsync(m);
        if (_dispatch.PrefersBlockingIo)
        {
            // The 2008 way: this worker has its own thread, and the host call blocks it. Every other pump's payment waits behind it.
            work.GetAwaiter().GetResult();
        }
        else
        {
            // The modern way: let the call continue on a callback. The worker moves on to the next pump's message. Results post back as events.
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
                    Log.Write("payment", "pre-auth {Tender} {Hold} on {Last4} for {PumpId}", ("Tender", pa.Tender), ("Hold", Site.HoldFor(pa.Tender)), ("Last4", pa.Card.Last4), ("PumpId", pa.PumpId));
                    var result = await Gateway.PreAuthorizeAsync(pa.SaleId, pa.PumpId, pa.Card, pa.Tender, _cts.Token);
                    if (result.Approved) { Log.Write("payment", "{ReasonCode} ceiling {Ceiling}", ("ReasonCode", result.ReasonCode), ("Ceiling", result.Ceiling)); PostToSale(new SaleEvent.Approved(pa.SaleId, result.Ceiling, result.Token!)); }
                    else if (result.NetworkFailure)
                    {
                        Log.Warn("payment", "network: {Message}", ("Message", result.Message));
                        // Refused means the request never left: no hold, nothing to reverse. Timed out means the bank may have heard us.
                        if (result.ReasonCode == nameof(NetworkOutcome.ConnectionRefused)) PostToSale(new SaleEvent.AuthorizationUnreachable(pa.SaleId, "connection refused"));
                        else PostToSale(new SaleEvent.AuthorizationTimedOut(pa.SaleId));
                    }
                    else { Log.Write("payment", "declined {ReasonCode}: {Message}", ("ReasonCode", result.ReasonCode), ("Message", result.Message)); PostToSale(new SaleEvent.Declined(pa.SaleId, result.ReasonCode, result.Message)); }
                    break;

                case PaymentMessage.CashAuthorize ca:
                    var cash = Gateway.CashAuthorize(ca.SaleId, ca.PumpId, ca.Amount);
                    Log.Write("payment", "{Message}", ("Message", cash.Message));
                    if (cash.Approved) PostToSale(new SaleEvent.Approved(ca.SaleId, cash.Ceiling, cash.Token!));
                    else PostToSale(new SaleEvent.Declined(ca.SaleId, cash.ReasonCode, cash.Message));
                    break;

                case PaymentMessage.Complete cp:
                    var completion = await Gateway.CompleteAsync(cp.SaleId, cp.Final, cp.Lines, _cts.Token);
                    Log.Write("payment", completion.Message);
                    if (completion.Sent) PostToSale(new SaleEvent.CompletionSent(cp.SaleId, cp.Final));
                    else PostToSale(new SaleEvent.CompletionQueued(cp.SaleId, cp.Final));
                    break;

                case PaymentMessage.Reverse rv:
                    var reversed = await Gateway.ReverseAsync(rv.SaleId, _cts.Token);
                    Log.Write("payment", reversed.Message);
                    PostToSale(reversed.Sent ? new SaleEvent.Reversed(rv.SaleId) : new SaleEvent.ReversalQueued(rv.SaleId));
                    break;

                case PaymentMessage.DrainPending:
                    Log.Write("payment", "draining {Queued} queued advice(s)", ("Queued", Gateway.Pending.Count));
                    var sent = await Gateway.DrainPendingAsync(_cts.Token);
                    Log.Write("payment", "drained {Sent} queued advice(s), {Left} left", ("Sent", sent.Count), ("Left", Gateway.Pending.Count));
                    // Tell each sale where its money landed. The machine updates Hold; an orphan is logged by its pump.
                    foreach (var advice in sent)
                        PostToSale(advice.Kind == PendingKind.Reversal
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
        // Timers first, so no command deadline or sale timer fires into a station that is going dark: a power loss leaves no records.
        foreach (var p in _points) p.StopTimers();
        // The strategy owns the workers, mailboxes, and tickers. One Dispose, in reverse creation order. Then each link.
        _dispatch.Dispose();
        foreach (var p in _points) p.Dispose();
        _cts.Dispose();
        if (_ownsLog && Log is IDisposable d) d.Dispose();
        _custody?.Dispose();
    }
}
