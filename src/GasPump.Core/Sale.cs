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

/// <summary>Everything that can happen to a sale. Device threads, timers, and the host all post one of these.</summary>
public abstract record SaleEvent(string SaleId)
{
    public sealed record CardPresented(string SaleId, string CardName, Grade Grade) : SaleEvent(SaleId);
    public sealed record CardReadFailed(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record Approved(string SaleId, Money Ceiling, string Token) : SaleEvent(SaleId);
    public sealed record Declined(string SaleId, string ReasonCode, string Message) : SaleEvent(SaleId);
    public sealed record AuthorizationTimedOut(string SaleId) : SaleEvent(SaleId);
    public sealed record PumpArmed(string SaleId, Money Ceiling, Volume MaxVolume) : SaleEvent(SaleId);
    public sealed record PumpRefused(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record NozzleLifted(string SaleId) : SaleEvent(SaleId);
    public sealed record Progress(string SaleId, Volume Volume, Money Money, FlowRate Flow, string Phase) : SaleEvent(SaleId);
    public sealed record NozzleHung(string SaleId) : SaleEvent(SaleId);
    public sealed record CeilingReached(string SaleId, Volume Volume, Money Money) : SaleEvent(SaleId);
    public sealed record CompletionSent(string SaleId, Money Final) : SaleEvent(SaleId);
    public sealed record CompletionQueued(string SaleId, Money Final) : SaleEvent(SaleId);
    public sealed record Reversed(string SaleId) : SaleEvent(SaleId);
    public sealed record Timeout(string SaleId, SaleState InState) : SaleEvent(SaleId);
    public sealed record Cancel(string SaleId, string Reason) : SaleEvent(SaleId);
    public sealed record Fault(string SaleId, string Reason) : SaleEvent(SaleId);
}

/// <summary>What the state machine asks the outside world to do. It never does any of it itself.</summary>
public abstract record SaleCommand(string SaleId)
{
    public sealed record RequestAuthorization(string SaleId, string CardName, Grade Grade, Money Hold) : SaleCommand(SaleId);
    public sealed record ArmPump(string SaleId, string Token, Grade Grade) : SaleCommand(SaleId);
    public sealed record OpenValve(string SaleId) : SaleCommand(SaleId);
    public sealed record CloseValve(string SaleId) : SaleCommand(SaleId);
    public sealed record SendCompletion(string SaleId, Money Final) : SaleCommand(SaleId);
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
    string? LastMessage);
