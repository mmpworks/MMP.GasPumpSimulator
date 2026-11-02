using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The idle timer. A driver who lets go of the trigger gets a minute to
/// squeeze again. A squeeze inside the minute cancels the timer and nothing
/// else changes. Silence past the minute closes the valve and settles the sale
/// for the fuel that moved, so a forgotten nozzle never holds a pump open.
/// </summary>
public class PausedFlowTests
{
    private static SimulationOptions ThirtyDollarHold => SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) };

    private static SaleStateMachine Fueling()
    {
        var m = new SaleStateMachine("S1");
        m.Apply(new SaleEvent.CardPresented("S1", "Good", Grade.Regular));
        m.Apply(new SaleEvent.CodeEntered("S1", TestCards.Zip));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(30), "tok"));
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        m.Apply(new SaleEvent.NozzleSeated("S1"));
        m.Apply(new SaleEvent.FlowStarted("S1"));
        m.Apply(new SaleEvent.Progress("S1", Volume.FromGallons(2), new Money(700), FlowRate.GallonsPerMinute(10), "full"));
        Assert.Equal(SaleState.Fueling, m.State);
        return m;
    }

    // ----- The pure machine.

    [Fact]
    public void Letting_go_arms_the_idle_timer_beside_the_max_fueling_timer()
    {
        var m = Fueling();
        var c = m.Apply(new SaleEvent.FlowPaused("S1", "trigger released"));
        Assert.Equal("paused", m.Phase);
        var idle = Assert.Single(c.OfType<SaleCommand.ArmTimer>());
        Assert.Equal(SaleCommand.IdleTimer, idle.Tag);
        Assert.Equal(SaleTimeouts.Default.PausedFlow, idle.After);
        Assert.DoesNotContain(c, x => x is SaleCommand.CancelTimer);   // the max-fueling timer keeps running
        Assert.Contains("finishes by itself after 60 s", m.Notice().Detail);
    }

    [Fact]
    public void Squeezing_again_cancels_only_the_idle_timer()
    {
        var m = Fueling();
        m.Apply(new SaleEvent.FlowPaused("S1", "trigger released"));
        var c = m.Apply(new SaleEvent.FlowResumed("S1"));
        Assert.Equal("full", m.Phase);
        var cancel = Assert.Single(c.OfType<SaleCommand.CancelTimer>());
        Assert.Equal(SaleCommand.IdleTimer, cancel.Tag);
    }

    [Fact]
    public void The_idle_timer_firing_closes_the_valve_and_settles_for_the_fuel_delivered()
    {
        var m = Fueling();
        m.Apply(new SaleEvent.FlowPaused("S1", "trigger released"));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.Fueling, SaleCommand.IdleTimer));
        Assert.Equal(SaleState.Completing, m.State);
        Assert.Equal(EndReason.NoFlow, m.End);
        Assert.Contains(c, x => x is SaleCommand.CloseValve);
        Assert.Contains(c, x => x is SaleCommand.SendCompletion { Final.Cents: 700 });
        Assert.Contains(c, x => x is SaleCommand.CancelTimer { Tag: null });   // every timer of the sale, max fueling included
    }

    [Fact]
    public void An_idle_timer_that_fires_after_the_squeeze_crossed_it_is_ignored()
    {
        var m = Fueling();
        m.Apply(new SaleEvent.FlowPaused("S1", "trigger released"));
        m.Apply(new SaleEvent.FlowResumed("S1"));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.Fueling, SaleCommand.IdleTimer));
        Assert.Equal(SaleState.Fueling, m.State);
        Assert.DoesNotContain(c, x => x is SaleCommand.CloseValve or SaleCommand.SendCompletion);
    }

    [Fact]
    public void The_max_fueling_timer_still_ends_a_sale_that_never_paused()
    {
        var m = Fueling();
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.Fueling));
        Assert.Equal(EndReason.MaxFuelingTime, m.End);
        Assert.Contains(c, x => x is SaleCommand.SendCompletion);
    }

    // ----- End to end, under every dispatch strategy.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_phone_call_pauses_the_pump_and_a_squeeze_picks_up_where_it_left_off(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(5));
        var atCall = h.State.Volume;
        Assert.True(atCall > Volume.Zero);

        // The phone rings. The driver lets go and talks for twenty seconds.
        h.Sim.ReleaseTrigger();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("paused", h.State.Phase);
        var pausedAt = h.Clock.GetUtcNow();
        Assert.NotNull(h.State.DeadlineAt);
        Assert.InRange(h.State.DeadlineAt!.Value, pausedAt + TimeSpan.FromSeconds(58), pausedAt + h.Options.Timeouts.PausedFlow);
        h.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(SaleState.Fueling, h.State.State);
        Assert.Equal(atCall, h.State.Volume);                  // the meter stood still
        Assert.Equal(Volume.Zero, h.Sim.Totalizer.Total - atCall);

        // Call over. One squeeze and the fuel moves again.
        h.Sim.SqueezeTrigger();
        h.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("full", h.State.Phase);
        Assert.True(h.State.Volume > atCall);
        // The screen counts down to the max-fueling deadline again, far past the idle minute.
        Assert.True(h.State.DeadlineAt!.Value > pausedAt + TimeSpan.FromMinutes(5));

        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(EndReason.Completed, h.State.End);
        Assert.Equal(h.State.Volume, h.Sim.Totalizer.Total);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_nozzle_left_idle_for_a_minute_finishes_the_sale_by_itself_and_bills_only_the_fuel_delivered(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(5));
        h.Sim.ReleaseTrigger();
        h.Advance(TimeSpan.FromSeconds(1));
        var delivered = h.State.Volume;
        var owed = h.State.Money;
        Assert.True(owed > Money.Zero);

        // Nobody comes back. The idle minute runs out with the nozzle still in the tank.
        h.Advance(h.Options.Timeouts.PausedFlow);
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(10));

        Assert.Equal(EndReason.NoFlow, h.State.End);
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(owed, h.State.Money);
        Assert.Equal(delivered, h.State.Volume);
        Assert.Equal(delivered, h.Sim.Totalizer.Total);
        Assert.Equal(GasPump.Pump.PumpPhase.Stopped, h.Sim.Pump.Phase);   // valve closed by the sale, nozzle still out
        Assert.Contains(h.Sim.Issuer.Settlements, s => s.Final == owed);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Empty(h.Sim.Gateway.Pending);
        Assert.Equal(0, h.Sim.Ledger.Verify());
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "sale.close" && e.Detail.Contains("NoFlow"));

        // A squeeze now does nothing: the sale is over and the pump is disarmed.
        h.Sim.SqueezeTrigger();
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(delivered, h.Sim.Totalizer.Total);

        // Hanging the nozzle afterwards is fine and changes nothing.
        h.Sim.HangNozzle();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Equal(owed, h.State.Money);
        Assert.Equal(GasPump.Pump.PumpPhase.Stopped, h.Sim.Pump.Phase);   // the resting phase after any finished delivery; the next Arm works from here

        // And the pump takes the next card.
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Pulling_the_nozzle_out_of_the_tank_pauses_too_and_the_same_minute_applies(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(3));
        h.Sim.UnseatNozzle();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("paused", h.State.Phase);
        Assert.Contains("Put the nozzle back in the tank", h.State.Notice.Detail);
        h.Advance(h.Options.Timeouts.PausedFlow);
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(10));
        Assert.Equal(EndReason.NoFlow, h.State.End);
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
    }
}
