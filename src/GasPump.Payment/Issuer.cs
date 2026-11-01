using GasPump.Core;

namespace GasPump.Payment;

public enum DeclineReason
{
    None,
    InsufficientFunds,
    Expired,
    LostOrStolen,
    DoNotHonor,
    InvalidPin,
    VelocityExceeded,
    IssuerUnavailable,
    UnknownCard,
}

/// <summary>
/// What the issuer says back. Approved for the full hold, approved for less
/// (partial approval, which the US networks require for fuel), or declined
/// with a reason the terminal can show.
/// </summary>
public sealed record IssuerDecision(bool Approved, Money ApprovedAmount, DeclineReason Reason, string Message)
{
    public bool IsPartial(Money requested) => Approved && ApprovedAmount < requested;
    public static IssuerDecision Approve(Money amount) => new(true, amount, DeclineReason.None, "approved");
    public static IssuerDecision Partial(Money amount) => new(true, amount, DeclineReason.None, $"partial approval {amount}");
    public static IssuerDecision Decline(DeclineReason reason, string message) => new(false, Money.Zero, reason, message);
}

public interface IIssuer
{
    IssuerDecision Authorize(string saleId, CardData card, Money hold, DateTimeOffset now);
    void Complete(string saleId, Money final);
    void Reverse(string saleId);
    Money AvailableBalance(string pan);
}

public enum AccountStatus { Active, Frozen, LostOrStolen }

/// <summary>
/// A bank in a dictionary. Every account has a balance, a status, and a
/// velocity window. Holds come off the balance at authorization and settle to
/// the real amount at completion, which is the part that confuses drivers who
/// see $75 on their statement for a $30 fill.
/// </summary>
public sealed class IssuerSimulator : IIssuer
{
    private sealed class Account
    {
        public required Money Available;
        public AccountStatus Status;
        public string? Pin;
        public int VelocityLimit = int.MaxValue;
        public readonly List<DateTimeOffset> RecentUses = [];
    }

    private sealed record Hold(string Pan, Money Amount);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Account> _accounts = new();
    private readonly Dictionary<string, Hold> _holds = new();
    /// <summary>
    /// Sale ids that were reversed before any hold arrived. A reversal that
    /// outruns its own authorization is the late-network race; the tombstone
    /// makes the late authorization land as a decline instead of an orphan hold.
    /// </summary>
    private readonly HashSet<string> _voided = new();
    private readonly TimeSpan _velocityWindow = TimeSpan.FromMinutes(10);

    /// <summary>Holds the issuer still has open, by sale id. The books a driver would dispute.</summary>
    public IReadOnlyCollection<string> OpenHolds { get { lock (_gate) return _holds.Keys.ToArray(); } }

    public bool Unavailable { get; set; }

    public static IssuerSimulator WithTestCards()
    {
        var issuer = new IssuerSimulator();
        issuer.Seed(TestCards.Get("Good").Pan, Money.FromDollars(5000));
        issuer.Seed(TestCards.Get("LowBalance").Pan, Money.FromDollars(18.50m));
        issuer.Seed(TestCards.Get("InsufficientFunds").Pan, Money.Zero);
        issuer.Seed(TestCards.Get("Expired").Pan, Money.FromDollars(5000));
        issuer.Seed(TestCards.Get("Stolen").Pan, Money.FromDollars(5000), AccountStatus.LostOrStolen);
        issuer.Seed(TestCards.Get("Frozen").Pan, Money.FromDollars(5000), AccountStatus.Frozen);
        issuer.Seed(TestCards.Get("Velocity").Pan, Money.FromDollars(5000), velocityLimit: 2);
        issuer.Seed(TestCards.Get("DebitGoodPin").Pan, Money.FromDollars(900), pin: "1234");
        issuer.Seed(TestCards.Get("DebitBadPin").Pan, Money.FromDollars(900), pin: "1234");
        return issuer;
    }

    public void Seed(string pan, Money available, AccountStatus status = AccountStatus.Active, string? pin = null, int velocityLimit = int.MaxValue)
    {
        lock (_gate)
            _accounts[pan] = new Account { Available = available, Status = status, Pin = pin, VelocityLimit = velocityLimit };
    }

    public IssuerDecision Authorize(string saleId, CardData card, Money hold, DateTimeOffset now)
    {
        if (Unavailable) return IssuerDecision.Decline(DeclineReason.IssuerUnavailable, "issuer unavailable");
        lock (_gate)
        {
            if (_voided.Contains(saleId))
                return IssuerDecision.Decline(DeclineReason.DoNotHonor, "sale already voided by the terminal");
            if (_holds.ContainsKey(saleId))
                return IssuerDecision.Decline(DeclineReason.DoNotHonor, "duplicate authorization for an open sale");
            if (!_accounts.TryGetValue(card.Pan, out var acct))
                return IssuerDecision.Decline(DeclineReason.UnknownCard, "no such account");
            if (acct.Status == AccountStatus.LostOrStolen)
                return IssuerDecision.Decline(DeclineReason.LostOrStolen, "card reported lost or stolen, pick up card");
            if (acct.Status == AccountStatus.Frozen)
                return IssuerDecision.Decline(DeclineReason.DoNotHonor, "do not honor");
            if (card.Kind == CardKind.Debit && (card.Pin is null || card.Pin != acct.Pin))
                return IssuerDecision.Decline(DeclineReason.InvalidPin, "invalid PIN");

            acct.RecentUses.RemoveAll(t => now - t > _velocityWindow);
            if (acct.RecentUses.Count >= acct.VelocityLimit)
                return IssuerDecision.Decline(DeclineReason.VelocityExceeded, "too many uses, try later");
            acct.RecentUses.Add(now);

            if (acct.Available <= Money.Zero)
                return IssuerDecision.Decline(DeclineReason.InsufficientFunds, "insufficient funds");

            // Fuel is MCC 5542. Partial approval is the rule, not the exception.
            var granted = Money.Min(hold, acct.Available);
            acct.Available -= granted;
            _holds[saleId] = new Hold(card.Pan, granted);
            return granted < hold ? IssuerDecision.Partial(granted) : IssuerDecision.Approve(granted);
        }
    }

    public void Complete(string saleId, Money final)
    {
        lock (_gate)
        {
            if (!_holds.Remove(saleId, out var hold)) return;   // duplicate completion, idempotent
            var acct = _accounts[hold.Pan];
            // Give back the part of the hold that was not spent.
            var refund = hold.Amount - Money.Min(final, hold.Amount);
            acct.Available += refund;
        }
    }

    public void Reverse(string saleId)
    {
        lock (_gate)
        {
            if (!_holds.Remove(saleId, out var hold))
            {
                // Nothing to release yet. Remember the void so a late authorization for this sale is refused.
                _voided.Add(saleId);
                return;
            }
            _accounts[hold.Pan].Available += hold.Amount;
        }
    }

    public Money AvailableBalance(string pan)
    {
        lock (_gate) return _accounts.TryGetValue(pan, out var a) ? a.Available : Money.Zero;
    }
}
