using GasPump.Core;

namespace GasPump.Payment;

public sealed record PreAuthResult(bool Approved, Money Ceiling, string? Token, string ReasonCode, string Message, bool NetworkFailure);

public sealed record CompletionResult(bool Sent, bool Queued, string Message);

/// <summary>
/// The EPS, in miniature. Two-phase: PreAuthorize puts a hold on, Complete
/// settles it to the real amount, Reverse drops it when no fuel moved.
/// Completions that cannot reach the host go in a store-and-forward list and
/// drain on the next call that succeeds. Every call is keyed by sale id so a
/// retry is harmless on both ends.
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
    private readonly List<(string SaleId, Money Final)> _pendingCompletions = [];

    public IReadOnlyList<(string SaleId, Money Final)> PendingCompletions
    {
        get { lock (_gate) return _pendingCompletions.ToArray(); }
    }

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
        lock (_gate) _pendingCompletions.Add((saleId, final));
        return new CompletionResult(false, true, $"host {result.Outcome}, completion {final} queued");
    }

    public async Task<bool> ReverseAsync(string saleId, CancellationToken ct)
    {
        var result = await network.SendAsync(() => { issuer.Reverse(saleId); return true; }, _options.HostTimeout, ct);
        return result.Delivered;
    }

    /// <summary>Try every queued completion, oldest first. Stop at the first failure and keep the rest.</summary>
    public async Task<int> DrainPendingAsync(CancellationToken ct)
    {
        int sent = 0;
        while (true)
        {
            (string SaleId, Money Final) next;
            lock (_gate)
            {
                if (_pendingCompletions.Count == 0) return sent;
                next = _pendingCompletions[0];
            }
            var result = await network.SendAsync(() => { issuer.Complete(next.SaleId, next.Final); return true; }, _options.HostTimeout, ct);
            if (!result.Delivered) return sent;
            lock (_gate) _pendingCompletions.RemoveAt(0);
            sent++;
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
