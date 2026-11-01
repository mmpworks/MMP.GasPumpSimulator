namespace GasPump.Core;

public sealed record SaleTimeouts(
    TimeSpan Authorization,
    TimeSpan NozzleLift,
    TimeSpan MaxFueling,
    TimeSpan Completion,
    TimeSpan Notice)
{
    public static SaleTimeouts Default => new(
        Authorization: TimeSpan.FromSeconds(30),
        NozzleLift: TimeSpan.FromSeconds(120),
        MaxFueling: TimeSpan.FromMinutes(10),
        Completion: TimeSpan.FromSeconds(30),
        Notice: TimeSpan.FromSeconds(30));
}

/// <summary>
/// One sale, one state, one writer. Apply takes an event and returns the
/// commands the outside world should run. Nothing in here touches a thread, a
/// timer, a socket, or a valve. That is what makes it testable with a list.
///
/// Illegal transitions log and stay put. Each state arms one timeout when it
/// is entered and cancels it when it is left. Terminal states arm the notice
/// timeout, and when it fires the screen clears to Idle for the next driver.
///
/// The sale is a saga. Each step toward "fuel for money" has a compensating
/// step back: a hold is reversed, a completion is queued. The machine tracks
/// where the money is in <see cref="Hold"/> apart from where the pump is in
/// <see cref="State"/>, because the two drift apart when the bank link runs
/// late, and the driver has to be told about the gap.
/// </summary>
public sealed class SaleStateMachine
{
    private readonly SaleTimeouts _timeouts;

