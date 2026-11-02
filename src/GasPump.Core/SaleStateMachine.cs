namespace GasPump.Core;

public sealed record SaleTimeouts(
    TimeSpan Authorization,
    /// <summary>Approved to first lift, card read at the pump. The driver is at the hose; nobody else can cancel for them.</summary>
    TimeSpan NozzleLift,
    TimeSpan MaxFueling,
    TimeSpan Completion,
    TimeSpan Notice,
    /// <summary>Approved to first lift, card or cash taken at the station. Longer, because the cashier holds the sale and can cancel it by hand.</summary>
    TimeSpan NozzleLiftAtStation,
    /// <summary>Card read to PIN or ZIP entered. The driver is at the keypad; a card left in the reader with nobody typing is a walk-away.</summary>
    TimeSpan CodeEntry,
    /// <summary>Flow paused to flow resumed. When it runs out the pump closes the valve and settles for the fuel delivered, so a forgotten nozzle never holds the pump open.</summary>
    TimeSpan PausedFlow)
{
    public static SaleTimeouts Default => new(
        Authorization: TimeSpan.FromSeconds(30),
        NozzleLift: TimeSpan.FromSeconds(120),
        MaxFueling: TimeSpan.FromMinutes(10),
        Completion: TimeSpan.FromSeconds(30),
        Notice: TimeSpan.FromSeconds(30),
        NozzleLiftAtStation: TimeSpan.FromSeconds(300),
        CodeEntry: TimeSpan.FromSeconds(30),
        PausedFlow: TimeSpan.FromSeconds(60));

    public TimeSpan LiftFor(CardSource source) => source == CardSource.Station ? NozzleLiftAtStation : NozzleLift;
}

