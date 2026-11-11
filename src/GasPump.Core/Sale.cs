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
/// the hold status says what the bank has. The two move separately, and a gap
/// between them is what produces an "I was charged and got no gas" complaint.
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
    /// <summary>The card was read and no PIN or ZIP code followed before the entry timer ran out.</summary>
    NoCodeEntered,
    MaxFuelingTime,
    /// <summary>The driver let go of the lever and never squeezed again. The pump finished the sale for the fuel delivered.</summary>
    NoFlow,
    PumpFault,
    SecurityRefusal,
}

/// <summary>The one thing a stranded driver can do next.</summary>
public enum DriverAction
{
    None,
    Wait,
    /// <summary>A debit card was read. The keypad wants the PIN.</summary>
    EnterPin,
    /// <summary>A credit card was read. The keypad wants the billing ZIP code.</summary>
    EnterZip,
    LiftNozzle,
    HangToStop,
    Leave,
    TryAgain,
    UseAnotherCard,
    UseAnotherPump,
    SeeAttendant,
}

/// <summary>What the pump screen shows: a headline, one line of detail, and one next step.</summary>
public sealed record DriverNotice(string Headline, string Detail, DriverAction Action);

/// <summary>Everything that can happen to a sale. Device threads, timers, and the host all post one of these. The JSON attributes let the same record cross a socket between the dispenser and the station.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$e")]
[JsonDerivedType(typeof(CardPresented), nameof(CardPresented))]
[JsonDerivedType(typeof(CodeEntered), nameof(CodeEntered))]
[JsonDerivedType(typeof(CashPaid), nameof(CashPaid))]
[JsonDerivedType(typeof(GradeSelected), nameof(GradeSelected))]
[JsonDerivedType(typeof(SpoutInserted), nameof(SpoutInserted))]
[JsonDerivedType(typeof(SpoutWithdrawn), nameof(SpoutWithdrawn))]
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
    /// <summary>The driver finished the keypad entry: a four-digit PIN for debit, a five-digit billing ZIP for credit. The sale machine only checks the shape; the bank checks the value. From a pump the code is masked, a star per digit: the card reader keeps the digits.</summary>
    public sealed record CodeEntered(string SaleId, string Code) : SaleEvent(SaleId);
    /// <summary>The cashier took cash. Amount is the ceiling, to the cent.</summary>
    public sealed record CashPaid(string SaleId, Money Amount, Grade? Grade = null) : SaleEvent(SaleId);
    public sealed record GradeSelected(string SaleId, Grade Grade) : SaleEvent(SaleId);
    public sealed record SpoutInserted(string SaleId) : SaleEvent(SaleId);
    public sealed record SpoutWithdrawn(string SaleId) : SaleEvent(SaleId);
    /// <summary>The first squeeze of the lever opened the valve. Fueling begins here, never at the lift.</summary>
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
    /// <summary>TxSeq names the transaction the dispenser wrote for this delivery, so the station can clear it once the money is dealt with. Null before any pour.</summary>
    public sealed record NozzleHung(string SaleId, long? TxSeq = null) : SaleEvent(SaleId);
    public sealed record CeilingReached(string SaleId, Volume Volume, Money Money, IReadOnlyList<ChargeLine>? Lines = null, long? TxSeq = null) : SaleEvent(SaleId);
    public sealed record CompletionSent(string SaleId, Money Final) : SaleEvent(SaleId);
    public sealed record CompletionQueued(string SaleId, Money Final) : SaleEvent(SaleId);
    public sealed record Reversed(string SaleId) : SaleEvent(SaleId);
    /// <summary>The release could not reach the bank and sits in the store-and-forward queue.</summary>
    public sealed record ReversalQueued(string SaleId) : SaleEvent(SaleId);
    /// <summary>A timer the sale armed has fired. Tag says which one: the state's own timer, or a named side timer such as the idle timer that runs while flow is paused.</summary>
    public sealed record Timeout(string SaleId, SaleState InState, string Tag = SaleCommand.StateTimer) : SaleEvent(SaleId);
    public sealed record Cancel(string SaleId, string Reason) : SaleEvent(SaleId);
    /// <summary>Code is the diagnostic code of the part that failed, so the notice and the records can name it.</summary>
    public sealed record Fault(string SaleId, string Reason, string? Code = null, long? TxSeq = null) : SaleEvent(SaleId);
    /// <summary>The pump's service status changed: a part broke, a part was fixed, the link dropped or came back. The station posts it once per change.</summary>
    public sealed record PumpService(string SaleId, ServiceStatus Service) : SaleEvent(SaleId);
}

/// <summary>What the state machine asks the outside world to do. It never does any of it itself.</summary>
public abstract record SaleCommand(string SaleId)
{
    /// <summary>The tag of the timer every state arms for itself. A side timer carries its own tag.</summary>
    public const string StateTimer = "state";
    /// <summary>The side timer that runs while flow is paused. When it fires, the pump finishes the sale for the fuel delivered.</summary>
    public const string IdleTimer = "idle";

    /// <summary>Ask the bank for a hold. The site sets the amount by tender. The sale machine doesn't. Code is the keypad entry as the machine saw it, masked on a pump; the reader seals the digits for the bank.</summary>
    public sealed record RequestAuthorization(string SaleId, string CardName, Grade Grade, Tender Tender, string Code) : SaleCommand(SaleId);
    /// <summary>Ask the payment side for a token worth the cash taken.</summary>
    public sealed record RequestCashAuthorization(string SaleId, Money Amount) : SaleCommand(SaleId);
    public sealed record ArmPump(string SaleId, string Token, Grade? Grade) : SaleCommand(SaleId);
    public sealed record CloseValve(string SaleId) : SaleCommand(SaleId);
    /// <summary>Settle the hold to Final. Lines itemize Final for the bank's record and the driver's receipt; their sum is Final.</summary>
    public sealed record SendCompletion(string SaleId, Money Final, IReadOnlyList<ChargeLine> Lines) : SaleCommand(SaleId);
    public sealed record Reverse(string SaleId) : SaleCommand(SaleId);
    /// <summary>The money for the delivery is settled or queued. Tell the pump it may drop transaction TxSeq and arm again.</summary>
    public sealed record ClearTransaction(string SaleId, long TxSeq) : SaleCommand(SaleId);
    /// <summary>Start a timer. Arming the same tag again replaces it. The Timeout it raises carries State and Tag back.</summary>
    public sealed record ArmTimer(string SaleId, SaleState State, TimeSpan After, string Tag = StateTimer) : SaleCommand(SaleId);
    /// <summary>Stop one timer by tag, or every timer of the sale when Tag is null.</summary>
    public sealed record CancelTimer(string SaleId, string? Tag = null) : SaleCommand(SaleId);
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
    bool SpoutInserted,
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
    /// <summary>The money itemized. Empty before any fuel moves. The lines sum to Money.</summary>
    IReadOnlyList<ChargeLine>? Lines = null)
{
    public IReadOnlyList<ChargeLine> Charges => Lines ?? [];
}

/// <summary>The keypad entry as the pump's sale machine sees it. The card reader keeps the digits and shows a star for each.</summary>
public static class KeypadEntry
{
    public const char Mask = '*';

    public static bool IsDigitOrMask(char c) => char.IsAsciiDigit(c) || c == Mask;
}
