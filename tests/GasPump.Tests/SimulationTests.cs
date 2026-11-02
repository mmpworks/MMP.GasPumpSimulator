using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Pump;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The whole system, end to end, under every dispatch strategy. It's one theory
/// with two rows today. A third row comes when AsyncProxy lands.
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

        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(Money.FromDollars(30), h.State.Ceiling);
        Assert.Equal(before - Money.FromDollars(30), h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal(DriverAction.LiftNozzle, h.State.Notice.Action);
        Assert.NotNull(h.State.DeadlineAt);

        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        var fuelingTime = h.AdvanceUntil(() => h.State.State is SaleState.Completing or SaleState.Idle, TimeSpan.FromMinutes(3));
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));

        Assert.Equal(3000, h.State.Money.Cents);
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.All(h.Snapshots, s => Assert.True(s.Money.Cents <= 3000));
        var phases = h.Snapshots.Where(s => s.State == SaleState.Fueling).Select(s => s.Phase).Distinct().ToList();
        Assert.Contains("full", phases);
        Assert.Contains("taper", phases);
        Assert.Contains("creep", phases);
        Assert.True(fuelingTime > TimeSpan.FromSeconds(60), $"took {fuelingTime}");

        Assert.Equal(before - Money.FromDollars(30), h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal(h.State.Volume, h.Sim.Totalizer.Total);
        Assert.Equal(0, h.Sim.Ledger.Verify());
        Assert.Empty(h.Sim.Gateway.Pending);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void The_bank_receives_the_charges_itemized_and_the_receipt_reads_the_same_lines(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Empty(h.State.Charges);   // nothing to itemize before fuel moves

        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.AdvanceUntil(() => h.State.State is SaleState.Completing or SaleState.Idle, TimeSpan.FromMinutes(3));
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));

        // What the driver can print: the same lines the pump priced, summing to the money on the screen.
        var receipt = h.State.Charges;
        Assert.Single(receipt);
        Assert.Equal(ChargeLine.Fuel, receipt[0].Code);
        Assert.Equal(h.State.Money, ChargeLine.Total(receipt));
        Assert.Contains("Regular", receipt[0].Description);

        // What the bank kept: the same lines, under the same sale id, summing to the settled amount.
        var settlement = Assert.Single(h.Sim.Issuer.Settlements);
        Assert.Equal(h.State.SaleId, settlement.SaleId);
        Assert.Equal(Money.FromDollars(30), settlement.Final);
        Assert.Equal(receipt, settlement.Lines);
        Assert.Equal(settlement.Final, ChargeLine.Total(settlement.Lines));
    }

    private sealed class Recorder : IStationCapability
    {
        public string Name => "recorder";
        public int FlowStarts;
        public int Views;
        public void OnSaleEvent(SaleEvent e) { if (e is SaleEvent.FlowStarted) Interlocked.Increment(ref FlowStarts); }
        public void OnPumpView(DispenserView v) => Interlocked.Increment(ref Views);
    }

    private sealed class Bomb : IStationCapability
    {
        public string Name => "bomb";
        public void OnSaleEvent(SaleEvent e) { if (e is SaleEvent.FlowStarted) throw new InvalidOperationException("video player crashed"); }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_capability_sees_the_telemetry_on_its_own_worker_and_one_that_throws_cannot_touch_the_sale(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var recorder = new Recorder();
        var bomb = new Bomb();
        h.Sim.Capabilities.Register(recorder);
        h.Sim.Capabilities.Register(bomb);

        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.AdvanceUntil(() => h.State.State is SaleState.Completing or SaleState.Idle, TimeSpan.FromMinutes(3));
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));

        // The sale finished as if nothing were attached.
        Assert.Equal(3000, h.State.Money.Cents);
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(0, h.Sim.Ledger.Verify());

        // The recorder saw fueling start once, and the pump views as they came.
        Assert.Equal(1, recorder.FlowStarts);
        Assert.True(recorder.Views > 0);
        Assert.True(h.Sim.Capabilities.HandledFor("recorder") > 1);
        Assert.Equal(0, h.Sim.Capabilities.DroppedFor("recorder"));

        // The bomb went off once, was switched off, and the log says so.
        Assert.Equal(["bomb"], h.Sim.Capabilities.Disabled);
        Assert.Contains(h.Sim.Log.Snapshot(), l => l.Source == "capability" && l.Message.Contains("switched off"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Driver_can_stop_and_start_the_lever_and_hanging_ends_the_sale(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));

        // Nothing flows until grade, lift, spout, and lever are all in.
        h.Sim.LiftNozzle(); h.Sim.InsertSpout(); h.Sim.SqueezeLever();
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SaleState.ReadyToPump, h.State.State);
        Assert.Equal(Volume.Zero, h.Sim.Totalizer.Total);
        Assert.Contains("Select a grade", h.State.Notice.Detail);

        h.Sim.SelectGrade(Grade.Plus);
        h.Sim.SqueezeLever();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(3));
        var firstPour = h.State.Volume;
        Assert.True(firstPour > Volume.Zero);

        h.Sim.ReleaseLever();
        h.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SaleState.Fueling, h.State.State);
        Assert.Equal("paused", h.State.Phase);
        Assert.Equal(firstPour, h.State.Volume);

        h.Sim.SqueezeLever();
        h.Advance(TimeSpan.FromSeconds(3));
        Assert.True(h.State.Volume > firstPour);
        Assert.Equal(Grade.Plus, h.State.Grade);

        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(h.State.Volume, h.Sim.Totalizer.Total);
        Assert.Equal(Pricing.Default.CentsFor(Grade.Plus, h.State.Volume), h.State.Money);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Partial_approval_sets_a_lower_ceiling(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Present("LowBalance");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(1850, h.State.Ceiling.Cents);
        Assert.Contains(h.Sim.Log.Snapshot(), l => l.Message.Contains("PartialApproval"));
        Assert.Contains("$18.50", h.State.Notice.Headline);
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
    [InlineData("asyncproxy", "Expired", "Expired")]
    [InlineData("asyncproxy", "Stolen", "LostOrStolen")]
    [InlineData("asyncproxy", "InsufficientFunds", "InsufficientFunds")]
    [InlineData("asyncproxy", "DebitBadPin", "InvalidPin")]
    public void Declined_cards_end_declined_with_the_reason(string strategy, string card, string reason)
    {
        using var h = new SimHarness(strategy);
        h.Present(card);
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.Contains(reason, h.State.LastMessage);
        Assert.Equal(DriverAction.UseAnotherCard, h.State.Notice.Action);
        h.Sim.StartFueling();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Volume.Zero, h.Sim.Pump.Volume);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Network_timeout_at_authorization_declines_after_thirty_seconds(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.Network.Mode = NetworkMode.Timeout;
        h.Present("Good");
        h.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SaleState.Authorizing, h.State.State);
        Assert.Equal(DriverAction.Wait, h.State.Notice.Action);
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.Contains("timed out", h.State.LastMessage);
        Assert.Equal(EndReason.BankSilent, h.State.End);
        Assert.Equal(DriverAction.TryAgain, h.State.Notice.Action);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Link_down_at_authorization_declines_at_once_with_nothing_queued(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Sim.Network.Mode = NetworkMode.Down;
        h.Present("Good");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(2));
        Assert.Equal(EndReason.BankDown, h.State.End);
        Assert.Equal(HoldStatus.None, h.State.Hold);
        Assert.Empty(h.Sim.Gateway.Pending);          // nothing to reverse, so nothing queued
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal("Bank link is down", h.State.Notice.Headline);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Link_goes_down_mid_fuel_and_the_sale_finishes_on_the_token_then_queues_the_completion(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(5));
        var atOutage = h.State.Volume;

        // The link dies with fuel flowing. The pump needs nothing from the bank until completion.
        h.Sim.Network.Mode = NetworkMode.Down;
        h.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(SaleState.Fueling, h.State.State);
        Assert.True(h.State.Volume > atOutage, "fuel kept flowing through the outage");

        h.Sim.ReleaseLever();
        h.Advance(TimeSpan.FromSeconds(2));
        h.Sim.SqueezeLever();                          // pause and resume still work with the bank gone
        h.Advance(TimeSpan.FromSeconds(3));
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.StoreAndForward, TimeSpan.FromSeconds(5));
        Assert.Equal(HoldStatus.SettlePending, h.State.Hold);
        Assert.Equal(DriverAction.Leave, h.State.Notice.Action);
        Assert.Single(h.Sim.Gateway.PendingCompletions);
        Assert.Equal(h.State.Volume, h.Sim.Totalizer.Total);
        Assert.Equal(0, h.Sim.Ledger.Verify());

        h.Sim.Network.Mode = NetworkMode.Up;
        h.Sim.DrainPendingCompletions();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(before - h.State.Money, h.Sim.Issuer.AvailableBalance(card.Pan));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Host_down_at_completion_queues_and_drains_when_it_returns(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(10));
        h.Sim.Network.Mode = NetworkMode.Down;
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.StoreAndForward, TimeSpan.FromSeconds(5));
        Assert.Single(h.Sim.Gateway.PendingCompletions);
        Assert.Equal(HoldStatus.SettlePending, h.State.Hold);
        Assert.Equal(DriverAction.Leave, h.State.Notice.Action);
        Assert.True(before - Money.FromDollars(30) == h.Sim.Issuer.AvailableBalance(card.Pan), "hold still on while queued");

        h.Sim.Network.Mode = NetworkMode.Up;
        h.Sim.DrainPendingCompletions();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.True(h.Sim.Gateway.Pending.Count == 0, "pending not drained\n" + h.TailLog(30));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(before - h.State.Money, h.Sim.Issuer.AvailableBalance(card.Pan));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void No_pour_for_two_minutes_cancels_and_reverses_the_hold(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        // The driver gets as far as lifting the nozzle, then walks off.
        h.Sim.SelectGrade(Grade.Regular);
        h.Sim.LiftNozzle();
        h.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(SaleState.Cancelled, h.State.State);
        Assert.Equal(EndReason.NoNozzleLift, h.State.End);
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Released, h.State.Hold);
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Contains("Nothing was charged", h.State.Notice.Detail);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Finished_screen_clears_to_idle_after_thirty_seconds(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Present("Stolen");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.NotNull(h.State.DeadlineAt);
        h.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(SaleState.Declined, h.State.State);
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Null(h.State.DeadlineAt);
    }

    // ----- The saga: the bank link runs late or dies with money in motion.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Bank_unreachable_after_timeout_queues_the_reversal_and_releases_when_the_link_returns(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        h.Sim.Network.Mode = NetworkMode.Timeout;
        h.Present("Good");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(35));
        // The reversal goes out and also times out. It must be queued, never dropped.
        h.AdvanceUntil(() => h.State.Hold == HoldStatus.ReleasePending, TimeSpan.FromSeconds(70));
        Assert.Single(h.Sim.Gateway.PendingReversals);
        Assert.Contains("releases automatically", h.State.Notice.Detail);
        Assert.Equal(DriverAction.TryAgain, h.State.Notice.Action);

        h.Sim.Network.Mode = NetworkMode.Up;
        h.Sim.DrainPendingCompletions();
        h.AdvanceUntil(() => h.State.Hold == HoldStatus.Released, TimeSpan.FromSeconds(5));
        Assert.Empty(h.Sim.Gateway.Pending);
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Empty(h.Sim.Issuer.OpenHolds);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Late_approval_is_reversed_not_armed_and_the_card_ends_untouched(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        // The bank hears us at 250 ms and holds $75. Its answer takes 45 s. We give up at 30 s.
        h.Sim.Network.Mode = NetworkMode.Late;
        h.Present("Good");
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before - Money.FromDollars(75), h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Single(h.Sim.Issuer.OpenHolds);

        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(35));
        Assert.Equal(EndReason.BankSilent, h.State.End);
        Assert.Null(h.Sim.Pump.Armed);

        // The late approval lands on a Declined sale. It must never arm the pump, and the hold must come off.
        h.AdvanceUntil(() => h.Sim.Log.Snapshot().Any(l => l.Message.Contains("LATE approval")), TimeSpan.FromSeconds(60));
        Assert.Equal(SaleState.Declined, h.State.State);
        Assert.Null(h.Sim.Pump.Armed);
        Assert.Equal(Volume.Zero, h.Sim.Totalizer.Total);

        h.AdvanceUntil(() => h.State.Hold == HoldStatus.Released, TimeSpan.FromMinutes(3));
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Contains("Nothing was charged", h.State.Notice.Detail);
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Late_approval_for_a_sale_the_driver_already_replaced_is_still_reversed(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        h.Sim.Network.Mode = NetworkMode.Late;
        var first = h.Present("Good");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(35));

        // The driver tries again at once. The old sale's late approval must not leak a hold.
        h.Sim.Network.Mode = NetworkMode.Up;
        var second = h.Present("Good");
        Assert.NotEqual(first, second);
        // Under the threaded strategy the payment worker is still blocked on the first sale's late call, so the
        // second authorization waits behind it (head-of-line blocking). The channel strategy answers at once.
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(60));

        h.AdvanceUntil(() => h.Sim.Log.Snapshot().Any(l => l.Message.Contains($"finished sale {first}")), TimeSpan.FromMinutes(2));
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([second], h.Sim.Issuer.OpenHolds);
        Assert.Equal(before - Money.FromDollars(75), h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal(SaleState.ReadyToPump, h.State.State);
    }

    // ----- Faults and repairs in the states the reviewers named.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Drive_off_with_the_lever_released_faults_the_sale_at_once_and_settles_for_the_fuel(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        h.Sim.ReleaseLever();                               // the realistic drive-off: nozzle in the car, lever let go
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("paused", h.State.Phase);
        var delivered = h.State.Money;

        h.Sim.DriveOff();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));   // one second, never the ten-minute fueling limit
        Assert.Equal("PUMP.HOSE.BREAKAWAY", h.State.FaultCode);
        Assert.Equal(delivered, h.State.Money);
        h.AdvanceUntil(() => h.State.Hold == HoldStatus.Settled, TimeSpan.FromSeconds(5));
        Assert.Equal(before - delivered, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal(PumpPhase.Faulted, h.Sim.Pump.Phase);

        h.Sim.ResetBreakaway();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(1));
        Assert.Equal("Pump back in service", h.State.Notice.Headline);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Tank_dry_while_the_bank_is_still_deciding_faults_the_sale_and_the_late_approval_leaves_no_hold(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Sim.Network.Latency = TimeSpan.FromSeconds(2);
        h.Present("Good");
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(SaleState.Authorizing, h.State.State);
        Assert.Equal(HoldStatus.Requested, h.State.Hold);

        h.Sim.TankRunsDry();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        Assert.Equal("PUMP.STP.NOFLOW", h.State.FaultCode);

        // The bank answers later. Whatever it says, nothing stays held.
        h.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.False(h.State.State == SaleState.ReadyToPump);

        h.Sim.TankRefilled();
        h.AdvanceUntil(() => h.State.State == SaleState.Idle, TimeSpan.FromSeconds(2));
        Assert.Equal("Pump back in service", h.State.Notice.Headline);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_per_sale_fault_never_claims_the_pump_was_out_of_service(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(1));
        h.Sim.AttackSpoofPulses(2000);
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        Assert.Equal("PUMP.MTR.OVERPULSE", h.State.FaultCode);
        Assert.True(h.Sim.View.Service.InService);              // a spoofed pulser is a per-sale fault, never an outage

        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(40));
        Assert.NotEqual("Pump back in service", h.State.Notice.Headline);
    }

    // ----- A second card. The last driver left without lifting; the next one should never wait on their hold.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Second_card_before_the_nozzle_is_lifted_cancels_the_first_sale_reverses_its_hold_and_starts_over(string strategy)
    {
        using var h = new SimHarness(strategy);
        var good = TestCards.Get("Good");
        var debit = TestCards.Get("DebitGoodPin");
        var goodBefore = h.Sim.Issuer.AvailableBalance(good.Pan);
        var debitBefore = h.Sim.Issuer.AvailableBalance(debit.Pan);

        var first = h.Present("Good", source: CardSource.PumpTap);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal([first], h.Sim.Issuer.OpenHolds);

        // Driver one drives off. Driver two taps a debit card at the same reader.
        var (ok, second, _) = h.TryPresent("DebitGoodPin", tender: Tender.Debit, source: CardSource.PumpTap);
        Assert.True(ok);
        Assert.NotEqual(first, second);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(second, h.State.SaleId);
        Assert.Equal(Tender.Debit, h.State.Tender);
        Assert.Equal([second], h.Sim.Issuer.OpenHolds);                 // the first hold is gone from the bank
        Assert.Equal(goodBefore, h.Sim.Issuer.AvailableBalance(good.Pan));
        Assert.True(h.Sim.Issuer.AvailableBalance(debit.Pan) < debitBefore);
        var ledger = h.Sim.Ledger.Snapshot();
        Assert.Contains(ledger, e => e.Kind == "sale.superseded" && e.Detail.StartsWith(first));
        Assert.Contains(ledger, e => e.Kind == "sale.close" && e.Detail.StartsWith(first) && e.Detail.Contains("Cancelled"));
        Assert.Contains(ledger, e => e.Kind == "sale.open" && e.Detail.StartsWith(second));
        Assert.Contains(h.Sim.Log.Snapshot(), l => l.Message.Contains($"hold for earlier sale {first} released"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void New_sale_on_an_approved_sale_reverses_its_hold_and_new_sale_while_fueling_settles_for_the_fuel(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        // Approved, nozzle in hand, operator presses New sale: the hold goes back.
        var first = h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.LiftNozzle();
        h.Advance(TimeSpan.FromMilliseconds(300));
        h.Sim.ResetSale();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(first, h.State.SaleId);
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "sale.superseded" && e.Detail.StartsWith(first) && e.Detail.Contains("reset"));

        // Fueling, operator presses New sale: the valve closes and the driver pays for what moved, no more.
        var second = h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        var delivered = h.State.Money;
        Assert.True(delivered > Money.Zero);
        h.Sim.ResetSale();
        h.AdvanceUntil(() => h.State.State == SaleState.Idle && h.State.Hold == HoldStatus.Settled, TimeSpan.FromSeconds(5));
        Assert.Equal(second, h.State.SaleId);
        Assert.Equal(delivered, h.State.Money);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(before - delivered, h.Sim.Issuer.AvailableBalance(card.Pan));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Second_card_while_the_nozzle_is_in_hand_is_refused_and_the_first_sale_keeps_going(string strategy)
    {
        using var h = new SimHarness(strategy);
        var first = h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.LiftNozzle();
        h.Advance(TimeSpan.FromMilliseconds(300));

        var (ok, id, reason) = h.TryPresent("DebitGoodPin", tender: Tender.Debit);
        Assert.False(ok);
        Assert.Equal(first, id);
        Assert.Contains("pump in use", reason);
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(first, h.State.SaleId);
        Assert.Equal(SaleState.ReadyToPump, h.State.State);
        Assert.Equal([first], h.Sim.Issuer.OpenHolds);

        // Fueling is in use too, for anyone, by any route.
        h.Sim.SelectGrade(Grade.Regular);
        h.Sim.InsertSpout();
        h.Sim.SqueezeLever();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        Assert.False(h.TryPresent("Good", source: CardSource.Station).Ok);
        Assert.Equal(first, h.Sim.PayCash(Money.FromDollars(10)));
        Assert.Equal(SaleState.Fueling, h.State.State);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_sale_set_at_the_station_waits_longer_for_the_lift_than_one_set_at_the_pump(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Present("Good", source: CardSource.PumpInsert);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(CardSource.PumpInsert, h.State.Source);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(120), h.State.DeadlineAt);
        h.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(SaleState.Cancelled, h.State.State);
        Assert.Equal(EndReason.NoNozzleLift, h.State.End);

        h.Sim.ResetSale();
        h.Present("Good", source: CardSource.Station);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(CardSource.Station, h.State.Source);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(300), h.State.DeadlineAt);
        Assert.Contains("cashier can cancel", h.State.Notice.Detail);
        h.Advance(TimeSpan.FromSeconds(150));
        Assert.Equal(SaleState.ReadyToPump, h.State.State);            // still waiting: the cashier holds this one
        h.Advance(TimeSpan.FromSeconds(151));
        Assert.Equal(SaleState.Cancelled, h.State.State);

        // Cash is always a station sale.
        h.Sim.ResetSale();
        h.Sim.PayCash(Money.FromDollars(10));
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(CardSource.Station, h.State.Source);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromSeconds(300), h.State.DeadlineAt);
    }

    // ----- Tender: credit, debit, cash. The site's ceilings, the bank's answer, and the pump's hard limits, in that order.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Debit_sale_holds_the_lesser_of_the_site_ceiling_and_the_bank(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("DebitGoodPin");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("DebitGoodPin", tender: Tender.Debit);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(Money.FromDollars(50), h.State.Ceiling);          // site debit ceiling, balance was $900
        Assert.Equal(before - Money.FromDollars(50), h.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Equal(Tender.Debit, h.State.Tender);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Cash_sale_stops_on_the_cash_amount_and_owes_change_when_hung_early(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.Network.Mode = NetworkMode.Down;   // cash never needs the bank
        h.Sim.PayCash(new Money(1_500));
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(1));
        Assert.Equal(new Money(1_500), h.State.Ceiling);
        Assert.Empty(h.Sim.Issuer.OpenHolds);

        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(5));
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(2));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(new Money(1_500) - h.State.Money, h.State.ChangeDue);
        Assert.True(h.State.ChangeDue > Money.Zero);
        Assert.Equal(h.State.ChangeDue, h.Sim.Drawer.ChangeDue[h.State.SaleId]);
        Assert.Contains("change at the cashier", h.State.Notice.Detail);

        // Pump it all and the sale lands on the cash amount to the cent.
        h.Sim.PayCash(new Money(1_500));
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(1));
        h.Sim.StartFueling();
        h.AdvanceUntil(() => h.State.State is SaleState.Idle, TimeSpan.FromMinutes(2));
        Assert.Equal(1_500, h.State.Money.Cents);
        Assert.Equal(Money.Zero, h.State.ChangeDue);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Admin_raises_the_debit_ceiling_to_5000_and_the_pump_still_clamps_at_its_hard_limit(string strategy)
    {
        using var h = new SimHarness(strategy);
        var (ok, _) = h.Sim.AdminSetHold("1234", Tender.Debit, Money.FromDollars(5000));
        Assert.True(ok);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "admin.limits" && e.Detail.Contains("$5000.00"));

        // DebitGoodPin has $900, so the bank approves $900 of the $5000 ask. The pump clamps that to $500.
        h.Present("DebitGoodPin", tender: Tender.Debit);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(Money.FromDollars(500), h.State.Ceiling);
        Assert.True(h.Sim.Pump.Armed!.Clamped);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "arm.ok" && e.Detail.Contains("clamped"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Brute_forcing_the_admin_pin_locks_the_panel_and_changes_nothing(string strategy)
    {
        using var h = new SimHarness(strategy);
        var tried = h.Sim.AttackBruteForceAdminPin(guesses: 50);
        Assert.Equal(3, tried);
        Assert.NotNull(h.Sim.Site.LockedUntil);
        Assert.Equal(Money.FromDollars(50), h.Sim.Site.DebitHold);
        Assert.Equal(1, h.Sim.Ledger.Snapshot().Count(e => e.Kind == "admin.locked"));
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Admin_changes_a_price_and_the_pump_bills_the_next_sale_by_it_after_the_running_one_ends(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        Assert.False(h.Sim.AdminSetPrice("0000", Grade.Regular, 4000).Ok);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "admin.refused" && e.Detail.Contains("wrong PIN"));
        Assert.False(h.Sim.AdminSetPrice("1234", Grade.Regular, 0).Ok);
        Assert.Equal(3499, h.Sim.Prices.PriceOf(Grade.Regular));

        // Mid-delivery, the write is answered Done and waits for the pour to end. The driver pays the posted price.
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        var (ok, reason) = h.Sim.AdminSetPrice("1234", Grade.Regular, 4000);
        Assert.True(ok, reason);
        Assert.Equal("Regular $3.499 -> $4.000", reason);
        Assert.Equal(4000, h.Sim.Prices.PriceOf(Grade.Regular));
        h.Settle();
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "admin.prices" && e.Detail == "Regular $3.499 -> $4.000");
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "cmd.answer" && e.Detail.StartsWith("SetPrices") && e.Detail.Contains("Done applies after the current delivery"));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "price.pending");
        Assert.Equal(3499, h.Sim.View.Prices![Grade.Regular]);              // the pump still shows what the driver is paying
        h.AdvanceUntil(() => h.State.State is SaleState.Idle && h.State.Hold == HoldStatus.Settled, TimeSpan.FromMinutes(3));
        Assert.Equal(Pricing.Default.CentsFor(Grade.Regular, h.State.Volume), h.State.Money);
        Assert.Equal(Money.FromDollars(30), h.State.Money);

        // The delivery over, the board is in force on the pump and the view says so.
        h.AdvanceUntil(() => h.Sim.View.Prices?[Grade.Regular] == 4000, TimeSpan.FromSeconds(2));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "price.set" && e.Detail.Contains("Regular $4.000"));
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntil(() => h.State.State is SaleState.Idle && h.State.Hold == HoldStatus.Settled, TimeSpan.FromMinutes(3));
        Assert.Equal(h.Sim.Prices.CentsFor(Grade.Regular, h.State.Volume), h.State.Money);
        Assert.Equal(h.Sim.Prices.MaxVolumeFor(Grade.Regular, Money.FromDollars(30)), h.State.Volume);   // $30 at $4.000, to the thousandth that still rounds to $30

        // The inspector reads the island's totals: the sealed counter live, the grade rows by finished delivery.
        h.Sim.ReadTotals();
        h.Settle();
        var totals = h.Sim.LastTotals!;
        Assert.Equal(h.Sim.Totalizer.Total, totals.Totalizer);
        var regular = Assert.Single(totals.ByGrade);
        Assert.Equal((Grade.Regular, 2L, Money.FromDollars(60)), (regular.Grade, regular.Deliveries, regular.Money));
        Assert.Equal(totals.Totalizer, regular.Volume);
        Assert.Equal(2, totals.LastTxSeq);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "totals.read" && e.Detail.Contains("Regular") && e.Detail.Contains("in 2"));
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    // ----- Forecourt faults: a failed part names itself, the sale closes for the fuel that moved, the driver hears the right next step.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Tank_runs_dry_mid_fuel_and_the_sale_settles_for_the_metered_fuel_with_the_code(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(3));
        var metered = h.State.Volume;

        h.Sim.TankRunsDry();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(6));
        Assert.Equal("PUMP.STP.NOFLOW", h.State.FaultCode);
        Assert.Equal(metered, h.State.Volume);
        Assert.Equal("Fuel supply stopped", h.State.Notice.Headline);
        Assert.Equal(DriverAction.UseAnotherPump, h.State.Notice.Action);
        Assert.Contains("charged only for the", h.State.Notice.Detail);

        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(before - h.State.Money, h.Sim.Issuer.AvailableBalance(card.Pan));
        var ledger = h.Sim.Ledger.Snapshot();
        Assert.Contains(ledger, e => e.Kind == "tank.fuelout" && e.Detail.Contains("[TANK.FUELOUT]"));
        Assert.Contains(ledger, e => e.Kind == "pump.fault" && e.Detail.Contains("[PUMP.STP.NOFLOW]"));
        Assert.Contains(h.Sim.Log.Snapshot(), l => l.Source == "pump" && l.Severity == LogSeverity.Error && l.Props.Any(p => p.Key == "Code" && (string?)p.Value == "PUMP.STP.NOFLOW"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Drive_off_closes_the_sale_calls_the_attendant_and_blocks_the_next_sale_until_reset(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));

        h.Sim.DriveOff();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        Assert.Equal("PUMP.HOSE.BREAKAWAY", h.State.FaultCode);
        Assert.Equal(DriverAction.SeeAttendant, h.State.Notice.Action);
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);

        // The next driver's card is refused before any money moves.
        var (ok, _, reason) = h.TryPresent("Good");
        Assert.False(ok);
        Assert.Contains("PUMP.HOSE.BREAKAWAY", reason);

        h.Sim.ResetBreakaway();
        h.Advance(TimeSpan.FromMilliseconds(300));
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "hose.breakaway.reset");
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Dead_pulser_mid_fuel_faults_fast_and_bills_only_what_was_counted(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        var counted = h.State.Volume;

        h.Sim.KillPulser();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        Assert.Equal("PUMP.MTR.NOPULSE", h.State.FaultCode);
        Assert.Equal(counted, h.State.Volume);
        Assert.True(h.Sim.Meter.UnmeteredVolume > Volume.Zero);
        Assert.Equal(DriverAction.SeeAttendant, h.State.Notice.Action);
    }

    // ----- Service conditions: a fault that needs a repair holds the pump out of service until the repair, then clears.

    /// <summary>Card, approval, grade, lift, insert, squeeze, and a few seconds of fuel.</summary>
    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_settled_sale_leaves_the_island_holding_nothing_and_every_command_was_answered(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        FuelForAWhile(h);
        h.Sim.HangNozzle();
        h.AdvanceUntil(() => h.State.State == SaleState.Idle && h.State.Hold == HoldStatus.Settled, TimeSpan.FromSeconds(10));
        h.Settle();

        Assert.Empty(h.Sim.Pump.Transactions.Open);
        Assert.Empty(h.Sim.View.OpenTransactions ?? []);
        var kinds = h.Sim.Ledger.Snapshot().Select(e => e.Kind).ToList();
        // The island's record, in order: written at the hang-up, claimed and released by the station once the bank answered.
        Assert.True(kinds.IndexOf("tx.payable") < kinds.IndexOf("tx.clear"), string.Join(",", kinds));
        Assert.True(kinds.IndexOf("tx.clear") < kinds.IndexOf("tx.locked"), string.Join(",", kinds));
        Assert.True(kinds.IndexOf("tx.locked") < kinds.IndexOf("tx.cleared"), string.Join(",", kinds));
        var answers = h.Sim.Ledger.Snapshot().Where(e => e.Kind == "cmd.answer").Select(e => e.Detail).ToList();
        Assert.Contains(answers, d => d.StartsWith("Arm ") && d.EndsWith(" Done"));
        Assert.Contains(answers, d => d.StartsWith("Lock ") && d.EndsWith(" Done"));
        Assert.Contains(answers, d => d.StartsWith("Clear ") && d.EndsWith(" Done"));
        Assert.DoesNotContain(kinds, k => k == "cmd.unanswered");

        // The next driver is not held up by the last one's paperwork.
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_delivery_whose_money_is_queued_by_a_dead_host_still_releases_the_island(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        FuelForAWhile(h);
        h.Sim.Network.Mode = NetworkMode.Down;                   // the completion cannot reach the bank
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.StoreAndForward, TimeSpan.FromSeconds(30));
        h.Settle();
        Assert.Empty(h.Sim.Pump.Transactions.Open);               // queued on the station is safe enough for the island
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "tx.cleared");
    }

    private static void FuelForAWhile(SimHarness h, double seconds = 2)
    {
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Attendant_reset_after_a_drive_off_clears_the_screen_at_once_when_the_hold_is_settled(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        FuelForAWhile(h);
        h.Sim.DriveOff();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal("Hose breakaway", h.State.Notice.Headline);

        h.Sim.HangNozzle();
        h.Sim.ResetBreakaway();
        h.Advance(TimeSpan.FromMilliseconds(300));       // far short of the 30 s notice timeout
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Equal("Pump back in service", h.State.Notice.Headline);
        Assert.Equal(PumpPhase.Disarmed, h.Sim.Pump.Phase);
        Assert.Null(h.Sim.View.FaultCode);
        Assert.True(h.Sim.View.Service.InService);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "pump.in-service" && e.Detail.Contains("[PUMP.HOSE.BREAKAWAY]"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Breakaway_reset_while_the_bank_still_owes_an_answer_keeps_the_screen_until_the_hold_settles(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        FuelForAWhile(h);
        h.Sim.Network.Mode = NetworkMode.Down;
        h.Sim.DriveOff();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.SettlePending, h.State.Hold);

        h.Sim.ResetBreakaway();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(SaleState.Faulted, h.State.State);  // the driver still needs to read where the money is
        Assert.True(h.Sim.View.Service.InService);

        h.Sim.Network.Mode = NetworkMode.Up;
        h.Sim.DrainPendingCompletions();
        h.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Equal("Pump back in service", h.State.Notice.Headline);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Tank_dry_while_idle_refuses_a_card_and_cash_with_the_code_and_a_refill_admits_the_next_card(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.TankRunsDry();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Equal("Out of service: submersible pump and supply line", h.State.Notice.Headline);
        Assert.Equal(DriverAction.UseAnotherPump, h.State.Notice.Action);

        var (ok, idle, reason) = h.TryPresent("Good");
        Assert.False(ok);
        Assert.Contains("PUMP.STP.NOFLOW", reason);
        Assert.Equal(idle, h.Sim.PayCash(Money.FromDollars(10)));   // the cashier is refused as well; no new sale opened
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Empty(h.Sim.Issuer.OpenHolds);

        h.Sim.TankRefilled();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.DoesNotContain("Out of service", h.State.Notice.Headline);
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Dead_pulser_while_idle_refuses_a_card_with_the_code_and_a_repair_admits_the_next_card(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.KillPulser();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("Out of service: meter pulser", h.State.Notice.Headline);
        var (ok, _, reason) = h.TryPresent("Good");
        Assert.False(ok);
        Assert.Contains("PUMP.MTR.NOPULSE", reason);

        h.Sim.FixPulser();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Tank_dry_after_approval_ends_the_sale_before_the_first_pour_and_gives_the_hold_back(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Advance(TimeSpan.FromMilliseconds(300));

        h.Sim.TankRunsDry();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        Assert.Equal("PUMP.STP.NOFLOW", h.State.FaultCode);
        Assert.Equal("Fuel supply stopped", h.State.Notice.Headline);
        Assert.Contains("No fuel was dispensed", h.State.Notice.Detail);
        Assert.NotEqual(PumpPhase.Armed, h.Sim.Pump.Phase);
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Released, h.State.Hold);
        Assert.Equal(before, h.Sim.Issuer.AvailableBalance(card.Pan));

        h.Sim.TankRefilled();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.Equal("Pump back in service", h.State.Notice.Headline);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_second_condition_keeps_the_pump_out_of_service_after_the_first_is_cleared(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        FuelForAWhile(h);
        h.Sim.DriveOff();
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        h.Sim.TankRunsDry();
        h.Advance(TimeSpan.FromSeconds(1));

        h.Sim.ResetBreakaway();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(SaleState.Idle, h.State.State);     // the breakaway that faulted this sale is fixed
        Assert.Equal("Out of service: submersible pump and supply line", h.State.Notice.Headline);
        Assert.Equal(PumpPhase.Faulted, h.Sim.Pump.Phase);
        var (ok, _, reason) = h.TryPresent("Good");
        Assert.False(ok);
        Assert.Contains("PUMP.STP.NOFLOW", reason);

        h.Sim.TankRefilled();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(PumpPhase.Disarmed, h.Sim.Pump.Phase);
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_per_sale_fault_from_spoofed_pulses_still_clears_on_the_next_arm(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        FuelForAWhile(h);
        h.Sim.AttackSpoofPulses(2000);
        h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
        Assert.Equal("PUMP.MTR.OVERPULSE", h.State.FaultCode);
        Assert.True(h.Sim.View.Service.InService);

        // While the metered fuel is being settled the island still holds the transaction, so a card is told the pump is busy.
        var (tooSoon, _, why) = h.Sim.TryPresentCard("Good");
        if (!tooSoon) Assert.Contains("settling", why);
        h.AdvanceUntil(() => h.State.Hold == HoldStatus.Settled, TimeSpan.FromSeconds(2));
        h.Settle();
        Assert.Empty(h.Sim.View.OpenTransactions ?? []);          // settled, so the station cleared it

        // Nothing to repair, so nothing holds the pump: the next card replaces the finished sale and arms.
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(PumpPhase.Armed, h.Sim.Pump.Phase);
        Assert.Null(h.Sim.Pump.LastFaultCode);
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

        h.Sim.StartFueling();
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
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.AttackTamperCeiling(Money.FromDollars(2000));
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains("BadSignature"));
        Assert.Equal(SaleState.Faulted, h.State.State);
        Assert.Equal(DriverAction.SeeAttendant, h.State.Notice.Action);
        Assert.NotEqual(PumpPhase.Dispensing, h.Sim.Pump.Phase);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Replayed_token_is_refused(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Present("Good");
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
        h.Sim.StartFueling();
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Volume.Zero, h.Sim.Totalizer.Total);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Spoofed_pulses_during_a_sale_fault_the_pump_close_the_valve_and_settle_the_fuel_poured(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        var totalBefore = h.Sim.Totalizer.Total;
        var moneyBefore = h.State.Money;

        h.Sim.AttackSpoofPulses(2000);
        h.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(SaleState.Faulted, h.State.State);
        Assert.Equal(PumpPhase.Faulted, h.Sim.Pump.Phase);
        Assert.Equal(totalBefore, h.Sim.Totalizer.Total);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "pump.fault" && e.Detail.Contains("pulser"));
        Assert.Equal(0, h.Sim.Ledger.Verify());
        Assert.Equal(DriverAction.UseAnotherPump, h.State.Notice.Action);

        // The driver owes for the real fuel, and only that.
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(before - moneyBefore, h.Sim.Issuer.AvailableBalance(card.Pan));
    }
}
