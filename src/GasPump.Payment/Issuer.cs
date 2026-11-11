using GasPump.Core;

namespace GasPump.Payment;

/// <summary>Why the bank said no. The name is the stable code: a decline carries it as its reason code.</summary>
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
    /// <summary>The bank has already accepted a message under this key serial number. A recorded message was sent again.</summary>
    KeyReplayed,
    /// <summary>The bank could not open the card data: the GCM tag failed. A changed byte, or a reader with keys this bank never injected.</summary>
    CardDataUnreadable,
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

/// <summary>The bank, as the terminal sees it. The card arrives as the reader sealed it; only the bank opens it.</summary>
public interface IIssuer
{
    IssuerDecision Authorize(string saleId, ProtectedCard card, Money hold, DateTimeOffset now);
    void Complete(string saleId, Money final);
    /// <summary>Complete with the charges itemized for the bank's record. An issuer that keeps no itemization settles the total.</summary>
    void Complete(string saleId, Money final, IReadOnlyList<ChargeLine> lines) => Complete(saleId, final);
    void Reverse(string saleId);
}

/// <summary>What the bank kept about a settled sale: the amount and its breakdown, for the statement and the dispute desk.</summary>
public sealed record Settlement(string SaleId, Money Final, IReadOnlyList<ChargeLine> Lines);

public enum AccountStatus { Active, Frozen, LostOrStolen }
