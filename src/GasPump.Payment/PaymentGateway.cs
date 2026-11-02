using GasPump.Core;

namespace GasPump.Payment;

public sealed record PreAuthResult(bool Approved, Money Ceiling, string? Token, string ReasonCode, string Message, bool NetworkFailure);

/// <summary>Change is the cash owed back after a cash sale. Zero for card sales.</summary>
public sealed record CompletionResult(bool Sent, bool Queued, string Message, Money Change = default);

/// <summary>Refund is the cash handed back after a cash sale that pumped nothing. Zero for card sales.</summary>
public sealed record ReversalResult(bool Sent, bool Queued, string Message, Money Refund = default);

/// <summary>
/// One message the host owes the bank. Kind is Completion or Reversal; Final is
/// zero for a reversal. Lines itemize Final for the bank's record; null when the
/// sender had no breakdown. Two advices are the same advice when SaleId and Kind
/// match, whatever their lines, so the queue never holds two for one sale.
/// </summary>
public sealed record PendingAdvice(string SaleId, PendingKind Kind, Money Final, IReadOnlyList<ChargeLine>? Lines = null)
{
    public bool SameAs(PendingAdvice other) => SaleId == other.SaleId && Kind == other.Kind;
}

public enum PendingKind { Completion, Reversal }

/// <summary>
/// The EPS (electronic payment server, the station box that talks to the bank), in miniature. Two-phase: PreAuthorize puts a hold on, Complete
/// settles it to the real amount, Reverse drops it when no fuel moved.
///
/// The hold it asks for is the site's ceiling for the tender (see
/// <see cref="SiteLimits"/>), and the ceiling the pump gets is the lesser of
/// that and what the bank approved. Cash never reaches the bank: the cashier
/// took the money, so the token is for that amount and completion is a
/// change calculation at the till.
///
/// Completions and reversals that cannot reach the host go in one
/// store-and-forward queue and drain on the next call that succeeds. Every
/// call is keyed by sale id so a retry is harmless on both ends. This queue is
/// the saga's compensation log: a step that could not run yet, kept until it can.
/// </summary>
public sealed class PaymentGateway
{
    private readonly IIssuer _issuer;
    private readonly INetwork _network;
    private readonly IAuthorizationSigner _signer;
    private readonly TimeProvider _clock;
    private readonly PaymentGatewayOptions _options;
    private readonly Lock _gate = new();
    private readonly List<PendingAdvice> _pending = [];
    private readonly Dictionary<string, Tender> _tenders = new();

    public PaymentGateway(
        IIssuer issuer,
        INetwork network,
        IAuthorizationSigner signer,
        TimeProvider clock,
        PaymentGatewayOptions? options = null,
        SiteLimits? site = null,
        CashDrawer? drawer = null)
    {
        _issuer = issuer;
        _network = network;
        _signer = signer;
        _clock = clock;
        _options = options ?? PaymentGatewayOptions.Default;
        Site = site ?? new SiteLimits(clock, Money.FromDollars(75), Money.FromDollars(50));
        Drawer = drawer ?? new CashDrawer();
    }

    public SiteLimits Site { get; }
    public CashDrawer Drawer { get; }

    /// <summary>
    /// Everything still owed to the bank, oldest first. Reversals and
    /// completions share one queue so order is kept. An advice is written here
    /// BEFORE its first attempt on the wire and removed only when the bank has
    /// it, so the queue is the saga's compensation log: a crash at any point
    /// between the decision and the bank's answer leaves the intent on disk
    /// for the next start to finish.
    /// </summary>
    public IReadOnlyList<PendingAdvice> Pending
    {
        get { lock (_gate) return _pending.ToArray(); }
    }

    /// <summary>Raised after every change to <see cref="Pending"/>. The station journals on it, so the intent reaches disk before the wire.</summary>
    public event Action? PendingChanged;

    /// <summary>
    /// Record what the bank is owed before anything is sent. Idempotent per
    /// sale and kind. Cash owes the bank nothing and is settled at the till.
    /// </summary>
    public void Intend(PendingAdvice advice)
    {
        if (TenderOf(advice.SaleId) == Tender.Cash) return;
        if (Enqueue(advice)) PendingChanged?.Invoke();
    }

    public IReadOnlyList<(string SaleId, Money Final)> PendingCompletions =>
        Pending.Where(p => p.Kind == PendingKind.Completion).Select(p => (p.SaleId, p.Final)).ToArray();

    public IReadOnlyList<string> PendingReversals =>
        Pending.Where(p => p.Kind == PendingKind.Reversal).Select(p => p.SaleId).ToArray();

    public Tender TenderOf(string saleId) { lock (_gate) return _tenders.GetValueOrDefault(saleId, Tender.Credit); }

