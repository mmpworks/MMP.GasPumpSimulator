namespace GasPump.Core;

public sealed record SaleTimeouts(
    TimeSpan Authorization,
    TimeSpan NozzleLift,
    TimeSpan MaxFueling,
    TimeSpan Completion)
{
    public static SaleTimeouts Default => new(
        Authorization: TimeSpan.FromSeconds(30),
        NozzleLift: TimeSpan.FromSeconds(120),
        MaxFueling: TimeSpan.FromMinutes(10),
        Completion: TimeSpan.FromSeconds(30));
}

/// <summary>
/// One sale, one state, one writer. Apply takes an event and returns the
/// commands the outside world should run. Nothing in here touches a thread, a
/// timer, a socket, or a valve. That is what makes it testable with a list.
///
/// Illegal transitions log and stay put. Each state arms one timeout when it
/// is entered and cancels it when it is left.
/// </summary>
public sealed class SaleStateMachine
{
    private readonly SaleTimeouts _timeouts;
    private readonly Money _hold;

    public SaleStateMachine(string saleId, Money hold, SaleTimeouts? timeouts = null)
    {
        SaleId = saleId;
        _hold = hold;
        _timeouts = timeouts ?? SaleTimeouts.Default;
        State = SaleState.Idle;
        Grade = Grade.Regular;
        Phase = "idle";
    }

    public string SaleId { get; }
    public SaleState State { get; private set; }
    public string? CardName { get; private set; }
    public Grade Grade { get; private set; }
    public Money Ceiling { get; private set; }
    public Volume Volume { get; private set; }
    public Money Money { get; private set; }
    public FlowRate Flow { get; private set; }
    public string Phase { get; private set; }
    public string? LastMessage { get; private set; }
    public string? Token { get; private set; }

    public SaleSnapshot Snapshot() => new(SaleId, State, CardName, Grade, Ceiling, Volume, Money, Flow, Phase, LastMessage);

    public IReadOnlyList<SaleCommand> Apply(SaleEvent e)
    {
        if (e.SaleId != SaleId)
            return [new SaleCommand.Log(SaleId, $"dropped event for other sale {e.SaleId}")];

        var commands = new List<SaleCommand>(4);
        // Cancel and Fault are legal from anywhere that is not already terminal.
        switch (e)
        {
            case SaleEvent.Fault f:
                return Transition(SaleState.Faulted, commands, $"FAULT: {f.Reason}", closeValve: true, reverse: State is SaleState.ReadyToPump or SaleState.Authorizing);
            case SaleEvent.Cancel c when State is SaleState.CardRead or SaleState.Authorizing or SaleState.ReadyToPump:
                return Transition(SaleState.Cancelled, commands, $"cancelled: {c.Reason}", reverse: State is SaleState.Authorizing or SaleState.ReadyToPump);
            case SaleEvent.Timeout t when t.InState != State:
                commands.Add(new SaleCommand.Log(SaleId, $"stale timeout for {t.InState} ignored in {State}"));
                return commands;
        }

        switch (State, e)
        {
            case (SaleState.Idle, SaleEvent.CardPresented cp):
                CardName = cp.CardName;
                Grade = cp.Grade;
                commands.Add(new SaleCommand.RequestAuthorization(SaleId, cp.CardName, cp.Grade, _hold));
                return Transition(SaleState.Authorizing, commands, $"card {cp.CardName}, hold {_hold}", timeout: _timeouts.Authorization);

            case (SaleState.Authorizing, SaleEvent.Approved a):
                Ceiling = a.Ceiling;
                Token = a.Token;
                commands.Add(new SaleCommand.ArmPump(SaleId, a.Token, Grade));
                return Transition(SaleState.ReadyToPump, commands, $"approved, ceiling {a.Ceiling}", timeout: _timeouts.NozzleLift);

            case (SaleState.Authorizing, SaleEvent.Declined d):
                return Transition(SaleState.Declined, commands, $"declined {d.ReasonCode}: {d.Message}");

            case (SaleState.Authorizing, SaleEvent.AuthorizationTimedOut):
            case (SaleState.Authorizing, SaleEvent.Timeout):
                return Transition(SaleState.Declined, commands, "authorization timed out", reverse: true);

            case (SaleState.ReadyToPump, SaleEvent.PumpArmed pa):
                Ceiling = pa.Ceiling;
                Phase = "armed";
                commands.Add(new SaleCommand.Log(SaleId, $"pump armed, ceiling {pa.Ceiling}, max {pa.MaxVolume}"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.PumpRefused pr):
                return Transition(SaleState.Faulted, commands, $"pump refused: {pr.Reason}", reverse: true);

            case (SaleState.ReadyToPump, SaleEvent.NozzleLifted):
                commands.Add(new SaleCommand.OpenValve(SaleId));
                return Transition(SaleState.Fueling, commands, "nozzle lifted", timeout: _timeouts.MaxFueling);

            case (SaleState.ReadyToPump, SaleEvent.Timeout):
                return Transition(SaleState.Cancelled, commands, "no nozzle lift, hold reversed", reverse: true);

            case (SaleState.Fueling, SaleEvent.Progress p):
                Volume = p.Volume;
                Money = p.Money;
                Flow = p.Flow;
                Phase = p.Phase;
                return commands;

            case (SaleState.Fueling, SaleEvent.CeilingReached cr):
                Volume = cr.Volume;
                Money = cr.Money;
                Flow = FlowRate.Zero;
                Phase = "ceiling";
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "ceiling reached");

            case (SaleState.Fueling, SaleEvent.NozzleHung):
                Flow = FlowRate.Zero;
                Phase = "hung";
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "nozzle hung");

            case (SaleState.Fueling, SaleEvent.Timeout):
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "max fueling time, valve closed");

