using System.Text.Json.Serialization;

namespace GasPump.Core;

public enum SaleState
{
    Idle,
    CardRead,
    Authorizing,
    ReadyToPump,
    Fueling,
    Completing,
    StoreAndForward,
    Declined,
    Cancelled,
    Faulted,
}

/// <summary>
/// Where the driver's money is. The sale state says what the pump is doing;
/// the hold status says what the bank has. The two move separately, and the
/// gap between them is where every "I was charged and got no gas" story lives.
/// </summary>
public enum HoldStatus
{
    /// <summary>No request has gone to the bank.</summary>
    None,
    /// <summary>A hold was requested and no answer has come back yet.</summary>
    Requested,
    /// <summary>The bank holds the money. Fuel may flow.</summary>
    Held,
    /// <summary>The hold was settled to the real amount. The sale is paid.</summary>
    Settled,
    /// <summary>The hold was released in full. Nothing was charged.</summary>
    Released,
    /// <summary>Fuel moved, the bank link was lost, and the settlement sits in a queue. It will post.</summary>
    SettlePending,
    /// <summary>No fuel moved, the bank link was lost, and the release sits in a queue. It will post.</summary>
    ReleasePending,
}

/// <summary>
/// How the driver pays. Credit and debit go to the bank for a hold, each
/// under its own site ceiling. Cash goes to the cashier, and the cash amount
/// is the ceiling: no bank, no hold, no automated threshold.
/// </summary>
public enum Tender { Credit, Debit, Cash }

/// <summary>
/// Where the card was read. At the pump, by insert or by tap, the driver is
/// standing at the hose and nobody else can cancel for them, so the wait for
/// a nozzle lift is short. At the station the cashier holds the sale and can
/// cancel it by hand, so the wait is longer. Cash is always at the station.
/// </summary>
public enum CardSource { PumpInsert, PumpTap, Station }

/// <summary>Why a sale ended. Drives the notice the driver reads and the next step it offers.</summary>
public enum EndReason
{
    None,
    Completed,
    CardDeclined,
    /// <summary>The bank may have heard us and did not answer in time.</summary>
    BankSilent,
    /// <summary>The link was down. The bank never heard us.</summary>
    BankDown,
    DriverCancelled,
    NoNozzleLift,
    MaxFuelingTime,
    PumpFault,
    SecurityRefusal,
}

/// <summary>The one thing a stranded driver can do next.</summary>
public enum DriverAction
{
    None,
    Wait,
    LiftNozzle,
    HangToStop,
    Leave,
    TryAgain,
    UseAnotherCard,
    UseAnotherPump,
    SeeAttendant,
}

/// <summary>What the pump screen says. Headline, one line of detail, one next step.</summary>
public sealed record DriverNotice(string Headline, string Detail, DriverAction Action);

