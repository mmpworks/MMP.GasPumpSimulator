using GasPump.Core;

namespace GasPump.Payment;

public sealed record PreAuthResult(bool Approved, Money Ceiling, string? Token, string ReasonCode, string Message, bool NetworkFailure);

public sealed record CompletionResult(bool Sent, bool Queued, string Message);

public sealed record ReversalResult(bool Sent, bool Queued, string Message);

/// <summary>One message the host owes the bank. Kind is Completion or Reversal; Final is zero for a reversal.</summary>
public sealed record PendingAdvice(string SaleId, PendingKind Kind, Money Final);

public enum PendingKind { Completion, Reversal }

/// <summary>
/// The EPS, in miniature. Two-phase: PreAuthorize puts a hold on, Complete
/// settles it to the real amount, Reverse drops it when no fuel moved.
///
/// Completions and reversals that cannot reach the host go in one
/// store-and-forward queue and drain on the next call that succeeds. Every
/// call is keyed by sale id so a retry is harmless on both ends. This queue is
/// the saga's compensation log: a step that could not run yet, kept until it can.
/// </summary>
public sealed class PaymentGateway(
    IIssuer issuer,
    INetwork network,
    IAuthorizationSigner signer,
    TimeProvider clock,
    string pumpId,
    PaymentGatewayOptions? options = null)
{
    private readonly PaymentGatewayOptions _options = options ?? PaymentGatewayOptions.Default;
    private readonly Lock _gate = new();
    private readonly List<PendingAdvice> _pending = [];

    /// <summary>Everything still owed to the bank, oldest first. Reversals and completions share one queue so order is kept.</summary>
    public IReadOnlyList<PendingAdvice> Pending
    {
        get { lock (_gate) return _pending.ToArray(); }
    }

    public IReadOnlyList<(string SaleId, Money Final)> PendingCompletions =>
        Pending.Where(p => p.Kind == PendingKind.Completion).Select(p => (p.SaleId, p.Final)).ToArray();

    public IReadOnlyList<string> PendingReversals =>
        Pending.Where(p => p.Kind == PendingKind.Reversal).Select(p => p.SaleId).ToArray();

    public async Task<PreAuthResult> PreAuthorizeAsync(string saleId, CardData card, Money hold, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var check = CardValidator.Check(card, now);
        if (check != CardCheck.Ok)
            return new PreAuthResult(false, Money.Zero, null, check.ToString(), LocalDeclineMessage(check), NetworkFailure: false);

        var result = await network.SendAsync(() => issuer.Authorize(saleId, card, hold, now), _options.HostTimeout, ct);
        if (!result.Delivered)
            return new PreAuthResult(false, Money.Zero, null, result.Outcome.ToString(), $"host {result.Outcome}", NetworkFailure: true);

        var decision = result.Value!;
        if (!decision.Approved)
            return new PreAuthResult(false, Money.Zero, null, decision.Reason.ToString(), decision.Message, NetworkFailure: false);

        // The ceiling the pump gets is the approved amount, which may be less than the hold.
        var token = signer.Issue(saleId, pumpId, decision.ApprovedAmount, _options.MaxVolumePerSale, now, _options.TokenLifetime);
        return new PreAuthResult(true, decision.ApprovedAmount, token.Serialize(), decision.IsPartial(hold) ? "PartialApproval" : "Approved", decision.Message, NetworkFailure: false);
    }

    public async Task<CompletionResult> CompleteAsync(string saleId, Money final, CancellationToken ct)
    {
        await DrainPendingAsync(ct);
        var result = await network.SendAsync(() => { issuer.Complete(saleId, final); return true; }, _options.HostTimeout, ct);
        if (result.Delivered) return new CompletionResult(true, false, $"completion {final} sent");
        Enqueue(new PendingAdvice(saleId, PendingKind.Completion, final));
        return new CompletionResult(false, true, $"host {result.Outcome}, completion {final} queued");
    }

    /// <summary>
    /// Give the hold back. If the bank cannot be reached the reversal is queued,
    /// never dropped: a hold left on a card for a sale that dispensed nothing is
    /// the failure a driver remembers longest.
    /// </summary>
    public async Task<ReversalResult> ReverseAsync(string saleId, CancellationToken ct)
    {
        var result = await network.SendAsync(() => { issuer.Reverse(saleId); return true; }, _options.HostTimeout, ct);
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
                ? await network.SendAsync(() => { issuer.Complete(next.SaleId, next.Final); return true; }, _options.HostTimeout, ct)
                : await network.SendAsync(() => { issuer.Reverse(next.SaleId); return true; }, _options.HostTimeout, ct);
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
