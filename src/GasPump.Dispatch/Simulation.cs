using GasPump.Core;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Security;

namespace GasPump.Dispatch;

public sealed record SimulationOptions(
    string PumpId,
    Money HoldAmount,
    TimeSpan TickPeriod,
    FlowRate NominalFlow,
    HardLimits HardLimits,
    TaperPolicy Taper,
    Pricing Pricing,
    SaleTimeouts Timeouts,
    PaymentGatewayOptions Gateway)
{
    public static SimulationOptions Default => new(
        PumpId: "PUMP-07",
        HoldAmount: Money.FromDollars(75),
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
    private SaleStateMachine _sale;
    private IDisposable? _saleTimer;
    private DateTimeOffset? _saleDeadline;
    private int _saleCounter;
    private int _inflightPayments;

    public Simulation(IDispatchStrategy dispatch, TimeProvider clock, SimulationOptions? options = null, IssuerSimulator? issuer = null, byte[]? hmacKey = null)
    {
        _dispatch = dispatch;
        _clock = clock;
        _options = options ?? SimulationOptions.Default;
        Log = new EventLog(clock);
        Ledger = new EventLedger(clock);
        Totalizer = new Totalizer();
        Issuer = issuer ?? IssuerSimulator.WithTestCards();
        Network = new NetworkFaultPolicy();
        var key = hmacKey ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Signer = new HmacAuthorizationSigner(key);
        _networkSim = new NetworkSimulator(Network, clock);
        Gateway = new PaymentGateway(Issuer, _networkSim, Signer, clock, _options.PumpId, _options.Gateway);
        Nonces = new NonceStore();
        Authority = new PumpAuthority(new HmacAuthorizationVerifier(key, _options.PumpId, Nonces, TimeSpan.FromSeconds(5)), _options.HardLimits, Ledger, clock);
        Meter = new Meter();
        Pump = new PumpController(Authority, new Hydraulics(_options.NominalFlow), Meter, Totalizer, Ledger, _options.Pricing, _options.Taper, clock);

        _sale = NewSale();

        _saleBox = dispatch.CreateMailbox<SaleEvent>("sale", 1024);
        _pumpBox = dispatch.CreateMailbox<PumpMessage>("pump", 1024);
        _paymentBox = dispatch.CreateMailbox<PaymentMessage>("payment", 64);

        _owned.Add(dispatch.StartWorker("sale", _saleBox, HandleSaleEvent));
        _owned.Add(dispatch.StartWorker("pump", _pumpBox, HandlePumpMessage));
        _owned.Add(dispatch.StartWorker("payment", _paymentBox, HandlePaymentMessage));
        _owned.Add(dispatch.StartTicker("pump-tick", _options.TickPeriod, _pumpBox, () => new PumpMessage.Tick(_options.TickPeriod)));

        Log.Write("sim", $"started with {dispatch.Name} dispatch, pump {_options.PumpId}, hold {_options.HoldAmount}");
    }

    public EventLog Log { get; }
    public EventLedger Ledger { get; }
    public Totalizer Totalizer { get; }
    public IssuerSimulator Issuer { get; }
    public NetworkFaultPolicy Network { get; }
    public PaymentGateway Gateway { get; }
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
    public string PresentCard(string cardName, Grade? grade = null)
    {
        string id;
        lock (_gate)
        {
            if (_sale.State is not SaleState.Idle) ReplaceSale();
            id = _sale.SaleId;
        }
        _saleBox.Post(new SaleEvent.CardPresented(id, cardName, grade));
        return id;
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

    // ----- Workers. Each handler runs one message at a time on its own worker.

    private void HandleSaleEvent(SaleEvent e)
    {
        IReadOnlyList<SaleCommand> commands;
        lock (_gate)
        {
            if (e.SaleId != _sale.SaleId) { HandleOrphanEvent(e); return; }
            commands = _sale.Apply(e);
        }
        foreach (var c in commands) Execute(c);
        Publish();
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
                Log.Write("sale", $"LATE approval {a.Ceiling} for finished sale {a.SaleId}; reversing the orphan hold (saga compensation)");
                _paymentBox.Post(new PaymentMessage.Reverse(a.SaleId));
                break;
            case SaleEvent.Reversed:
                Log.Write("sale", $"hold for earlier sale {e.SaleId} released");
                break;
            case SaleEvent.CompletionSent cs:
                Log.Write("sale", $"completion {cs.Final} for earlier sale {e.SaleId} posted");
                break;
            case SaleEvent.ReversalQueued or SaleEvent.CompletionQueued or SaleEvent.Declined or SaleEvent.AuthorizationTimedOut:
                Log.Write("sale", $"{e.GetType().Name} for earlier sale {e.SaleId} noted");
                break;
            default:
                Log.Write("sale", $"dropped {e.GetType().Name} for other sale {e.SaleId}");
                break;
        }
    }

    private void Execute(SaleCommand c)
    {
        switch (c)
        {
            case SaleCommand.RequestAuthorization ra:
                var card = TestCards.ByName.TryGetValue(ra.CardName, out var known) ? known : new CardData("0000000000000000", 1, 2000, CardKind.Credit);
                _paymentBox.Post(new PaymentMessage.PreAuthorize(ra.SaleId, card, ra.Hold));
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
                if (ok) { Log.Write("pump", $"armed {armed!.Ceiling} max {armed.MaxVolume} ({armed.ClampNote})"); _saleBox.Post(new SaleEvent.PumpArmed(arm.SaleId, armed.Ceiling, armed.MaxVolume)); }
                else { Log.Write("pump", $"REFUSED arm for {arm.SaleId}: {reason}"); _saleBox.Post(new SaleEvent.PumpRefused(arm.SaleId, reason)); }
                break;

            case PumpMessage.SelectGrade sg:
                var (gradeOk, gradeWhy) = Pump.SelectGrade(sg.Grade);
                if (gradeOk) _saleBox.Post(new SaleEvent.GradeSelected(saleId, sg.Grade));
                else Log.Write("pump", $"grade {sg.Grade} refused: {gradeWhy}");
                break;

            case PumpMessage.Lift:
                if (Pump.LiftNozzle()) _saleBox.Post(new SaleEvent.NozzleLifted(saleId));
                else Log.Write("pump", $"nozzle lifted while {Pump.Phase}, nothing happens");
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
                if (!flowOk) Log.Write("pump", $"trigger ignored: {flowWhy}");
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
                Log.Write("pump", $"disarmed: {stop.Reason}");
                break;

            case PumpMessage.SpoofPulses sp:
                Log.Write("attack", $"spoofing {sp.Pulses} pulses");
                Meter.InjectPulses(sp.Pulses);
                break;

            case PumpMessage.Tick tick:
                var before = Pump.Phase;
                var t = Pump.Tick(tick.Dt);
                if (t.Fault is not null && before != PumpPhase.Faulted)
                {
                    Log.Write("pump", $"FAULT {t.Fault}");
                    _saleBox.Post(new SaleEvent.Fault(saleId, t.Fault));
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
                    Log.Write("payment", $"pre-auth {pa.Hold} on {pa.Card.Last4}");
                    var result = await Gateway.PreAuthorizeAsync(pa.SaleId, pa.Card, pa.Hold, _cts.Token);
                    if (result.Approved) { Log.Write("payment", $"{result.ReasonCode} ceiling {result.Ceiling}"); _saleBox.Post(new SaleEvent.Approved(pa.SaleId, result.Ceiling, result.Token!)); }
                    else if (result.NetworkFailure) { Log.Write("payment", $"network: {result.Message}"); _saleBox.Post(new SaleEvent.AuthorizationTimedOut(pa.SaleId)); }
                    else { Log.Write("payment", $"declined {result.ReasonCode}: {result.Message}"); _saleBox.Post(new SaleEvent.Declined(pa.SaleId, result.ReasonCode, result.Message)); }
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
                    Log.Write("payment", $"draining {Gateway.Pending.Count} queued advice(s)");
                    var sent = await Gateway.DrainPendingAsync(_cts.Token);
                    Log.Write("payment", $"drained {sent.Count} queued advice(s), {Gateway.Pending.Count} left");
                    // Tell the sale where its money landed. The machine updates Hold; an orphan is logged.
                    foreach (var advice in sent)
                        _saleBox.Post(advice.Kind == PendingKind.Reversal
                            ? new SaleEvent.Reversed(advice.SaleId)
                            : new SaleEvent.CompletionSent(advice.SaleId, advice.Final));
                    break;
            }
        }
        catch (OperationCanceledException) { Log.Write("payment", $"cancelled while handling {m.GetType().Name}"); }
        catch (Exception ex)
        {
            Log.Write("payment", $"ERROR {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _inflightPayments);
        }
    }

    private SaleStateMachine NewSale()
    {
        var id = $"S{Interlocked.Increment(ref _saleCounter):0000}";
        return new SaleStateMachine(id, _options.HoldAmount, _options.Timeouts);
    }

    private void Publish()
    {
        SaleSnapshot snap;
        lock (_gate) snap = _sale.Snapshot(_saleDeadline);
        SaleChanged?.Invoke(snap);
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate) _saleTimer?.Dispose();
        // The strategy owns the workers, mailboxes, and tickers. One Dispose, in reverse creation order.
        _dispatch.Dispose();
        _cts.Dispose();
    }
}
