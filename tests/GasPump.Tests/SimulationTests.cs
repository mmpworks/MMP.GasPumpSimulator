using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Pump;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The whole thing, end to end, under every dispatch strategy. One theory,
/// two rows today, a third when AsyncProxy lands.
/// </summary>
public class SimulationTests
{
    private static SimulationOptions ThirtyDollarHold => SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) };

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Full_sale_lands_on_the_ceiling_and_settles_for_the_real_amount(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(Money.FromDollars(30), h.State.Ceiling);
        Assert.Equal(before - Money.FromDollars(30), h.Sim.Issuer.AvailableBalance(card.Pan));

        h.Sim.LiftNozzle();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        var fuelingTime = h.AdvanceUntil(() => h.State.State is SaleState.Completing or SaleState.Idle, TimeSpan.FromMinutes(3));
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));

        Assert.Equal(3000, h.State.Money.Cents);
        Assert.All(h.Snapshots, s => Assert.True(s.Money.Cents <= 3000));
        var phases = h.Snapshots.Where(s => s.State == SaleState.Fueling).Select(s => s.Phase).Distinct().ToList();
        Assert.Contains("full", phases);
        Assert.Contains("taper", phases);
        Assert.Contains("creep", phases);
        Assert.True(fuelingTime > TimeSpan.FromSeconds(60), $"took {fuelingTime}");

        Assert.Equal(before - Money.FromDollars(30), h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal(h.State.Volume, h.Sim.Totalizer.Total);
        Assert.Equal(0, h.Sim.Ledger.Verify());
        Assert.Empty(h.Sim.Gateway.PendingCompletions);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Partial_approval_sets_a_lower_ceiling(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.PresentCard("LowBalance");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(1850, h.State.Ceiling.Cents);
        Assert.Contains(h.Sim.Log.Snapshot(), l => l.Message.Contains("PartialApproval"));
    }

    [Theory]
    [InlineData("threaded", "Expired", "Expired")]
    [InlineData("threaded", "Stolen", "LostOrStolen")]
    [InlineData("threaded", "InsufficientFunds", "InsufficientFunds")]
    [InlineData("threaded", "DebitBadPin", "InvalidPin")]
    [InlineData("channel", "Expired", "Expired")]
    [InlineData("channel", "Stolen", "LostOrStolen")]
    [InlineData("channel", "InsufficientFunds", "InsufficientFunds")]
    [InlineData("channel", "DebitBadPin", "InvalidPin")]
    public void Declined_cards_end_declined_with_the_reason(string strategy, string card, string reason)
    {
        using var h = new SimHarness(strategy);
        h.Sim.PresentCard(card);
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.Contains(reason, h.State.LastMessage);
        h.Sim.LiftNozzle();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Volume.Zero, h.Sim.Pump.Volume);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Network_timeout_at_authorization_declines_after_thirty_seconds(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.Network.Mode = NetworkMode.Timeout;
        h.Sim.PresentCard("Good");
        h.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SaleState.Authorizing, h.State.State);
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.Contains("timed out", h.State.LastMessage);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Host_down_at_completion_queues_and_drains_when_it_returns(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.LiftNozzle();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(10));
        h.Sim.Network.Mode = NetworkMode.Down;
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.StoreAndForward, TimeSpan.FromSeconds(5));
        Assert.Single(h.Sim.Gateway.PendingCompletions);
        Assert.True(before - Money.FromDollars(30) == h.Sim.Issuer.AvailableBalance(card.Pan), "hold still on while queued");

        h.Sim.Network.Mode = NetworkMode.Up;
        h.Sim.DrainPendingCompletions();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.True(h.Sim.Gateway.PendingCompletions.Count == 0, "pending not drained\n" + h.TailLog(30));
        Assert.Equal(before - h.State.Money, h.Sim.Issuer.AvailableBalance(card.Pan));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void No_nozzle_lift_for_two_minutes_cancels_and_reverses_the_hold(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(SaleState.Cancelled, h.State.State);
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
    }

    // ----- Attacks

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Signed_600_gallon_token_is_clamped_to_50_gallons_and_500_dollars(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.AttackSignedOverLimit(Volume.FromGallons(600), Money.FromDollars(2000));
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(2));
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(Money.FromDollars(500), h.State.Ceiling);
        Assert.True(h.Sim.Pump.Armed!.Clamped);
        Assert.Equal(Volume.FromGallons(50), h.Sim.Pump.Armed.MaxVolume);

        h.Sim.LiftNozzle();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.AdvanceUntil(() => h.State.State != SaleState.Fueling, TimeSpan.FromMinutes(12));
        Assert.Equal(Volume.FromGallons(50), h.State.Volume);
        Assert.Equal(17495, h.State.Money.Cents);
        Assert.Equal(Volume.FromGallons(50), h.Sim.Totalizer.Total);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Tampered_token_is_refused_and_the_sale_fails_closed(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.AttackTamperCeiling(Money.FromDollars(2000));
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains("BadSignature"));
        Assert.Equal(SaleState.Faulted, h.State.State);
        Assert.NotEqual(PumpPhase.Dispensing, h.Sim.Pump.Phase);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Replayed_token_is_refused(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Advance(TimeSpan.FromMilliseconds(300));
        h.Sim.AttackReplayLastToken();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains("Replayed"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Arming_without_a_token_is_refused(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.AttackArmWithoutToken();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains("BadFormat"));
        h.Sim.LiftNozzle();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Volume.Zero, h.Sim.Totalizer.Total);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Spoofed_pulses_during_a_sale_fault_the_pump_and_close_the_valve(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.LiftNozzle();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        var totalBefore = h.Sim.Totalizer.Total;

        h.Sim.AttackSpoofPulses(2000);
        h.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(SaleState.Faulted, h.State.State);
        Assert.Equal(PumpPhase.Faulted, h.Sim.Pump.Phase);
        Assert.Equal(totalBefore, h.Sim.Totalizer.Total);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "pump.fault" && e.Detail.Contains("pulser"));
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }
}
