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
    public void Driver_can_stop_and_start_the_trigger_and_hanging_ends_the_sale(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));

        // Nothing flows until grade, lift, seat, and trigger are all in.
        h.Sim.LiftNozzle(); h.Sim.SeatNozzle(); h.Sim.SqueezeTrigger();
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SaleState.ReadyToPump, h.State.State);
        Assert.Equal(Volume.Zero, h.Sim.Totalizer.Total);
        Assert.Contains("Select a grade", h.State.Notice.Detail);

        h.Sim.SelectGrade(Grade.Plus);
        h.Sim.SqueezeTrigger();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(3));
        var firstPour = h.State.Volume;
        Assert.True(firstPour > Volume.Zero);

        h.Sim.ReleaseTrigger();
        h.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(SaleState.Fueling, h.State.State);
        Assert.Equal("paused", h.State.Phase);
        Assert.Equal(firstPour, h.State.Volume);

        h.Sim.SqueezeTrigger();
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
        h.Sim.PresentCard("LowBalance");
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
        h.Sim.PresentCard(card);
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
        h.Sim.PresentCard("Good");
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
    public void Host_down_at_completion_queues_and_drains_when_it_returns(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var card = TestCards.Get("Good");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);

        h.Sim.PresentCard("Good");
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
        h.Sim.PresentCard("Good");
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
        h.Sim.PresentCard("Stolen");
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
        h.Sim.PresentCard("Good");
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
        h.Sim.PresentCard("Good");
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
        var first = h.Sim.PresentCard("Good");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(35));

        // The driver tries again at once. The old sale's late approval must not leak a hold.
        h.Sim.Network.Mode = NetworkMode.Up;
        var second = h.Sim.PresentCard("Good");
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

    // ----- Tender: credit, debit, cash. The site's ceilings, the bank's answer, and the pump's hard limits, in that order.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Debit_sale_holds_the_lesser_of_the_site_ceiling_and_the_bank(string strategy)
    {
        using var h = new SimHarness(strategy);
        var card = TestCards.Get("DebitGoodPin");
        var before = h.Sim.Issuer.AvailableBalance(card.Pan);
        h.Sim.PresentCard("DebitGoodPin", tender: Tender.Debit);
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
        h.Sim.PresentCard("DebitGoodPin", tender: Tender.Debit);
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
        h.Sim.PresentCard("Good");
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
        h.Sim.PresentCard("Good");
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
