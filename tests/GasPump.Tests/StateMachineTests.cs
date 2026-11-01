using GasPump.Core;
using Xunit;

namespace GasPump.Tests;

public class StateMachineTests
{
    private static SaleStateMachine Machine() => new("S1");

    /// <summary>Card, approval, and the driver's hand sequence up to the first pour.</summary>
    private static SaleStateMachine Fueling(Grade? preselected = null)
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", preselected));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        if (preselected is null) m.Apply(new SaleEvent.GradeSelected("S1", Grade.Regular));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        m.Apply(new SaleEvent.NozzleSeated("S1"));
        m.Apply(new SaleEvent.FlowStarted("S1"));
        Assert.Equal(SaleState.Fueling, m.State);
        return m;
    }

    [Fact]
    public void Happy_path_emits_the_right_commands_in_order()
    {
        var m = Machine();
        var c1 = m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Plus));
        Assert.Equal(SaleState.Authorizing, m.State);
        Assert.Equal(HoldStatus.Requested, m.Hold);
        Assert.Contains(c1, c => c is SaleCommand.RequestAuthorization { Tender: Tender.Credit });
        Assert.Contains(c1, c => c is SaleCommand.ArmTimer { State: SaleState.Authorizing });

        var c2 = m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(30), "tok"));
        Assert.Equal(SaleState.ReadyToPump, m.State);
        Assert.Equal(HoldStatus.Held, m.Hold);
        Assert.Contains(c2, c => c is SaleCommand.ArmPump { Token: "tok", Grade: Grade.Plus });

        m.Apply(new SaleEvent.PumpArmed("S1", Money.FromDollars(30), Volume.FromGallons(50)));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Equal(SaleState.ReadyToPump, m.State);   // a lift is not a pour
        m.Apply(new SaleEvent.NozzleSeated("S1"));
        var c3 = m.Apply(new SaleEvent.FlowStarted("S1"));
        Assert.Equal(SaleState.Fueling, m.State);
        Assert.Contains(c3, c => c is SaleCommand.ArmTimer { State: SaleState.Fueling });

        m.Apply(new SaleEvent.Progress("S1", Volume.FromGallons(1), new Money(350), FlowRate.GallonsPerMinute(10), "full"));
        var c4 = m.Apply(new SaleEvent.CeilingReached("S1", Volume.FromGallons(8.574m), new Money(3000)));
        Assert.Equal(SaleState.Completing, m.State);
        Assert.Contains(c4, c => c is SaleCommand.CloseValve);
        Assert.Contains(c4, c => c is SaleCommand.SendCompletion { Final.Cents: 3000 });

        m.Apply(new SaleEvent.CompletionSent("S1", new Money(3000)));
        Assert.Equal(SaleState.Idle, m.State);
        Assert.Equal(HoldStatus.Settled, m.Hold);
    }

    [Fact]
    public void Grade_is_picked_at_the_pump_when_the_reader_did_not_take_one()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good"));
        var c = m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        Assert.Contains(c, x => x is SaleCommand.ArmPump { Grade: null });
        Assert.False(m.GradeSelected);
        Assert.Contains("Select a grade", m.Notice().Detail);

        m.Apply(new SaleEvent.GradeSelected("S1", Grade.Premium));
        Assert.True(m.GradeSelected);
        Assert.Equal(Grade.Premium, m.Grade);
        Assert.Contains("Lift the nozzle", m.Notice().Detail);
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Contains("in your tank", m.Notice().Detail);
        m.Apply(new SaleEvent.NozzleSeated("S1"));
        Assert.Contains("Squeeze the trigger", m.Notice().Detail);
    }

    [Fact]
    public void Trigger_release_pauses_and_squeeze_resumes_without_ending_the_sale()
    {
        var m = Fueling();
        m.Apply(new SaleEvent.Progress("S1", Volume.FromGallons(2), new Money(700), FlowRate.GallonsPerMinute(10), "full"));
        m.Apply(new SaleEvent.FlowPaused("S1", "trigger released"));
        Assert.Equal(SaleState.Fueling, m.State);
        Assert.Equal("paused", m.Phase);
        Assert.Equal(FlowRate.Zero, m.Flow);
        Assert.StartsWith("Paused at $7.00", m.Notice().Headline);
        m.Apply(new SaleEvent.FlowResumed("S1"));
        Assert.Equal(SaleState.Fueling, m.State);
        Assert.Equal("full", m.Phase);
    }

    [Fact]
    public void Hang_before_any_pour_keeps_the_sale_armed()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        var c = m.Apply(new SaleEvent.NozzleHung("S1"));
        Assert.Equal(SaleState.ReadyToPump, m.State);
        Assert.DoesNotContain(c, x => x is SaleCommand.Reverse);
        Assert.False(m.NozzleOut);
    }

    [Fact]
    public void Illegal_events_are_logged_and_ignored()
    {
        var m = Machine();
        var c = m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Equal(SaleState.Idle, m.State);
        Assert.Single(c);
        Assert.Contains("illegal", ((SaleCommand.Log)c[0]).Message);
    }

    [Fact]
    public void Event_for_another_sale_is_dropped()
    {
        var m = Machine();
        var c = m.Apply(new SaleEvent.CardPresented("S2", "Good", Grade.Regular));
        Assert.Equal(SaleState.Idle, m.State);
        Assert.Contains("other sale", ((SaleCommand.Log)c[0]).Message);
    }

    [Fact]
    public void No_pour_in_time_cancels_and_reverses_the_hold()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.ReadyToPump));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.Equal(EndReason.NoNozzleLift, m.End);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
        Assert.Contains(c, x => x is SaleCommand.ArmTimer { State: SaleState.Cancelled });   // the notice timer
        Assert.Equal(DriverAction.TryAgain, m.Notice().Action);
    }

    [Fact]
    public void Stale_timeout_from_an_earlier_state_is_ignored()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        m.Apply(new SaleEvent.Timeout("S1", SaleState.Authorizing));
        Assert.Equal(SaleState.ReadyToPump, m.State);
    }

    [Fact]
    public void Hang_with_zero_fuel_reverses_instead_of_completing()
    {
        var m = Fueling(Grade.Regular);
        var c = m.Apply(new SaleEvent.NozzleHung("S1"));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
        Assert.DoesNotContain(c, x => x is SaleCommand.SendCompletion);
    }

    [Fact]
    public void Host_down_at_completion_goes_to_store_and_forward_and_settles_when_it_posts()
    {
        var m = Fueling(Grade.Regular);
        m.Apply(new SaleEvent.CeilingReached("S1", Volume.FromGallons(10), new Money(3499)));
        m.Apply(new SaleEvent.CompletionQueued("S1", new Money(3499)));
        Assert.Equal(SaleState.StoreAndForward, m.State);
        Assert.Equal(HoldStatus.SettlePending, m.Hold);
        Assert.Equal(DriverAction.Leave, m.Notice().Action);
        Assert.Contains("will be charged $34.99", m.Notice().Detail);

        m.Apply(new SaleEvent.CompletionSent("S1", new Money(3499)));
        Assert.Equal(SaleState.StoreAndForward, m.State);
        Assert.Equal(HoldStatus.Settled, m.Hold);
        Assert.Contains("was charged $34.99", m.Notice().Detail);
    }

    [Fact]
    public void Fault_while_fueling_closes_the_valve_and_settles_the_fuel_already_poured()
    {
        var m = Fueling(Grade.Regular);
        m.Apply(new SaleEvent.Progress("S1", Volume.FromGallons(2), new Money(700), FlowRate.GallonsPerMinute(10), "full"));
        var c = m.Apply(new SaleEvent.Fault("S1", "pulser spoof"));
        Assert.Equal(SaleState.Faulted, m.State);
        Assert.Contains(c, x => x is SaleCommand.CloseValve);
        Assert.Contains(c, x => x is SaleCommand.SendCompletion { Final.Cents: 700 });
        Assert.DoesNotContain(c, x => x is SaleCommand.Reverse);
        Assert.Equal(DriverAction.UseAnotherPump, m.Notice().Action);
    }

    [Fact]
    public void Fault_before_any_fuel_reverses_the_hold()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        var c = m.Apply(new SaleEvent.Fault("S1", "stray pulses"));
        Assert.Equal(SaleState.Faulted, m.State);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
    }

    [Fact]
    public void Declined_is_terminal_until_a_new_sale()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Stolen", Grade.Regular));
        m.Apply(new SaleEvent.Declined("S1", "LostOrStolen", "card reported lost or stolen, pick up card"));
        Assert.Equal(SaleState.Declined, m.State);
        Assert.Equal(HoldStatus.None, m.Hold);
        Assert.Equal(DriverAction.UseAnotherCard, m.Notice().Action);
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Equal(SaleState.Declined, m.State);
    }

    // ----- The saga: where the money is when the bank runs late.

    [Fact]
    public void Authorization_timeout_declines_reverses_and_tells_the_driver_to_try_again()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.Authorizing));
        Assert.Equal(SaleState.Declined, m.State);
        Assert.Equal(EndReason.BankSilent, m.End);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
        var n = m.Notice();
        Assert.Equal("Bank did not answer", n.Headline);
        Assert.Equal(DriverAction.TryAgain, n.Action);
        Assert.Contains("No fuel was dispensed", n.Detail);
    }

    [Fact]
    public void Late_approval_after_the_timeout_is_reversed_and_never_armed()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Timeout("S1", SaleState.Authorizing));
        var c = m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "late-token"));
        Assert.Equal(SaleState.Declined, m.State);
        Assert.Null(m.Token);
        Assert.DoesNotContain(c, x => x is SaleCommand.ArmPump);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
        Assert.Equal(HoldStatus.Held, m.Hold);
        m.Apply(new SaleEvent.Reversed("S1"));
        Assert.Equal(HoldStatus.Released, m.Hold);
        Assert.Contains("Nothing was charged", m.Notice().Detail);
    }

    [Fact]
    public void Queued_reversal_shows_release_pending_until_the_bank_confirms()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        m.Apply(new SaleEvent.Timeout("S1", SaleState.ReadyToPump));
        m.Apply(new SaleEvent.ReversalQueued("S1"));
        Assert.Equal(HoldStatus.ReleasePending, m.Hold);
        Assert.Contains("releases automatically", m.Notice().Detail);
        m.Apply(new SaleEvent.Reversed("S1"));
        Assert.Equal(HoldStatus.Released, m.Hold);
    }

    [Fact]
    public void Late_completion_answer_settles_a_store_and_forward_sale()
    {
        var m = Fueling(Grade.Regular);
        m.Apply(new SaleEvent.CeilingReached("S1", Volume.FromGallons(10), new Money(3499)));
        m.Apply(new SaleEvent.Timeout("S1", SaleState.Completing));
        Assert.Equal(SaleState.StoreAndForward, m.State);
        Assert.Equal(HoldStatus.SettlePending, m.Hold);
        m.Apply(new SaleEvent.CompletionSent("S1", new Money(3499)));
        Assert.Equal(HoldStatus.Settled, m.Hold);
    }

    // ----- Tender: cash is the ceiling, change comes back at the till.

    [Fact]
    public void Cash_sale_sets_the_ceiling_to_the_cent_and_owes_change_for_the_unpumped_part()
    {
        var m = Machine();
        var c1 = m.Apply(new SaleEvent.CashPaid("S1", new Money(2_000)));
        Assert.Equal(SaleState.Authorizing, m.State);
        Assert.Equal(Tender.Cash, m.Tender);
        Assert.Contains(c1, c => c is SaleCommand.RequestCashAuthorization { Amount.Cents: 2_000 });
        Assert.Contains("Cash $20.00 received", m.Notice().Headline);

        m.Apply(new SaleEvent.Approved("S1", new Money(2_000), "cash-token"));
        Assert.Equal(SaleState.ReadyToPump, m.State);
        Assert.Equal(new Money(2_000), m.Ceiling);
        Assert.Contains("You prepaid $20.00 in cash", m.Notice().Detail);

        m.Apply(new SaleEvent.GradeSelected("S1", Grade.Regular));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        m.Apply(new SaleEvent.NozzleSeated("S1"));
        m.Apply(new SaleEvent.FlowStarted("S1"));
        m.Apply(new SaleEvent.Progress("S1", Volume.FromGallons(2), new Money(700), FlowRate.GallonsPerMinute(10), "full"));
        m.Apply(new SaleEvent.NozzleHung("S1"));
        Assert.Equal(SaleState.Completing, m.State);
        m.Apply(new SaleEvent.CompletionSent("S1", new Money(700)));
        Assert.Equal(SaleState.Idle, m.State);
        Assert.Equal(HoldStatus.Settled, m.Hold);
        Assert.Equal(new Money(1_300), m.ChangeDue);
    }

    [Fact]
    public void Cash_sale_abandoned_before_any_pour_refunds_the_whole_amount()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CashPaid("S1", new Money(2_000)));
        m.Apply(new SaleEvent.Approved("S1", new Money(2_000), "cash-token"));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.ReadyToPump));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
        m.Apply(new SaleEvent.Reversed("S1"));
        Assert.Equal(new Money(2_000), m.ChangeDue);
        Assert.Contains("Collect your $20.00 refund at the cashier", m.Notice().Detail);
    }

    [Fact]
    public void Terminal_screens_clear_to_idle_after_the_notice_timeout()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Stolen", Grade.Regular));
        var c = m.Apply(new SaleEvent.Declined("S1", "LostOrStolen", "pick up card"));
        Assert.Contains(c, x => x is SaleCommand.ArmTimer { State: SaleState.Declined, After.TotalSeconds: 30 });
        m.Apply(new SaleEvent.Timeout("S1", SaleState.Declined));
        Assert.Equal(SaleState.Idle, m.State);
        Assert.StartsWith("Insert or tap your card", m.Notice().Headline);
    }
}