/// <summary>Everything that can happen to a sale. Device threads, timers, and the host all post one of these. The JSON attributes let the same record cross a socket between the dispenser and the station.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$e")]
[JsonDerivedType(typeof(CardPresented), nameof(CardPresented))]
[JsonDerivedType(typeof(CashPaid), nameof(CashPaid))]
[JsonDerivedType(typeof(GradeSelected), nameof(GradeSelected))]
[JsonDerivedType(typeof(NozzleSeated), nameof(NozzleSeated))]
[JsonDerivedType(typeof(NozzleUnseated), nameof(NozzleUnseated))]
[JsonDerivedType(typeof(FlowStarted), nameof(FlowStarted))]
[JsonDerivedType(typeof(FlowPaused), nameof(FlowPaused))]
[JsonDerivedType(typeof(FlowResumed), nameof(FlowResumed))]
[JsonDerivedType(typeof(CardReadFailed), nameof(CardReadFailed))]
[JsonDerivedType(typeof(Approved), nameof(Approved))]
[JsonDerivedType(typeof(Declined), nameof(Declined))]
[JsonDerivedType(typeof(AuthorizationTimedOut), nameof(AuthorizationTimedOut))]
[JsonDerivedType(typeof(AuthorizationUnreachable), nameof(AuthorizationUnreachable))]
[JsonDerivedType(typeof(PumpArmed), nameof(PumpArmed))]
[JsonDerivedType(typeof(PumpRefused), nameof(PumpRefused))]
[JsonDerivedType(typeof(NozzleLifted), nameof(NozzleLifted))]
[JsonDerivedType(typeof(Progress), nameof(Progress))]
[JsonDerivedType(typeof(NozzleHung), nameof(NozzleHung))]
[JsonDerivedType(typeof(CeilingReached), nameof(CeilingReached))]
[JsonDerivedType(typeof(CompletionSent), nameof(CompletionSent))]
[JsonDerivedType(typeof(CompletionQueued), nameof(CompletionQueued))]
[JsonDerivedType(typeof(Reversed), nameof(Reversed))]
[JsonDerivedType(typeof(ReversalQueued), nameof(ReversalQueued))]
[JsonDerivedType(typeof(Timeout), nameof(Timeout))]
[JsonDerivedType(typeof(Cancel), nameof(Cancel))]
[JsonDerivedType(typeof(Fault), nameof(Fault))]
[JsonDerivedType(typeof(PumpService), nameof(PumpService))]
public abstract record SaleEvent(string SaleId)
{
    /// <summary>Grade is null when the driver will pick it at the pump after approval, which is the normal path.</summary>
    public sealed record CardPresented(string SaleId, string CardName, Grade? Grade = null, Tender Tender = Tender.Credit, CardSource Source = CardSource.PumpInsert) : SaleEvent(SaleId);
    /// <summary>The cashier took cash. Amount is the ceiling, to the cent.</summary>
    public sealed record CashPaid(string SaleId, Money Amount, Grade? Grade = null) : SaleEvent(SaleId);
    public sealed record GradeSelected(string SaleId, Grade Grade) : SaleEvent(SaleId);
    public sealed record NozzleSeated(string SaleId) : SaleEvent(SaleId);
    public sealed record NozzleUnseated(string SaleId) : SaleEvent(SaleId);
    /// <summary>The first squeeze of the trigger opened the valve. Fueling begins here, never at the lift.</summary>
    public sealed record FlowStarted(string SaleId) : SaleEvent(SaleId);
    public sealed record FlowPaused(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record FlowResumed(string SaleId) : SaleEvent(SaleId);
    public sealed record CardReadFailed(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record Approved(string SaleId, Money Ceiling, string Token) : SaleEvent(SaleId);
    public sealed record Declined(string SaleId, string ReasonCode, string Message) : SaleEvent(SaleId);
    /// <summary>The request left and nothing came back. The bank may have heard us, so the hold must be reversed to be sure.</summary>
    public sealed record AuthorizationTimedOut(string SaleId) : SaleEvent(SaleId);
    /// <summary>The link refused the connection. The request never left, so no hold exists and nothing needs reversing.</summary>
    public sealed record AuthorizationUnreachable(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record PumpArmed(string SaleId, Money Ceiling, Volume MaxVolume) : SaleEvent(SaleId);
    public sealed record PumpRefused(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record NozzleLifted(string SaleId) : SaleEvent(SaleId);
    /// <summary>Lines is the money itemized, from the pump's price schedule. Null when the sender has no breakdown; the sale then carries one fuel line.</summary>
    public sealed record Progress(string SaleId, Volume Volume, Money Money, FlowRate Flow, string Phase, IReadOnlyList<ChargeLine>? Lines = null) : SaleEvent(SaleId);
    public sealed record NozzleHung(string SaleId) : SaleEvent(SaleId);
    public sealed record CeilingReached(string SaleId, Volume Volume, Money Money, IReadOnlyList<ChargeLine>? Lines = null) : SaleEvent(SaleId);
    public sealed record CompletionSent(string SaleId, Money Final) : SaleEvent(SaleId);
    public sealed record CompletionQueued(string SaleId, Money Final) : SaleEvent(SaleId);
    public sealed record Reversed(string SaleId) : SaleEvent(SaleId);
    /// <summary>The release could not reach the bank and sits in the store-and-forward queue.</summary>
    public sealed record ReversalQueued(string SaleId) : SaleEvent(SaleId);
    public sealed record Timeout(string SaleId, SaleState InState) : SaleEvent(SaleId);
    public sealed record Cancel(string SaleId, string Reason) : SaleEvent(SaleId);
    /// <summary>Code is the diagnostic code of the part that failed, so the notice and the records can name it.</summary>
    public sealed record Fault(string SaleId, string Reason, string? Code = null) : SaleEvent(SaleId);
    /// <summary>The pump's service status changed: a part broke, a part was fixed, the link dropped or came back. The station posts it once per change.</summary>
    public sealed record PumpService(string SaleId, ServiceStatus Service) : SaleEvent(SaleId);
}

/// <summary>What the state machine asks the outside world to do. It never does any of it itself.</summary>
public abstract record SaleCommand(string SaleId)
{
    /// <summary>Ask the bank for a hold. How much is the site's call by tender, never the sale machine's.</summary>
    public sealed record RequestAuthorization(string SaleId, string CardName, Grade Grade, Tender Tender) : SaleCommand(SaleId);
    /// <summary>Ask the payment side for a token worth the cash taken.</summary>
    public sealed record RequestCashAuthorization(string SaleId, Money Amount) : SaleCommand(SaleId);
    public sealed record ArmPump(string SaleId, string Token, Grade? Grade) : SaleCommand(SaleId);
    public sealed record CloseValve(string SaleId) : SaleCommand(SaleId);
    /// <summary>Settle the hold to Final. Lines itemize Final for the bank's record and the driver's receipt; their sum is Final.</summary>
    public sealed record SendCompletion(string SaleId, Money Final, IReadOnlyList<ChargeLine> Lines) : SaleCommand(SaleId);
    public sealed record Reverse(string SaleId) : SaleCommand(SaleId);
    public sealed record ArmTimer(string SaleId, SaleState State, TimeSpan After) : SaleCommand(SaleId);
    public sealed record CancelTimer(string SaleId) : SaleCommand(SaleId);
    public sealed record Log(string SaleId, string Message) : SaleCommand(SaleId);
}

public sealed record SaleSnapshot(
    string SaleId,
    SaleState State,
    string? CardName,
    Grade Grade,
    Money Ceiling,
    Volume Volume,
    Money Money,
    FlowRate Flow,
    string Phase,
    string? LastMessage,
    HoldStatus Hold,
    EndReason End,
    DriverNotice Notice,
    bool GradeSelected,
    bool NozzleOut,
    bool NozzleSeated,
    Tender Tender,
    /// <summary>Where the card was read, or Station for cash.</summary>
    CardSource Source,
    /// <summary>The diagnostic code behind a fault, or null.</summary>
    string? FaultCode,
    /// <summary>Cash taken at the cashier. Zero for card sales.</summary>
    Money CashPrepaid,
    /// <summary>Change the cashier owes after a cash sale settled or refunded.</summary>
    Money ChangeDue,
    /// <summary>When the armed timer fires, on the simulation clock. Null when no timer is armed.</summary>
    DateTimeOffset? DeadlineAt,
    /// <summary>Money itemized. Empty before any fuel moves. The sum is Money.</summary>
    IReadOnlyList<ChargeLine>? Lines = null)
{
    public IReadOnlyList<ChargeLine> Charges => Lines ?? [];
}