    /// <summary>For the journal: which tender each sale used, so a completion after a restart goes to the till or the bank correctly.</summary>
    public IReadOnlyDictionary<string, Tender> Tenders { get { lock (_gate) return new Dictionary<string, Tender>(_tenders); } }

    /// <summary>Bring back the store-and-forward queue and the tender map after a restart. The queue is the saga's compensation log; losing it is losing money.</summary>
    public void Restore(IEnumerable<PendingAdvice> pending, IReadOnlyDictionary<string, Tender> tenders)
    {
        lock (_gate)
        {
            _pending.Clear();
            _pending.AddRange(pending);
            foreach (var kv in tenders) _tenders[kv.Key] = kv.Value;
        }
    }

    /// <summary>
    /// Card path. The request to the bank is the site's ceiling for the
    /// tender. The pump's ceiling is the lesser of that and the approved
    /// amount, which is the partial-approval rule and the debit rule in one
    /// line: a $50 debit ceiling against a $900 balance holds $50, and against
    /// an $18.50 balance it holds $18.50.
    /// </summary>
    /// <param name="pumpId">The pump the sale is on. The token is signed for it, and only that pump's authority accepts the token.</param>
    /// <param name="keypad">What the driver typed at the keypad: the PIN for debit, the billing ZIP for credit. It rides to the bank on the card data and is checked there.</param>
    public async Task<PreAuthResult> PreAuthorizeAsync(string saleId, string pumpId, CardData card, Tender tender, string? keypad, CancellationToken ct)
    {
        if (tender == Tender.Cash) throw new ArgumentException("cash does not pre-authorize; use CashAuthorize", nameof(tender));
        lock (_gate) _tenders[saleId] = tender;

        var now = _clock.GetUtcNow();
        var check = CardValidator.Check(card, now);
        if (check != CardCheck.Ok)
            return new PreAuthResult(false, Money.Zero, null, check.ToString(), LocalDeclineMessage(check), NetworkFailure: false);

        // The tender decides what the typed code means. Debit sends it as the PIN; a debit sale with no PIN is a PIN failure
        // at the bank, which is what a real terminal sees. Credit sends it as the ZIP for the address check.
        var presented = tender == Tender.Debit
            ? card with { Kind = CardKind.Debit, Pin = keypad }
            : card with { Zip = keypad };
        var requested = Site.HoldFor(tender);

        var result = await _network.SendAsync(() => _issuer.Authorize(saleId, presented, requested, now), _options.HostTimeout, ct);
        if (!result.Delivered)
            return new PreAuthResult(false, Money.Zero, null, result.Outcome.ToString(), $"host {result.Outcome}", NetworkFailure: true);

        var decision = result.Value!;
        if (!decision.Approved)
            return new PreAuthResult(false, Money.Zero, null, decision.Reason.ToString(), decision.Message, NetworkFailure: false);

        // The admin may have lowered the ceiling while the call was out. The lesser of the two always wins.
        var ceiling = Money.Min(decision.ApprovedAmount, Site.HoldFor(tender));
        var token = _signer.Issue(saleId, pumpId, ceiling, _options.MaxVolumePerSale, now, _options.TokenLifetime);
        var code = decision.IsPartial(requested) ? "PartialApproval" : "Approved";
        return new PreAuthResult(true, ceiling, token.Serialize(), code, $"{decision.Message}, {tender} ceiling {ceiling}", NetworkFailure: false);
    }

    /// <summary>Cash path. No bank. The cashier's amount is the ceiling, to the cent, and the pump's hard limits still clamp it.</summary>
    public PreAuthResult CashAuthorize(string saleId, string pumpId, Money amount)
    {
        lock (_gate) _tenders[saleId] = Tender.Cash;
        if (amount <= Money.Zero)
            return new PreAuthResult(false, Money.Zero, null, "NoCash", "no cash taken", NetworkFailure: false);
        Drawer.Take(saleId, amount);
        var token = _signer.Issue(saleId, pumpId, amount, _options.MaxVolumePerSale, _clock.GetUtcNow(), _options.TokenLifetime);
        return new PreAuthResult(true, amount, token.Serialize(), "CashPrepaid", $"cash {amount} taken at the cashier", NetworkFailure: false);
    }

    public Task<CompletionResult> CompleteAsync(string saleId, Money final, CancellationToken ct) => CompleteAsync(saleId, final, null, ct);