    public SaleStateMachine(string saleId, SaleTimeouts? timeouts = null)
    {
        SaleId = saleId;
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
    public HoldStatus Hold { get; private set; }
    public EndReason End { get; private set; }
    public string? EndDetail { get; private set; }
    public bool GradeSelected { get; private set; }
    public bool NozzleOut { get; private set; }
    public bool NozzleSeated { get; private set; }
    public Tender Tender { get; private set; } = Tender.Credit;
    public Money CashPrepaid { get; private set; }
    public Money ChangeDue { get; private set; }

    /// <summary>True while the bank still owes an answer about the driver's money.</summary>
    public bool HoldUnsettled => Hold is HoldStatus.Requested or HoldStatus.Held or HoldStatus.ReleasePending or HoldStatus.SettlePending;

    public static bool IsTerminal(SaleState s) =>
        s is SaleState.Declined or SaleState.Cancelled or SaleState.Faulted or SaleState.StoreAndForward;

    public SaleSnapshot Snapshot(DateTimeOffset? deadlineAt = null) =>
        new(SaleId, State, CardName, Grade, Ceiling, Volume, Money, Flow, Phase, LastMessage, Hold, End, Notice(), GradeSelected, NozzleOut, NozzleSeated, Tender, CashPrepaid, ChangeDue, deadlineAt);

    public IReadOnlyList<SaleCommand> Apply(SaleEvent e)
    {
        if (e.SaleId != SaleId)
            return [new SaleCommand.Log(SaleId, $"dropped event for other sale {e.SaleId}")];

        var commands = new List<SaleCommand>(4);

        // Money events are legal in every state. They move Hold, and only Completing cares about them for State.
        if (ApplyHoldEvent(e, commands)) return commands;

        // Cancel and Fault are legal from anywhere that is not already terminal.
        switch (e)
        {
            case SaleEvent.Fault f when !IsTerminal(State):
                End = EndReason.PumpFault;
                EndDetail = f.Reason;
                if (Volume > Volume.Zero)
                {
                    // Fuel moved before the fault. The driver owes for it, so settle to the metered amount rather than reverse.
                    commands.Add(new SaleCommand.SendCompletion(SaleId, Money));
                    return Transition(SaleState.Faulted, commands, $"FAULT: {f.Reason}, settling {Money} for fuel already dispensed", closeValve: true);
                }
                return Transition(SaleState.Faulted, commands, $"FAULT: {f.Reason}", closeValve: true, reverse: Hold is HoldStatus.Held or HoldStatus.Requested);
            case SaleEvent.Cancel c when State is SaleState.CardRead or SaleState.Authorizing or SaleState.ReadyToPump:
                End = EndReason.DriverCancelled;
                EndDetail = c.Reason;
                return Transition(SaleState.Cancelled, commands, $"cancelled: {c.Reason}", reverse: Hold is HoldStatus.Held or HoldStatus.Requested);
            case SaleEvent.Timeout t when t.InState != State:
                commands.Add(new SaleCommand.Log(SaleId, $"stale timeout for {t.InState} ignored in {State}"));
                return commands;
            case SaleEvent.Timeout when IsTerminal(State) && HoldUnsettled:
                // The driver may still be reading. Keep the screen until the bank has confirmed where the money went.
                commands.Add(new SaleCommand.ArmTimer(SaleId, State, _timeouts.Notice));
                commands.Add(new SaleCommand.Log(SaleId, $"screen held, hold is {Hold}"));
                return commands;
            case SaleEvent.Timeout when IsTerminal(State):
                return Transition(SaleState.Idle, commands, "screen cleared for the next driver");
        }

        switch (State, e)
        {
            case (SaleState.Idle, SaleEvent.CardPresented cp):
                CardName = cp.CardName;
                Grade = cp.Grade ?? Grade.Regular;
                GradeSelected = cp.Grade is not null;
                Tender = cp.Tender;
                Hold = HoldStatus.Requested;
                commands.Add(new SaleCommand.RequestAuthorization(SaleId, cp.CardName, Grade, cp.Tender));
                return Transition(SaleState.Authorizing, commands, $"card {cp.CardName} as {cp.Tender}", timeout: _timeouts.Authorization);

            case (SaleState.Idle, SaleEvent.CashPaid cash):
                CardName = "cash";
                Grade = cash.Grade ?? Grade.Regular;
                GradeSelected = cash.Grade is not null;
                Tender = Tender.Cash;
                CashPrepaid = cash.Amount;
                Hold = HoldStatus.Requested;
                commands.Add(new SaleCommand.RequestCashAuthorization(SaleId, cash.Amount));
                return Transition(SaleState.Authorizing, commands, $"cash {cash.Amount} taken at the cashier", timeout: _timeouts.Authorization);

            case (SaleState.Authorizing, SaleEvent.Approved a):
                Ceiling = a.Ceiling;
                Token = a.Token;
                Hold = HoldStatus.Held;
                commands.Add(new SaleCommand.ArmPump(SaleId, a.Token, GradeSelected ? Grade : null));
                return Transition(SaleState.ReadyToPump, commands, $"approved, ceiling {a.Ceiling}", timeout: _timeouts.NozzleLift);

            case (SaleState.Authorizing, SaleEvent.Declined d):
                Hold = HoldStatus.None;
                End = EndReason.CardDeclined;
                EndDetail = d.Message;
                return Transition(SaleState.Declined, commands, $"declined {d.ReasonCode}: {d.Message}");

            case (SaleState.Authorizing, SaleEvent.AuthorizationTimedOut):
            case (SaleState.Authorizing, SaleEvent.Timeout):
                // The bank may still have heard us. Reverse whatever it holds; the reversal is idempotent on their side.
                End = EndReason.BankSilent;
                EndDetail = "the bank did not answer in time";
                return Transition(SaleState.Declined, commands, "authorization timed out", reverse: true);

            case (SaleState.Authorizing, SaleEvent.AuthorizationUnreachable u):
                // The connection was refused. Nothing reached the bank, so there is no hold and nothing to reverse.
                Hold = HoldStatus.None;
                End = EndReason.BankDown;
                EndDetail = u.Reason;
                return Transition(SaleState.Declined, commands, $"bank link down: {u.Reason}, nothing sent");

            case (SaleState.ReadyToPump, SaleEvent.PumpArmed pa):
                Ceiling = pa.Ceiling;
                Phase = "armed";
                commands.Add(new SaleCommand.Log(SaleId, $"pump armed, ceiling {pa.Ceiling}, max {pa.MaxVolume}"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.PumpRefused pr):
                End = EndReason.SecurityRefusal;
                EndDetail = pr.Reason;
                return Transition(SaleState.Faulted, commands, $"pump refused: {pr.Reason}", reverse: true);

            // Before the first drop the driver can take as long as the lift timer allows to pick a grade, lift, and seat.
            case (SaleState.ReadyToPump, SaleEvent.GradeSelected gs):
                Grade = gs.Grade;
                GradeSelected = true;
                commands.Add(new SaleCommand.Log(SaleId, $"grade {gs.Grade} selected"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.NozzleLifted):
                NozzleOut = true;
                commands.Add(new SaleCommand.Log(SaleId, "nozzle lifted, waiting for grade, seat, and trigger"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.NozzleSeated):
                NozzleSeated = true;
                commands.Add(new SaleCommand.Log(SaleId, "nozzle seated in fill point"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.NozzleUnseated):
                NozzleSeated = false;
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.NozzleHung):
                // No pour yet, so the sale stays armed. The lift timer still runs.
                NozzleOut = false;
                NozzleSeated = false;
                commands.Add(new SaleCommand.Log(SaleId, "nozzle hung before any pour, still armed"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.FlowStarted):
                Phase = "full";
                return Transition(SaleState.Fueling, commands, "first pour, valve open", timeout: _timeouts.MaxFueling);

            case (SaleState.ReadyToPump, SaleEvent.Timeout):
                End = EndReason.NoNozzleLift;
                EndDetail = $"fueling did not start within {_timeouts.NozzleLift.TotalSeconds:0} s";
                return Transition(SaleState.Cancelled, commands, "no pour started, hold reversed", reverse: true);

            case (SaleState.Fueling, SaleEvent.Progress p):
                Volume = p.Volume;
                Money = p.Money;
                Flow = p.Flow;
                Phase = p.Phase;
                return commands;

            case (SaleState.Fueling, SaleEvent.FlowPaused fp):
                Flow = FlowRate.Zero;
                Phase = "paused";
                commands.Add(new SaleCommand.Log(SaleId, $"flow paused: {fp.Reason}, {Volume} for {Money} so far"));
                return commands;

            case (SaleState.Fueling, SaleEvent.NozzleSeated):
                NozzleSeated = true;
                return commands;

            case (SaleState.Fueling, SaleEvent.NozzleUnseated):
                NozzleSeated = false;
                return commands;

            case (SaleState.Fueling, SaleEvent.FlowResumed):
                Phase = "full";
                commands.Add(new SaleCommand.Log(SaleId, "flow resumed"));
                return commands;

            case (SaleState.Fueling, SaleEvent.GradeSelected):
                commands.Add(new SaleCommand.Log(SaleId, "grade change refused after the first pour"));
                return commands;

            case (SaleState.Fueling, SaleEvent.CeilingReached cr):
                Volume = cr.Volume;
                Money = cr.Money;
                Flow = FlowRate.Zero;
                Phase = "ceiling";
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "ceiling reached", EndReason.Completed);

            case (SaleState.Fueling, SaleEvent.NozzleHung):
                Flow = FlowRate.Zero;
                Phase = "hung";
                NozzleOut = false;
                NozzleSeated = false;
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "nozzle hung", EndReason.Completed);

            case (SaleState.Fueling, SaleEvent.Timeout):
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "max fueling time, valve closed", EndReason.MaxFuelingTime);

            case (SaleState.Completing, SaleEvent.Timeout):
                // The completion is in flight and late. The gateway will queue it if the host never answers; if the
                // host does answer, CompletionSent lands in StoreAndForward and settles the hold there.
                Hold = HoldStatus.SettlePending;
                return Transition(SaleState.StoreAndForward, commands, "completion late, will post when the bank answers", timeout: _timeouts.Notice);

            default:
                commands.Add(new SaleCommand.Log(SaleId, $"illegal: {e.GetType().Name} in {State}, ignored"));
                return commands;
        }
    }

    /// <summary>
    /// The saga's compensation table. Returns true when the event was a money
    /// event and has been handled. A late answer from the bank is the case that
    /// matters: the pump has already told the driver "no", so an approval that
    /// arrives now must be reversed, never armed.
    /// </summary>
    private bool ApplyHoldEvent(SaleEvent e, List<SaleCommand> commands)
    {
        switch (e)
        {
            case SaleEvent.Approved late when State != SaleState.Authorizing:
                // Late approval after we gave up. The bank holds money for a sale we already ended. Give it back.
                Hold = HoldStatus.Held;
                commands.Add(new SaleCommand.Reverse(SaleId));
                commands.Add(new SaleCommand.Log(SaleId, $"LATE approval {late.Ceiling} arrived in {State}; token discarded, hold reversed (saga compensation)"));
                return true;

            case SaleEvent.Declined when State != SaleState.Authorizing:
                if (Hold == HoldStatus.Requested) Hold = HoldStatus.None;
                commands.Add(new SaleCommand.Log(SaleId, $"late decline arrived in {State}, nothing held"));
                return true;

            case SaleEvent.AuthorizationTimedOut or SaleEvent.AuthorizationUnreachable when State != SaleState.Authorizing:
                commands.Add(new SaleCommand.Log(SaleId, $"late network failure arrived in {State}, ignored"));
                return true;

            case SaleEvent.Reversed when Tender == Tender.Cash:
                Hold = HoldStatus.Released;
                ChangeDue = CashPrepaid;
                commands.Add(new SaleCommand.Log(SaleId, $"cash refund {CashPrepaid} due at the cashier"));
                return true;

            case SaleEvent.Reversed:
                Hold = HoldStatus.Released;
                commands.Add(new SaleCommand.Log(SaleId, "hold released by the bank"));
                return true;

            case SaleEvent.ReversalQueued:
                Hold = HoldStatus.ReleasePending;
                commands.Add(new SaleCommand.Log(SaleId, "bank unreachable, release queued, will post when the link returns"));
                return true;

            case SaleEvent.CompletionSent cs when State == SaleState.Completing:
                Hold = HoldStatus.Settled;
                SettleCash(cs.Final);
                Transition(SaleState.Idle, commands, Tender == Tender.Cash ? $"cash sale {cs.Final}, change {ChangeDue}" : $"completion sent for {cs.Final}");
                return true;

            case SaleEvent.CompletionSent cs:
                Hold = HoldStatus.Settled;
                SettleCash(cs.Final);
                commands.Add(new SaleCommand.Log(SaleId, $"completion for {cs.Final} posted late, hold settled"));
                return true;

            case SaleEvent.CompletionQueued cq when State == SaleState.Completing:
                Hold = HoldStatus.SettlePending;
                Transition(SaleState.StoreAndForward, commands, $"host down, completion for {cq.Final} queued", timeout: _timeouts.Notice);
                return true;

            case SaleEvent.CompletionQueued:
                if (Hold != HoldStatus.Settled) Hold = HoldStatus.SettlePending;
                commands.Add(new SaleCommand.Log(SaleId, "completion queued"));
                return true;
        }
        return false;
    }

    /// <summary>A cash sale settles at the till: whatever was prepaid and not pumped comes back as change.</summary>
    private void SettleCash(Money final)
    {
        if (Tender != Tender.Cash) return;
        ChangeDue = CashPrepaid - Money.Min(final, CashPrepaid);
    }

    private IReadOnlyList<SaleCommand> Finish(List<SaleCommand> commands, string why, EndReason end)
    {
        End = end;
        if (Volume == Volume.Zero)
        {
            // No fuel moved. A completion for zero is wrong; the hold must be reversed in full.
            End = EndReason.DriverCancelled;
            EndDetail = "no fuel was dispensed";
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
        // Terminal states show the notice for a while, then clear for the next driver.
        if (timeout is null && IsTerminal(to)) timeout = _timeouts.Notice;
        if (timeout is { } t) commands.Add(new SaleCommand.ArmTimer(SaleId, to, t));
        commands.Add(new SaleCommand.Log(SaleId, $"{State} -> {to}: {message}"));
        State = to;
        LastMessage = message;
        if (to is SaleState.Idle || IsTerminal(to))
            Phase = to.ToString().ToLowerInvariant();
        return commands;
    }

    /// <summary>
    /// The screen text, derived from state, hold, and end reason. A pure
    /// function of the machine, so the same facts always produce the same words.
    /// </summary>
    public DriverNotice Notice()
    {
        var holdLine = Tender == Tender.Cash ? CashLine() : Hold switch
        {
            HoldStatus.Released => "Nothing was charged to your card.",
            HoldStatus.ReleasePending => "A temporary hold may show on your card. It releases automatically when our bank link returns.",
            HoldStatus.Requested or HoldStatus.Held => "Any temporary hold on your card is being released.",
            HoldStatus.Settled => $"Your card was charged {Money}.",
            HoldStatus.SettlePending => $"Your card will be charged {Money} when our bank link returns.",
            _ => "",
        };

        switch (State)
        {
            case SaleState.Idle when Hold == HoldStatus.Settled:
                // The last sale's receipt stays on the screen until the next driver starts or the screen is reset.
                return new($"Thank you: {Volume} for {Money}", Join(holdLine, "Insert or tap your card, or pay the cashier, for the next sale."), DriverAction.Leave);
            case SaleState.Idle:
                return new("Insert or tap your card, or pay the cashier", "Select a grade after approval.", DriverAction.None);
            case SaleState.Authorizing when Tender == Tender.Cash:
                return new($"Cash {CashPrepaid} received", "Setting the pump to your amount.", DriverAction.Wait);
            case SaleState.Authorizing:
                return new($"Contacting your bank ({Tender})", $"Most approvals take a few seconds. We stop waiting after {_timeouts.Authorization.TotalSeconds:0} s.", DriverAction.Wait);
            case SaleState.ReadyToPump:
            {
                var leave = $"If you leave, the pump cancels after {_timeouts.NozzleLift.TotalSeconds:0} s and releases the hold.";
                var step = !GradeSelected ? "Select a grade."
                    : !NozzleOut ? "Lift the nozzle."
                    : !NozzleSeated ? "Put the nozzle in your tank."
                    : "Squeeze the trigger to begin.";
                return new($"Approved for up to {Ceiling}", Join(step, Tender == Tender.Cash ? holdLine : "", leave), DriverAction.LiftNozzle);
            }
            case SaleState.Fueling when Phase == "paused":
                return new($"Paused at {Money}", Join(NozzleSeated ? "Squeeze the trigger to continue." : "Put the nozzle back in the tank and squeeze to continue.", "Hang up the nozzle to finish."), DriverAction.HangToStop);
            case SaleState.Fueling:
                return new("Fueling", "Let go of the trigger to pause. Hang up the nozzle to finish. The pump stops by itself at your limit.", DriverAction.HangToStop);
            case SaleState.Completing:
                return new($"Finishing: {Money}", "Settling with your bank. This usually takes a few seconds.", DriverAction.Wait);
            case SaleState.StoreAndForward:
                return new($"Done: {Volume} for {Money}", $"Our bank link is slow or down. {holdLine} Take your receipt. You may go.", DriverAction.Leave);
        }

        // Terminal failures. Headline says what happened; detail says where the money is and what to do.
        return End switch
        {
            EndReason.CardDeclined => new("Card declined", Join(Sentence(EndDetail), holdLine, "Use a different card, or see the attendant."), DriverAction.UseAnotherCard),
            EndReason.BankSilent => new("Bank did not answer", Join("No fuel was dispensed.", holdLine, "Try again here, or use another pump."), DriverAction.TryAgain),
            EndReason.BankDown => new("Bank link is down", "No fuel was dispensed. Nothing was sent to your bank and nothing was charged. Pay the cashier in cash, try again in a few minutes, or use another station.", DriverAction.TryAgain),
            EndReason.NoNozzleLift => new("Sale cancelled", Join("The nozzle was not lifted in time.", holdLine, "Start again when ready."), DriverAction.TryAgain),
            EndReason.DriverCancelled => new("Sale cancelled", Join(Sentence(EndDetail), holdLine, "Start again when ready."), DriverAction.TryAgain),
            EndReason.PumpFault => new("Pump out of service", Join(Sentence(EndDetail), holdLine, "Please use another pump."), DriverAction.UseAnotherPump),
            EndReason.SecurityRefusal => new("Pump refused the sale", Join(Sentence(EndDetail), holdLine, "Please use another pump and tell the attendant."), DriverAction.SeeAttendant),
            EndReason.MaxFuelingTime => new("Time limit reached", Join("The pump stopped at its time limit.", holdLine), DriverAction.Leave),
            _ => new(State.ToString(), holdLine, DriverAction.None),
        };
    }

    /// <summary>Where the cash is. The till owes change after a short pump, or the whole amount after no pump.</summary>
    private string CashLine() => Hold switch
    {
        HoldStatus.Requested or HoldStatus.Held => $"You prepaid {CashPrepaid} in cash.",
        HoldStatus.Settled when ChangeDue > Money.Zero => $"You prepaid {CashPrepaid}. Collect {ChangeDue} change at the cashier.",
        HoldStatus.Settled => $"Paid in full from your {CashPrepaid} cash.",
        HoldStatus.Released => $"Collect your {CashPrepaid} refund at the cashier.",
        _ => "",
    };

    /// <summary>Capitalise a reason and end it with a period, or return empty for nothing.</summary>
    private static string Sentence(string? s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..].TrimEnd('.') + ".";

    private static string Join(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0));
}
