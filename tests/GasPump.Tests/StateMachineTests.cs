using GasPump.Core;
using Xunit;

namespace GasPump.Tests;

public class StateMachineTests
{
    private static SaleStateMachine Machine() => new("S1", Money.FromDollars(75));

    [Fact]
    public void Happy_path_emits_the_right_commands_in_order()
    {
        var m = Machine();
        var c1 = m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Plus));
        Assert.Equal(SaleState.Authorizing, m.State);
        Assert.Contains(c1, c => c is SaleCommand.RequestAuthorization { Hold.Cents: 7500 });
        Assert.Contains(c1, c => c is SaleCommand.ArmTimer { State: SaleState.Authorizing });

        var c2 = m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(30), "tok"));
        Assert.Equal(SaleState.ReadyToPump, m.State);
        Assert.Contains(c2, c => c is SaleCommand.ArmPump { Token: "tok", Grade: Grade.Plus });

        m.Apply(new SaleEvent.PumpArmed("S1", Money.FromDollars(30), Volume.FromGallons(50)));
        var c3 = m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Equal(SaleState.Fueling, m.State);
        Assert.Contains(c3, c => c is SaleCommand.OpenValve);

        m.Apply(new SaleEvent.Progress("S1", Volume.FromGallons(1), new Money(350), FlowRate.GallonsPerMinute(10), "full"));
        var c4 = m.Apply(new SaleEvent.CeilingReached("S1", Volume.FromGallons(8.574m), new Money(3000)));
        Assert.Equal(SaleState.Completing, m.State);
        Assert.Contains(c4, c => c is SaleCommand.CloseValve);
        Assert.Contains(c4, c => c is SaleCommand.SendCompletion { Final.Cents: 3000 });

        m.Apply(new SaleEvent.CompletionSent("S1", new Money(3000)));
        Assert.Equal(SaleState.Idle, m.State);
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
    public void No_nozzle_lift_cancels_and_reverses_the_hold()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.ReadyToPump));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
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
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        var c = m.Apply(new SaleEvent.NozzleHung("S1"));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.Contains(c, x => x is SaleCommand.Reverse);
        Assert.DoesNotContain(c, x => x is SaleCommand.SendCompletion);
    }

    [Fact]
    public void Host_down_at_completion_goes_to_store_and_forward()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        m.Apply(new SaleEvent.CeilingReached("S1", Volume.FromGallons(10), new Money(3499)));
        m.Apply(new SaleEvent.CompletionQueued("S1", new Money(3499)));
        Assert.Equal(SaleState.StoreAndForward, m.State);
    }

    [Fact]
    public void Fault_while_fueling_closes_the_valve_from_any_state()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(75), "tok"));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        var c = m.Apply(new SaleEvent.Fault("S1", "pulser spoof"));
        Assert.Equal(SaleState.Faulted, m.State);
        Assert.Contains(c, x => x is SaleCommand.CloseValve);
    }

    [Fact]
    public void Declined_is_terminal_until_a_new_sale()
    {
        var m = Machine();
        m.Apply(new SaleEvent.CardPresented("S1", "Stolen", Grade.Regular));
        m.Apply(new SaleEvent.Declined("S1", "LostOrStolen", "pick up card"));
        Assert.Equal(SaleState.Declined, m.State);
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Equal(SaleState.Declined, m.State);
    }
}