    /// <summary>Settle the hold to <paramref name="final"/>. <paramref name="lines"/> itemize it for the bank's record; the sum must be <paramref name="final"/>.</summary>
    public async Task<CompletionResult> CompleteAsync(string saleId, Money final, IReadOnlyList<ChargeLine>? lines, CancellationToken ct)
    {
        if (TenderOf(saleId) == Tender.Cash)
        {
            var change = Drawer.Settle(saleId, final);
            return new CompletionResult(true, false, change > Money.Zero ? $"cash sale {final}, change due {change}" : $"cash sale {final}, paid in full", change);
        }
        // Intent first, then one send path for everything owed, oldest first. If an older advice fails, this one waits behind it.
        var advice = new PendingAdvice(saleId, PendingKind.Completion, final, lines);
        Intend(advice);
        await DrainPendingAsync(ct);
        // Sent means the bank has it, whichever drain carried it: with two pumps, the other pump's drain may have sent this advice first.
        return StillPending(advice)
            ? new CompletionResult(false, true, $"host {LastOutcome}, completion {final} queued")
            : new CompletionResult(true, false, $"completion {final} sent");
    }

    /// <summary>
    /// Give the hold back. If the bank cannot be reached the reversal is queued
    /// and never dropped, because a dropped one leaves a hold on the driver's
    /// card for a sale that dispensed nothing. Cash refunds at the till.
    /// </summary>
    public async Task<ReversalResult> ReverseAsync(string saleId, CancellationToken ct)
    {
        if (TenderOf(saleId) == Tender.Cash)
        {
            var refund = Drawer.Refund(saleId);
            return new ReversalResult(true, false, $"cash refund {refund} at the cashier", refund);
        }
        var advice = new PendingAdvice(saleId, PendingKind.Reversal, Money.Zero);
        Intend(advice);
        await DrainPendingAsync(ct);
        return StillPending(advice)
            ? new ReversalResult(false, true, $"host {LastOutcome}, reversal queued")
            : new ReversalResult(true, false, "hold reversed");
    }

    private bool StillPending(PendingAdvice advice) { lock (_gate) return _pending.Any(advice.SameAs); }

    /// <summary>The outcome of the last attempt on the wire, for the message a queued advice carries.</summary>
    private NetworkOutcome LastOutcome { get { lock (_gate) return _lastOutcome; } }
    private NetworkOutcome _lastOutcome = NetworkOutcome.Delivered;

    /// <summary>One drain at a time. Two pumps completing at once under a non-blocking strategy would otherwise both send the head advice.</summary>
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private int _drainWaiters;

    /// <summary>Calls parked behind another drain. A harness counts these as waiting on the clock, because the drain they wait for is.</summary>
    public int DrainWaiters => Volatile.Read(ref _drainWaiters);

    /// <summary>
    /// Try every queued advice, oldest first. Stop at the first failure and
    /// keep the rest. Returns what this drain delivered. Single-flight: a
    /// second caller waits for the first drain to finish, then drains what is
    /// left, so no advice goes to the bank twice from two drains.
    /// </summary>
    public async Task<IReadOnlyList<PendingAdvice>> DrainPendingAsync(CancellationToken ct)
    {
        var sent = new List<PendingAdvice>();
        Interlocked.Increment(ref _drainWaiters);
        try { await _drainGate.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref _drainWaiters); }
        try
        {
            while (true)
            {
                PendingAdvice next;
                lock (_gate)
                {
                    if (_pending.Count == 0) return sent;
                    next = _pending[0];
                }
                var result = next.Kind == PendingKind.Completion
                    ? await _network.SendAsync(() => { _issuer.Complete(next.SaleId, next.Final, next.Lines ?? []); return true; }, _options.HostTimeout, ct)
                    : await _network.SendAsync(() => { _issuer.Reverse(next.SaleId); return true; }, _options.HostTimeout, ct);
                lock (_gate) _lastOutcome = result.Outcome;
                if (!result.Delivered) return sent;
                lock (_gate) _pending.Remove(next);
                sent.Add(next);
                PendingChanged?.Invoke();
            }
        }
        finally { _drainGate.Release(); }
    }

    /// <summary>One advice per sale and kind. A retry that fails again must never double the queue. Returns true when it was new.</summary>
    private bool Enqueue(PendingAdvice advice)
    {
        lock (_gate)
        {
            if (_pending.Any(advice.SameAs)) return false;
            _pending.Add(advice);
            return true;
        }
    }

    private static string LocalDeclineMessage(CardCheck check) => check switch
    {
        CardCheck.Expired => "card expired",
        CardCheck.BadLuhn => "card read error, try again",
        CardCheck.BadFormat => "card not recognized",
        _ => "declined",
    };
}

public sealed record PaymentGatewayOptions(TimeSpan HostTimeout, TimeSpan TokenLifetime, Volume MaxVolumePerSale)
{
    public static PaymentGatewayOptions Default => new(
        HostTimeout: TimeSpan.FromSeconds(30),
        TokenLifetime: TimeSpan.FromMinutes(5),
        MaxVolumePerSale: Volume.FromGallons(50));
}