/// <summary>
/// One sale, one state, one writer. Apply takes an event and returns the
/// commands the outside world should run. Nothing in here touches a thread, a
/// timer, a socket, or a valve. That is what makes it testable with a list.
///
/// Illegal transitions log and stay put. Each state arms one timeout when it
/// is entered and cancels it when it is left. Terminal states arm the notice
/// timeout, and when it fires the screen clears to Idle for the next driver.
/// A sale faulted for a part that needs service clears sooner: when the pump
/// reports the part fixed and the bank has answered.
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

    /// <param name="service">What the station last heard about the pump, so a new sale starts knowing whether the pump can deliver.</param>
    public SaleStateMachine(string saleId, SaleTimeouts? timeouts = null, ServiceStatus? service = null)
    {
        SaleId = saleId;
        _timeouts = timeouts ?? SaleTimeouts.Default;
        Service = service ?? ServiceStatus.Normal;
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
    public bool SpoutInserted { get; private set; }
    public Tender Tender { get; private set; } = Tender.Credit;
    public CardSource Source { get; private set; } = CardSource.PumpInsert;
    public string? FaultCode { get; private set; }
    /// <summary>The dispenser's transaction for this delivery, once a pour ended. Cleared on the pump when the money is settled or queued.</summary>
    public long? TxSeq { get; private set; }
    /// <summary>The pump's conditions as last reported through <see cref="SaleEvent.PumpService"/>.</summary>
    public ServiceStatus Service { get; private set; }

    /// <summary>True while the driver's money is held or requested and no fuel has moved: the sale a newer card may take over.</summary>
    public bool IsOpenAndUntouched => State is SaleState.CardRead or SaleState.Authorizing or SaleState.ReadyToPump && !NozzleOut;

    /// <summary>The keypad entry this tender needs: a PIN for debit, a billing ZIP for credit. Cash needs none.</summary>
    public static (int Digits, string Name, DriverAction Action) CodeFor(Tender tender) => tender switch
    {
        Tender.Debit => (4, "PIN", DriverAction.EnterPin),
        Tender.Credit => (5, "ZIP code", DriverAction.EnterZip),
        _ => (0, "", DriverAction.None),
    };
    private TimeSpan LiftTimeout => _timeouts.LiftFor(Source);
    public Money CashPrepaid { get; private set; }
    public Money ChangeDue { get; private set; }

    /// <summary>The money itemized, as the pump last reported it. Empty until the pump sends a breakdown.</summary>
    public IReadOnlyList<ChargeLine> Lines { get; private set; } = [];

    /// <summary>The lines the completion and the receipt carry. A pump that sent no breakdown gets one fuel line for the money.</summary>
    public IReadOnlyList<ChargeLine> Charges =>
        Lines.Count > 0 ? Lines : Money == Money.Zero ? [] : [new ChargeLine(ChargeLine.Fuel, $"{Grade} {Volume}", Money)];

    /// <summary>True while the bank still owes an answer about the driver's money.</summary>
    public bool HoldUnsettled => Hold is HoldStatus.Requested or HoldStatus.Held or HoldStatus.ReleasePending or HoldStatus.SettlePending;

    public static bool IsTerminal(SaleState s) =>
        s is SaleState.Declined or SaleState.Cancelled or SaleState.Faulted or SaleState.StoreAndForward;

    public SaleSnapshot Snapshot(DateTimeOffset? deadlineAt = null) =>
        new(SaleId, State, CardName, Grade, Ceiling, Volume, Money, Flow, Phase, LastMessage, Hold, End, Notice(), GradeSelected, NozzleOut, SpoutInserted, Tender, Source, FaultCode, CashPrepaid, ChangeDue, deadlineAt, Charges);

    public IReadOnlyList<SaleCommand> Apply(SaleEvent e)
    {
        if (e.SaleId != SaleId)
            return [new SaleCommand.Log(SaleId, $"dropped event for other sale {e.SaleId}")];

        var commands = new List<SaleCommand>(4);

        // Money events are legal in every state. They move Hold. Only Completing also changes State on one.
        // A settled hold is the last thing a repaired pump waits for before its screen clears.
        if (ApplyHoldEvent(e, commands)) return ClearIfRepaired(commands);

        // Service reports are legal in every state.
        if (e is SaleEvent.PumpService ps) return ApplyService(ps, commands);

        // Fault is legal in any state that isn't terminal. Cancel is legal only before fueling starts
        // (CardRead, Authorizing, ReadyToPump).
        switch (e)
        {
            case SaleEvent.Fault f when !IsTerminal(State):
                TxSeq ??= f.TxSeq;
                return FaultSale(f.Reason, f.Code, commands);
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
                Source = cp.Source;
                // Nothing has gone to the bank yet. The keypad comes first: a PIN for debit, a ZIP code for credit.
                return Transition(SaleState.CardRead, commands, $"card {cp.CardName} as {cp.Tender}, {SourceText(cp.Source)}, waiting for {CodeFor(cp.Tender).Name}", timeout: _timeouts.CodeEntry);

            case (SaleState.CardRead, SaleEvent.CodeEntered ce):
            {
                var (digits, name, _) = CodeFor(Tender);
                if (ce.Code.Length != digits || !ce.Code.All(char.IsDigit))
                {
                    // The digits never reach a log. Only the shape of the mistake does.
                    commands.Add(new SaleCommand.Log(SaleId, $"{name} entry refused: {digits} digits expected, {ce.Code.Length} typed; waiting"));
                    return commands;
                }
                Hold = HoldStatus.Requested;
                commands.Add(new SaleCommand.RequestAuthorization(SaleId, CardName!, Grade, Tender, ce.Code));
                return Transition(SaleState.Authorizing, commands, $"{name} entered, asking the bank", timeout: _timeouts.Authorization);
            }

            case (SaleState.CardRead, SaleEvent.Timeout):
                End = EndReason.NoCodeEntered;
                EndDetail = $"no {CodeFor(Tender).Name} was entered within {_timeouts.CodeEntry.TotalSeconds:0} s";
                return Transition(SaleState.Cancelled, commands, "keypad entry timed out, nothing was sent to the bank");

            // The pump is calling: a nozzle came off the boot with no sale behind it. Nothing opens; the screen asks for a card.
            case (SaleState.Idle, SaleEvent.NozzleLifted):
            case (SaleState.CardRead, SaleEvent.NozzleLifted):
            case (SaleState.Authorizing, SaleEvent.NozzleLifted):
                NozzleOut = true;
                commands.Add(new SaleCommand.Log(SaleId, $"nozzle lifted with no authorization ({State}), pump calling"));
                return commands;

            case (SaleState.Idle, SaleEvent.NozzleHung):
            case (SaleState.CardRead, SaleEvent.NozzleHung):
            case (SaleState.Authorizing, SaleEvent.NozzleHung):
                NozzleOut = false;
                SpoutInserted = false;
                commands.Add(new SaleCommand.Log(SaleId, $"nozzle hung with no authorization ({State})"));
                return commands;

            case (SaleState.CardRead, SaleEvent.SpoutInserted):
            case (SaleState.Authorizing, SaleEvent.SpoutInserted):
                SpoutInserted = true;
                return commands;

            case (SaleState.CardRead, SaleEvent.SpoutWithdrawn):
            case (SaleState.Authorizing, SaleEvent.SpoutWithdrawn):
                SpoutInserted = false;
                return commands;

            case (SaleState.Idle, SaleEvent.CashPaid cash):
                CardName = "cash";
                Grade = cash.Grade ?? Grade.Regular;
                GradeSelected = cash.Grade is not null;
                Tender = Tender.Cash;
                Source = CardSource.Station;
                CashPrepaid = cash.Amount;
                Hold = HoldStatus.Requested;
                commands.Add(new SaleCommand.RequestCashAuthorization(SaleId, cash.Amount));
                return Transition(SaleState.Authorizing, commands, $"cash {cash.Amount} taken at the cashier", timeout: _timeouts.Authorization);

            case (SaleState.Authorizing, SaleEvent.Approved a):
                Ceiling = a.Ceiling;
                Token = a.Token;
                Hold = HoldStatus.Held;
                commands.Add(new SaleCommand.ArmPump(SaleId, a.Token, GradeSelected ? Grade : null));
                return Transition(SaleState.ReadyToPump, commands, $"approved, ceiling {a.Ceiling}, {LiftTimeout.TotalSeconds:0} s to lift", timeout: LiftTimeout);

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

            // Before the first pour the driver has as long as the lift timer allows to pick a grade, lift, and insert.
            case (SaleState.ReadyToPump, SaleEvent.GradeSelected gs):
                Grade = gs.Grade;
                GradeSelected = true;
                commands.Add(new SaleCommand.Log(SaleId, $"grade {gs.Grade} selected"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.NozzleLifted):
                NozzleOut = true;
                commands.Add(new SaleCommand.Log(SaleId, "nozzle lifted, waiting for grade, spout, and lever"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.SpoutInserted):
                SpoutInserted = true;
                commands.Add(new SaleCommand.Log(SaleId, "spout inserted in the fill pipe"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.SpoutWithdrawn):
                SpoutInserted = false;
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.NozzleHung):
                // No pour yet, so the sale stays armed. The lift timer still runs.
                NozzleOut = false;
                SpoutInserted = false;
                commands.Add(new SaleCommand.Log(SaleId, "nozzle hung before any pour, still armed"));
                return commands;

            case (SaleState.ReadyToPump, SaleEvent.FlowStarted):
                Phase = "full";
                return Transition(SaleState.Fueling, commands, "first pour, valve open", timeout: _timeouts.MaxFueling);

            case (SaleState.ReadyToPump, SaleEvent.Timeout):
                End = EndReason.NoNozzleLift;
                EndDetail = $"fueling did not start within {LiftTimeout.TotalSeconds:0} s ({SourceText(Source)})";
                return Transition(SaleState.Cancelled, commands, "no pour started, hold reversed", reverse: true);

            case (SaleState.Fueling, SaleEvent.Progress p):
                Volume = p.Volume;
                Money = p.Money;
                if (p.Lines is not null) Lines = p.Lines;
                Flow = p.Flow;
                Phase = p.Phase;
                return commands;

            case (SaleState.Fueling, SaleEvent.FlowPaused fp):
                Flow = FlowRate.Zero;
                Phase = "paused";
                // The idle timer runs beside the max-fueling timer. A squeeze cancels it; silence lets it finish the sale.
                commands.Add(new SaleCommand.ArmTimer(SaleId, SaleState.Fueling, _timeouts.PausedFlow, SaleCommand.IdleTimer));
                commands.Add(new SaleCommand.Log(SaleId, $"flow paused: {fp.Reason}, {Volume} for {Money} so far, finishes in {_timeouts.PausedFlow.TotalSeconds:0} s without a squeeze"));
                return commands;

            case (SaleState.Fueling, SaleEvent.SpoutInserted):
                SpoutInserted = true;
                return commands;

            case (SaleState.Fueling, SaleEvent.SpoutWithdrawn):
                SpoutInserted = false;
                return commands;

            case (SaleState.Fueling, SaleEvent.FlowResumed):
                Phase = "full";
                commands.Add(new SaleCommand.CancelTimer(SaleId, SaleCommand.IdleTimer));
                commands.Add(new SaleCommand.Log(SaleId, "flow resumed"));
                return commands;

            case (SaleState.Fueling, SaleEvent.GradeSelected):
                commands.Add(new SaleCommand.Log(SaleId, "grade change refused after the first pour"));
                return commands;

            case (SaleState.Fueling, SaleEvent.CeilingReached cr):
                Volume = cr.Volume;
                Money = cr.Money;
                if (cr.Lines is not null) Lines = cr.Lines;
                Flow = FlowRate.Zero;
                Phase = "ceiling";
                TxSeq ??= cr.TxSeq;
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "ceiling reached", EndReason.Completed);

            case (SaleState.Fueling, SaleEvent.NozzleHung nh):
                TxSeq ??= nh.TxSeq;
                Flow = FlowRate.Zero;
                Phase = "hung";
                NozzleOut = false;
                SpoutInserted = false;
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "nozzle hung", EndReason.Completed);

            case (SaleState.Fueling, SaleEvent.Timeout { Tag: SaleCommand.IdleTimer }) when Phase != "paused":
                // The squeeze and the timer crossed in the mailbox. Flow is back, so the timer means nothing.
                commands.Add(new SaleCommand.Log(SaleId, "idle timer fired after flow resumed, ignored"));
                return commands;

            case (SaleState.Fueling, SaleEvent.Timeout { Tag: SaleCommand.IdleTimer }):
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, $"no flow for {_timeouts.PausedFlow.TotalSeconds:0} s, valve closed", EndReason.NoFlow);

            case (SaleState.Fueling, SaleEvent.Timeout):
                commands.Add(new SaleCommand.CloseValve(SaleId));
                return Finish(commands, "max fueling time, valve closed", EndReason.MaxFuelingTime);

            // The dispenser's own end-of-delivery report can land after the sale already moved on (a timer finished it first,
            // or the station closed the valve). It carries the transaction number the station still has to clear.
            case (SaleState.Completing or SaleState.StoreAndForward or SaleState.Faulted or SaleState.Cancelled or SaleState.Idle, SaleEvent.NozzleHung late):
                NoteTransaction(late.TxSeq, commands);
                NozzleOut = false;
                SpoutInserted = false;
                commands.Add(new SaleCommand.Log(SaleId, $"delivery end reported in {State}{(late.TxSeq is { } t1 ? $", transaction #{t1}" : "")}"));
                return commands;
            case (SaleState.Completing or SaleState.StoreAndForward or SaleState.Faulted or SaleState.Cancelled or SaleState.Idle, SaleEvent.CeilingReached lateCeiling):
                NoteTransaction(lateCeiling.TxSeq, commands);
                commands.Add(new SaleCommand.Log(SaleId, $"ceiling reported in {State}{(lateCeiling.TxSeq is { } t2 ? $", transaction #{t2}" : "")}"));
                return commands;

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
                ReleasePump(commands);
                Transition(SaleState.Idle, commands, Tender == Tender.Cash ? $"cash sale {cs.Final}, change {ChangeDue}" : $"completion sent for {cs.Final}");
                return true;

            case SaleEvent.CompletionSent cs:
                Hold = HoldStatus.Settled;
                SettleCash(cs.Final);
                ReleasePump(commands);
                commands.Add(new SaleCommand.Log(SaleId, $"completion for {cs.Final} posted late, hold settled"));
                return true;

            case SaleEvent.CompletionQueued cq when State == SaleState.Completing:
                Hold = HoldStatus.SettlePending;
                ReleasePump(commands);
                Transition(SaleState.StoreAndForward, commands, $"host down, completion for {cq.Final} queued", timeout: _timeouts.Notice);
                return true;

            case SaleEvent.CompletionQueued:
                if (Hold != HoldStatus.Settled) Hold = HoldStatus.SettlePending;
                ReleasePump(commands);
                commands.Add(new SaleCommand.Log(SaleId, "completion queued"));
                return true;
        }
        return false;
    }

    /// <summary>
    /// The pump stopped. Fuel that moved is settled to the metered amount; a
    /// sale with no fuel gives the hold back. The code names the part, so the
    /// notice can name the driver's next step.
    /// </summary>
    /// <summary>
    /// The money for the delivery is settled or safely queued, so the pump may
    /// drop its transaction and arm again. Once per transaction: the number
    /// is forgotten here so a late duplicate settlement sends nothing.
    /// </summary>
    private void ReleasePump(List<SaleCommand> commands)
    {
        if (TxSeq is not { } tx) return;
        commands.Add(new SaleCommand.ClearTransaction(SaleId, tx));
        TxSeq = null;
    }

    /// <summary>
    /// The dispenser named the transaction for this delivery. Remember it, and
    /// if the money is already dealt with (settled, queued, released, or never
    /// asked for) clear it now; otherwise the completion will.
    /// </summary>
    private void NoteTransaction(long? tx, List<SaleCommand> commands)
    {
        if (tx is null) return;
        TxSeq ??= tx;
        if (Hold is not (HoldStatus.Requested or HoldStatus.Held)) ReleasePump(commands);
    }

    private IReadOnlyList<SaleCommand> FaultSale(string reason, string? code, List<SaleCommand> commands)
    {
        End = EndReason.PumpFault;
        EndDetail = reason;
        FaultCode = code;
        if (Volume > Volume.Zero)
        {
            commands.Add(new SaleCommand.SendCompletion(SaleId, Money, Charges));
            return Transition(SaleState.Faulted, commands, $"FAULT: {reason}, settling {Money} for fuel already dispensed", closeValve: true);
        }
        var result = Transition(SaleState.Faulted, commands, $"FAULT: {reason}", closeValve: true, reverse: Hold is HoldStatus.Held or HoldStatus.Requested);
        // No money is owed for this delivery (nothing metered, or the meter lied), so the pump's record can go now.
        ReleasePump(commands);
        return result;
    }

    /// <summary>
    /// The pump's service status changed. Two reactions. Before the first
    /// pour, an out-of-service pump ends the sale and gives the hold back,
    /// because the driver must never be told to squeeze a lever that cannot
    /// deliver. After a fault, the repair of the part that caused it clears the
    /// screen, once the bank has answered.
    /// </summary>
    private IReadOnlyList<SaleCommand> ApplyService(SaleEvent.PumpService ps, List<SaleCommand> commands)
    {
        Service = ps.Service;
        commands.Add(new SaleCommand.Log(SaleId, $"pump {Service}"));
        if (!Service.InService && State is SaleState.Authorizing or SaleState.ReadyToPump)
        {
            var code = Service.Codes[0];
            return FaultSale($"pump out of service: {DiagnosticCodes.Find(code)?.Part ?? code}", code, commands);
        }
        if (RepairedAfterFault && HoldUnsettled)
            commands.Add(new SaleCommand.Log(SaleId, $"pump back in service; screen held until the bank answers, hold is {Hold}"));
        return ClearIfRepaired(commands);
    }

    /// <summary>This sale faulted for a part that needs service, and the pump no longer reports that part.</summary>
    private bool RepairedAfterFault =>
        State == SaleState.Faulted
        && FaultCode is { } code
        && DiagnosticCodes.Find(code) is { OutOfService: true }
        && !Service.Holds(code);

    /// <summary>The same rule as the notice timeout: the screen clears only once the bank has said where the money went.</summary>
    private IReadOnlyList<SaleCommand> ClearIfRepaired(List<SaleCommand> commands) =>
        RepairedAfterFault && !HoldUnsettled
            ? Transition(SaleState.Idle, commands, "pump back in service")
            : commands;

    /// <summary>What the next driver reads while the pump cannot deliver: which part, and who fixes it.</summary>
    private DriverNotice OutOfServiceNotice()
    {
        var code = DiagnosticCodes.Find(Service.Codes[0]);
        var repair = code is null ? "" : ServiceActions.Describe(code.ClearedBy);
        return new($"Out of service: {code?.Part ?? Service.Codes[0]}", Join(repair, "Please use another pump."), DriverAction.UseAnotherPump);
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
            var result = Transition(SaleState.Cancelled, commands, $"{why}, no fuel, hold reversed", reverse: true);
            ReleasePump(commands);
            return result;
        }
        commands.Add(new SaleCommand.SendCompletion(SaleId, Money, Charges));
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
            case SaleState.Idle when !Service.InService:
                return OutOfServiceNotice();
            case SaleState.Idle when End == EndReason.PumpFault && FaultCode is { } fc && DiagnosticCodes.Find(fc) is { OutOfService: true }:
                // The last sale faulted for a part that was then repaired. Say so, and where that driver's money went.
                return new("Pump back in service", Join(holdLine, "Insert or tap your card, or pay the cashier, for the next sale."), DriverAction.None);
            case SaleState.Idle when Hold == HoldStatus.Settled:
                // The last sale's receipt stays on the screen until the next driver starts or the screen is reset.
                return new($"Thank you: {Volume} for {Money}", Join(holdLine, "Insert or tap your card, or pay the cashier, for the next sale."), DriverAction.Leave);
            case SaleState.Idle when NozzleOut:
                // The pump is calling. The driver has the nozzle in hand and no authorization yet.
                return new("Nozzle lifted. Insert or tap your card, or pay the cashier, to begin", "Nothing flows until a payment is approved. Hang the nozzle back up to cancel.", DriverAction.None);
            case SaleState.Idle:
                return new("Insert or tap your card, or pay the cashier", "Select a grade after approval.", DriverAction.None);
            case SaleState.CardRead:
            {
                var (digits, name, action) = CodeFor(Tender);
                var what = Tender == Tender.Debit ? $"Enter your {digits}-digit {name}" : $"Enter your {digits}-digit billing {name}";
                return new(what, $"Press Enter when done. The sale cancels after {_timeouts.CodeEntry.TotalSeconds:0} s with nothing typed. Nothing has been sent to your bank yet.", action);
            }
            case SaleState.Authorizing when Tender == Tender.Cash:
                return new($"Cash {CashPrepaid} received", "Setting the pump to your amount.", DriverAction.Wait);
            case SaleState.Authorizing:
                return new($"Contacting your bank ({Tender})", $"Most approvals take a few seconds. We stop waiting after {_timeouts.Authorization.TotalSeconds:0} s.", DriverAction.Wait);
            case SaleState.ReadyToPump:
            {
                var leave = Source == CardSource.Station
                    ? $"If you leave, the cashier can cancel, or the pump cancels after {LiftTimeout.TotalSeconds:0} s and releases the hold."
                    : $"If you leave, the pump cancels after {LiftTimeout.TotalSeconds:0} s and releases the hold. A new card at this pump cancels it at once.";
                var step = !GradeSelected ? "Select a grade."
                    : !NozzleOut ? "Lift the nozzle."
                    : !SpoutInserted ? "Insert the nozzle into your fill pipe."
                    : "Squeeze the lever to begin.";
                return new($"Approved for up to {Ceiling}", Join(step, Tender == Tender.Cash ? holdLine : "", leave), DriverAction.LiftNozzle);
            }
            case SaleState.Fueling when Phase == "paused":
                return new($"Paused at {Money}", Join(SpoutInserted ? "Squeeze the lever to continue." : "Insert the nozzle again and squeeze the lever to continue.", $"Hang up the nozzle to finish, or the pump finishes by itself after {_timeouts.PausedFlow.TotalSeconds:0} s."), DriverAction.HangToStop);
            case SaleState.Fueling:
                return new("Fueling", "Let go of the lever to pause. Hang up the nozzle to finish. The pump stops by itself at your limit.", DriverAction.HangToStop);
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
            EndReason.NoCodeEntered => new("Sale cancelled", Join(Sentence(EndDetail), "Nothing was sent to your bank.", "Start again when ready."), DriverAction.TryAgain),
            EndReason.DriverCancelled => new("Sale cancelled", Join(Sentence(EndDetail), holdLine, "Start again when ready."), DriverAction.TryAgain),
            EndReason.PumpFault => FaultNotice(holdLine),
            EndReason.SecurityRefusal => new("Pump refused the sale", Join(Sentence(EndDetail), holdLine, "Please use another pump and tell the attendant."), DriverAction.SeeAttendant),
            EndReason.MaxFuelingTime => new("Time limit reached", Join("The pump stopped at its time limit.", holdLine), DriverAction.Leave),
            _ => new(State.ToString(), holdLine, DriverAction.None),
        };
    }

    public static string SourceText(CardSource s) => s switch
    {
        CardSource.PumpInsert => "inserted at the pump",
        CardSource.PumpTap => "tapped at the pump",
        _ => "at the station",
    };

    /// <summary>A fault names its part so the driver hears the right next step: a dry tank wants another pump, a broken hose wants the attendant.</summary>
    private DriverNotice FaultNotice(string holdLine)
    {
        var delivered = Volume > Volume.Zero ? $"You were charged only for the {Volume} delivered." : "No fuel was dispensed.";
        return FaultCode switch
        {
            "PUMP.STP.NOFLOW" => new("Fuel supply stopped", Join("This pump has no fuel pressure.", delivered, holdLine, "Please use another pump."), DriverAction.UseAnotherPump),
            "PUMP.HOSE.BREAKAWAY" => new("Hose breakaway", Join("The hose coupling separated and sealed.", delivered, holdLine, "Do not drive away. See the attendant."), DriverAction.SeeAttendant),
            "PUMP.MTR.NOPULSE" => new("Meter fault", Join("The meter stopped counting.", delivered, holdLine, "See the attendant before you leave."), DriverAction.SeeAttendant),
            "LINK.DISPENSER.LOST" => new("Pump offline", Join("The station lost contact with this pump.", delivered, holdLine, "See the attendant before you leave."), DriverAction.SeeAttendant),
            _ => new("Pump out of service", Join(Sentence(EndDetail), holdLine, "Please use another pump."), DriverAction.UseAnotherPump),
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

    /// <summary>Capitalizes a reason and ends it with a period. Returns an empty string for null or empty input.</summary>
    private static string Sentence(string? s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..].TrimEnd('.') + ".";

    private static string Join(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0));
}