            case (SaleState.Completing, SaleEvent.CompletionSent cs):
                return Transition(SaleState.Idle, commands, $"completion sent for {cs.Final}");

            case (SaleState.Completing, SaleEvent.CompletionQueued cq):
                return Transition(SaleState.StoreAndForward, commands, $"host down, completion for {cq.Final} queued");

            case (SaleState.Completing, SaleEvent.Timeout):
                return Transition(SaleState.StoreAndForward, commands, "completion timed out, queued");

            case (SaleState.Completing, SaleEvent.Reversed):
            case (SaleState.Declined, SaleEvent.Reversed):
            case (SaleState.Cancelled, SaleEvent.Reversed):
                commands.Add(new SaleCommand.Log(SaleId, "hold reversed"));
                return commands;

            default:
                commands.Add(new SaleCommand.Log(SaleId, $"illegal: {e.GetType().Name} in {State}, ignored"));
                return commands;
        }
    }

    private IReadOnlyList<SaleCommand> Finish(List<SaleCommand> commands, string why)
    {
        if (Volume == Volume.Zero)
        {
            // No fuel moved. A completion for zero is wrong; the hold must be reversed in full.
            return Transition(SaleState.Cancelled, commands, $"{why}, no fuel, hold reversed", reverse: true);
        }
        commands.Add(new SaleCommand.SendCompletion(SaleId, Money));
        return Transition(SaleState.Completing, commands, $"{why}, {Volume} for {Money}", timeout: _timeouts.Completion);
    }

    private IReadOnlyList<SaleCommand> Transition(
        SaleState to,
        List<SaleCommand> commands,
        string message,
        TimeSpan? timeout = null,
        bool closeValve = false,
        bool reverse = false)
    {
        commands.Insert(0, new SaleCommand.CancelTimer(SaleId));
        if (closeValve) commands.Add(new SaleCommand.CloseValve(SaleId));
        if (reverse) commands.Add(new SaleCommand.Reverse(SaleId));
        if (timeout is { } t) commands.Add(new SaleCommand.ArmTimer(SaleId, to, t));
        commands.Add(new SaleCommand.Log(SaleId, $"{State} -> {to}: {message}"));
        State = to;
        LastMessage = message;
        if (to is SaleState.Idle or SaleState.Declined or SaleState.Cancelled or SaleState.Faulted or SaleState.StoreAndForward)
            Phase = to.ToString().ToLowerInvariant();
        return commands;
    }
}
