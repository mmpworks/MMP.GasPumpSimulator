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
    /// <summary>The billing ZIP typed at the keypad does not match the one on file. The address check (AVS) failed.</summary>
    ZipMismatch,
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
    /// <summary>Complete with the charges itemized for the bank's record. An issuer that keeps no itemization settles the total.</summary>
    void Complete(string saleId, Money final, IReadOnlyList<ChargeLine> lines) => Complete(saleId, final);
    void Reverse(string saleId);
    Money AvailableBalance(string pan);
}

/// <summary>What the bank kept about a settled sale: the amount and its breakdown, for the statement and the dispute desk.</summary>
public sealed record Settlement(string SaleId, Money Final, IReadOnlyList<ChargeLine> Lines);

public enum AccountStatus { Active, Frozen, LostOrStolen }

/// <summary>
/// An in-memory bank. Every account has a balance, a status, and a
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
        /// <summary>Billing ZIP on file. Null means the bank has no address check for this card.</summary>
        public string? Zip;
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

    /// <summary>Holds the issuer still has open, by sale id. These are the holds a driver could dispute.</summary>
    public IReadOnlyCollection<string> OpenHolds { get { lock (_gate) return _holds.Keys.ToArray(); } }

    public bool Unavailable { get; set; }

    public static IssuerSimulator WithTestCards()
    {
        var issuer = new IssuerSimulator();
        var zip = TestCards.Zip;
        issuer.Seed(TestCards.Get("Good").Pan, Money.FromDollars(5000), zip: zip);
        issuer.Seed(TestCards.Get("LowBalance").Pan, Money.FromDollars(18.50m), zip: zip);
        issuer.Seed(TestCards.Get("InsufficientFunds").Pan, Money.Zero, zip: zip);
        issuer.Seed(TestCards.Get("Expired").Pan, Money.FromDollars(5000), zip: zip);
        issuer.Seed(TestCards.Get("Stolen").Pan, Money.FromDollars(5000), AccountStatus.LostOrStolen, zip: zip);
        issuer.Seed(TestCards.Get("Frozen").Pan, Money.FromDollars(5000), AccountStatus.Frozen, zip: zip);
        issuer.Seed(TestCards.Get("Velocity").Pan, Money.FromDollars(5000), velocityLimit: 2, zip: zip);
        issuer.Seed(TestCards.Get("DebitGoodPin").Pan, Money.FromDollars(900), pin: TestCards.Pin);
        issuer.Seed(TestCards.Get("DebitBadPin").Pan, Money.FromDollars(900));
        return issuer;
    }

    public void Seed(string pan, Money available, AccountStatus status = AccountStatus.Active, string? pin = null, int velocityLimit = int.MaxValue, string? zip = null)
    {
        lock (_gate)
            _accounts[pan] = new Account { Available = available, Status = status, Pin = pin, Zip = zip, VelocityLimit = velocityLimit };
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
            // Credit runs the address check: the ZIP typed at the pump against the billing ZIP on file.
            if (card.Kind == CardKind.Credit && acct.Zip is not null && card.Zip != acct.Zip)
                return IssuerDecision.Decline(DeclineReason.ZipMismatch, "billing ZIP does not match");

            acct.RecentUses.RemoveAll(t => now - t > _velocityWindow);
            if (acct.RecentUses.Count >= acct.VelocityLimit)
                return IssuerDecision.Decline(DeclineReason.VelocityExceeded, "too many uses, try later");
            acct.RecentUses.Add(now);

            if (acct.Available <= Money.Zero)
                return IssuerDecision.Decline(DeclineReason.InsufficientFunds, "insufficient funds");

            // Fuel is MCC 5542, and partial approval is standard for it.
            var granted = Money.Min(hold, acct.Available);
            acct.Available -= granted;
            _holds[saleId] = new Hold(card.Pan, granted);
            return granted < hold ? IssuerDecision.Partial(granted) : IssuerDecision.Approve(granted);
        }
    }

    public void Complete(string saleId, Money final) => Complete(saleId, final, []);

    public void Complete(string saleId, Money final, IReadOnlyList<ChargeLine> lines)
    {
        lock (_gate)
        {
            if (!_holds.Remove(saleId, out var hold)) return;   // duplicate completion, idempotent
            var acct = _accounts[hold.Pan];
            // Give back the part of the hold that was not spent.
            var refund = hold.Amount - Money.Min(final, hold.Amount);
            acct.Available += refund;
            _settlements[saleId] = new Settlement(saleId, final, lines);
        }
    }

    private readonly Dictionary<string, Settlement> _settlements = new();

    /// <summary>The bank's record of every settled sale this run, itemized as the terminal sent it.</summary>
    public IReadOnlyList<Settlement> Settlements { get { lock (_gate) return _settlements.Values.ToArray(); } }

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

    /// <summary>
    /// For the journal. The real bank keeps its own books; this simulator's bank
    /// lives in the same process as the pump, so a restart has to bring it back
    /// too or every open hold would vanish with the process.
    /// </summary>
    public IssuerState Export()
    {
        lock (_gate)
        {
            return new IssuerState(
                _accounts.Select(kv => new IssuerAccountState(kv.Key, kv.Value.Available.Cents, kv.Value.Status.ToString(), kv.Value.Pin, kv.Value.VelocityLimit, kv.Value.RecentUses.ToList(), kv.Value.Zip)).ToList(),
                _holds.Select(kv => new IssuerHoldState(kv.Key, kv.Value.Pan, kv.Value.Amount.Cents)).ToList(),
                _voided.ToList());
        }
    }

    public void Import(IssuerState state)
    {
        lock (_gate)
        {
            _accounts.Clear(); _holds.Clear(); _voided.Clear();
            foreach (var a in state.Accounts)
            {
                var acct = new Account { Available = new Money(a.AvailableCents), Status = Enum.Parse<AccountStatus>(a.Status), Pin = a.Pin, Zip = a.Zip, VelocityLimit = a.VelocityLimit };
                acct.RecentUses.AddRange(a.RecentUses);
                _accounts[a.Pan] = acct;
            }
            foreach (var h in state.Holds) _holds[h.SaleId] = new Hold(h.Pan, new Money(h.AmountCents));
            foreach (var v in state.Voided) _voided.Add(v);
        }
    }
}

/// <summary>Zip is last and optional so a journal written before the address check still loads.</summary>
public sealed record IssuerAccountState(string Pan, long AvailableCents, string Status, string? Pin, int VelocityLimit, List<DateTimeOffset> RecentUses, string? Zip = null);
public sealed record IssuerHoldState(string SaleId, string Pan, long AmountCents);
public sealed record IssuerState(List<IssuerAccountState> Accounts, List<IssuerHoldState> Holds, List<string> Voided);
