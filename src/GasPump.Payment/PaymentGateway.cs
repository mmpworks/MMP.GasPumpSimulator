using GasPump.Core;

namespace GasPump.Payment;

public sealed record PreAuthResult(bool Approved, Money Ceiling, string? Token, string ReasonCode, string Message, bool NetworkFailure);

/// <summary>Change is the cash owed back after a cash sale. Zero for card sales.</summary>
public sealed record CompletionResult(bool Sent, bool Queued, string Message, Money Change = default);

/// <summary>Refund is the cash handed back after a cash sale that pumped nothing. Zero for card sales.</summary>
public sealed record ReversalResult(bool Sent, bool Queued, string Message, Money Refund = default);

/// <summary>One message the host owes the bank. Kind is Completion or Reversal; Final is zero for a reversal.</summary>
public sealed record PendingAdvice(string SaleId, PendingKind Kind, Money Final);

public enum PendingKind { Completion, Reversal }

/// <summary>
/// The EPS, in miniature. Two-phase: PreAuthorize puts a hold on, Complete
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
    private readonly string _pumpId;
    private readonly PaymentGatewayOptions _options;
    private readonly Lock _gate = new();
    private readonly List<PendingAdvice> _pending = [];
    private readonly Dictionary<string, Tender> _tenders = new();

    public PaymentGateway(
        IIssuer issuer,
        INetwork network,
        IAuthorizationSigner signer,
        TimeProvider clock,
        string pumpId,
        PaymentGatewayOptions? options = null,
        SiteLimits? site = null,
        CashDrawer? drawer = null)
    {
        _issuer = issuer;
        _network = network;
        _signer = signer;
        _clock = clock;
        _pumpId = pumpId;
        _options = options ?? PaymentGatewayOptions.Default;
        Site = site ?? new SiteLimits(clock, Money.FromDollars(75), Money.FromDollars(50));
        Drawer = drawer ?? new CashDrawer();
    }

    public SiteLimits Site { get; }
    public CashDrawer Drawer { get; }

    /// <summary>Everything still owed to the bank, oldest first. Reversals and completions share one queue so order is kept.</summary>
    public IReadOnlyList<PendingAdvice> Pending
    {
        get { lock (_gate) return _pending.ToArray(); }
    }

    public IReadOnlyList<(string SaleId, Money Final)> PendingCompletions =>
        Pending.Where(p => p.Kind == PendingKind.Completion).Select(p => (p.SaleId, p.Final)).ToArray();

    public IReadOnlyList<string> PendingReversals =>
        Pending.Where(p => p.Kind == PendingKind.Reversal).Select(p => p.SaleId).ToArray();

    public Tender TenderOf(string saleId) { lock (_gate) return _tenders.GetValueOrDefault(saleId, Tender.Credit); }

    /// <summary>
    /// Card path. The request to the bank is the site's ceiling for the
    /// tender. The pump's ceiling is the lesser of that and the approved
    /// amount, which is the partial-approval rule and the debit rule in one
    /// line: a $50 debit ceiling against a $900 balance holds $50; against an
    /// $18.50 balance holds $18.50.
    /// </summary>
    public async Task<PreAuthResult> PreAuthorizeAsync(string saleId, CardData card, Tender tender, CancellationToken ct)
    {
        if (tender == Tender.Cash) throw new ArgumentException("cash does not pre-authorize; use CashAuthorize", nameof(tender));
        lock (_gate) _tenders[saleId] = tender;

        var now = _clock.GetUtcNow();
        var check = CardValidator.Check(card, now);
        if (check != CardCheck.Ok)
            return new PreAuthResult(false, Money.Zero, null, check.ToString(), LocalDeclineMessage(check), NetworkFailure: false);

        // Paying debit with a card that has no PIN on file is a PIN failure at the bank, which is what a real terminal sees.
        var presented = tender == Tender.Debit ? card with { Kind = CardKind.Debit } : card;
        var requested = Site.HoldFor(tender);

        var result = await _network.SendAsync(() => _issuer.Authorize(saleId, presented, requested, now), _options.HostTimeout, ct);
        if (!result.Delivered)
            return new PreAuthResult(false, Money.Zero, null, result.Outcome.ToString(), $"host {result.Outcome}", NetworkFailure: true);

        var decision = result.Value!;
        if (!decision.Approved)
            return new PreAuthResult(false, Money.Zero, null, decision.Reason.ToString(), decision.Message, NetworkFailure: false);

        // The admin may have lowered the ceiling while the call was out. The lesser of the two always wins.
        var ceiling = Money.Min(decision.ApprovedAmount, Site.HoldFor(tender));
        var token = _signer.Issue(saleId, _pumpId, ceiling, _options.MaxVolumePerSale, now, _options.TokenLifetime);
        var code = decision.IsPartial(requested) ? "PartialApproval" : "Approved";
        return new PreAuthResult(true, ceiling, token.Serialize(), code, $"{decision.Message}, {tender} ceiling {ceiling}", NetworkFailure: false);
    }

    /// <summary>Cash path. No bank. The cashier's amount is the ceiling, to the cent, and the pump's hard limits still clamp it.</summary>
    public PreAuthResult CashAuthorize(string saleId, Money amount)
    {
        lock (_gate) _tenders[saleId] = Tender.Cash;
        if (amount <= Money.Zero)
            return new PreAuthResult(false, Money.Zero, null, "NoCash", "no cash taken", NetworkFailure: false);
        Drawer.Take(saleId, amount);
        var token = _signer.Issue(saleId, _pumpId, amount, _options.MaxVolumePerSale, _clock.GetUtcNow(), _options.TokenLifetime);
        return new PreAuthResult(true, amount, token.Serialize(), "CashPrepaid", $"cash {amount} taken at the cashier", NetworkFailure: false);
    }

    public async Task<CompletionResult> CompleteAsync(string saleId, Money final, CancellationToken ct)
    {
        if (TenderOf(saleId) == Tender.Cash)
        {
            var change = Drawer.Settle(saleId, final);
            return new CompletionResult(true, false, change > Money.Zero ? $"cash sale {final}, change due {change}" : $"cash sale {final}, paid in full", change);
        }
        await DrainPendingAsync(ct);
        var result = await _network.SendAsync(() => { _issuer.Complete(saleId, final); return true; }, _options.HostTimeout, ct);
        if (result.Delivered) return new CompletionResult(true, false, $"completion {final} sent");
        Enqueue(new PendingAdvice(saleId, PendingKind.Completion, final));
        return new CompletionResult(false, true, $"host {result.Outcome}, completion {final} queued");
    }

    /// <summary>
    /// Give the hold back. If the bank cannot be reached the reversal is queued,
    /// never dropped: a hold left on a card for a sale that dispensed nothing is
    /// the failure a driver remembers longest. Cash refunds at the till.
    /// </summary>
    public async Task<ReversalResult> ReverseAsync(string saleId, CancellationToken ct)
    {
        if (TenderOf(saleId) == Tender.Cash)
        {
            var refund = Drawer.Refund(saleId);
            return new ReversalResult(true, false, $"cash refund {refund} at the cashier", refund);
        }
        var result = await _network.SendAsync(() => { _issuer.Reverse(saleId); return true; }, _options.HostTimeout, ct);
        if (result.Delivered) return new ReversalResult(true, false, "hold reversed");
        Enqueue(new PendingAdvice(saleId, PendingKind.Reversal, Money.Zero));
        return new ReversalResult(false, true, $"host {result.Outcome}, reversal queued");
    }

    /// <summary>Try every queued advice, oldest first. Stop at the first failure and keep the rest. Returns what was delivered.</summary>
    public async Task<IReadOnlyList<PendingAdvice>> DrainPendingAsync(CancellationToken ct)
    {
        var sent = new List<PendingAdvice>();
        while (true)
        {
            PendingAdvice next;
            lock (_gate)
            {
                if (_pending.Count == 0) return sent;
                next = _pending[0];
            }
            var result = next.Kind == PendingKind.Completion
                ? await _network.SendAsync(() => { _issuer.Complete(next.SaleId, next.Final); return true; }, _options.HostTimeout, ct)
                : await _network.SendAsync(() => { _issuer.Reverse(next.SaleId); return true; }, _options.HostTimeout, ct);
            if (!result.Delivered) return sent;
            lock (_gate) _pending.Remove(next);
            sent.Add(next);
        }
    }

    private void Enqueue(PendingAdvice advice)
    {
        lock (_gate)
        {
            // One advice per sale and kind. A retry that fails again must never double the queue.
            if (_pending.Any(p => p.SaleId == advice.SaleId && p.Kind == advice.Kind)) return;
            _pending.Add(advice);
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
