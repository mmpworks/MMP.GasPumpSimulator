using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Pump;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// One station, two fuelling points, written before the code that makes them
/// (docs/prd-two-pumps.md, acceptance rows 3 to 8). Each test opens a sale on
/// both pumps and checks that the one payment worker, the one journal, the
/// one ledger, and the one bank keep them apart.
/// </summary>
public class TwoPumpTests : IDisposable
{
    private static readonly byte[] Ikm = Enumerable.Range(1, 32).Select(i => (byte)(i * 11)).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-two-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The library defaults to one pump; every test here asks for two, as the host does.</summary>
    private static readonly SimulationOptions TwoPumps = SimulationOptions.Default with { PumpCount = 2 };
    private static readonly SimulationOptions ThirtyDollarHold = TwoPumps with { HoldAmount = Money.FromDollars(30) };

    private SimulationOptions Journaled(string runId) => ThirtyDollarHold with
    {
        Journal = new JournalOptions(_dir, "STATION", Ikm, runId),
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_two_pump_station_has_two_points_with_their_own_ids_nodes_and_sale_counters()
    {
        using var h = new SimHarness("channel", TwoPumps);
        Assert.Equal(2, h.Sim.Points.Count);
        Assert.Equal(["PUMP-01", "PUMP-02"], h.Sim.Points.Select(p => p.PumpId));
        Assert.Equal([1, 2], h.Sim.Points.Select(p => p.Number));
        Assert.Equal(WireNode.Pump(1), PumpNodes.Of(h.Sim.Point(1).PumpId));
        Assert.Equal(WireNode.Pump(2), PumpNodes.Of(h.Sim.Point(2).PumpId));
        Assert.Equal("P1-S0001", h.Sim.Point(1).CurrentSaleId);
        Assert.Equal("P2-S0001", h.Sim.Point(2).CurrentSaleId);
        // The one-pump facade is pump 1, so the 394 older tests keep their meaning.
        Assert.Same(h.Sim.Point(1), h.Sim.Point(1));
        Assert.Equal(h.Sim.Point(1).CurrentSaleId, h.Sim.CurrentSaleId);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Two_sales_fuel_at_once_and_each_lands_on_its_own_cent(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var good = TestCards.Get("Good");
        var low = TestCards.Get("LowBalance");
        var goodBefore = h.Sim.Issuer.AvailableBalance(good.Pan);
        var lowBefore = h.Sim.Issuer.AvailableBalance(low.Pan);

        var s1 = h.Present(1, "Good");
        var s2 = h.Present(2, "LowBalance");
        h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.AdvanceUntilState(2, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));

        // Routing: the partial approval belongs to pump 2 and the full one to pump 1.
        Assert.Equal(Money.FromDollars(30), h.Point(1).Snapshot().Ceiling);
        Assert.Equal(new Money(1850), h.Point(2).Snapshot().Ceiling);
        Assert.Equal(["P1-S0001", "P2-S0001"], h.Sim.Issuer.OpenHolds.Order());

        h.Point(1).StartFueling(Grade.Regular);
        h.Point(2).StartFueling(Grade.Premium);
        h.AdvanceUntilState(1, SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.AdvanceUntilState(2, SaleState.Fueling, TimeSpan.FromSeconds(1));
        Assert.Equal(PumpPhase.Dispensing, h.Point(1).LocalPump.Phase);
        Assert.Equal(PumpPhase.Dispensing, h.Point(2).LocalPump.Phase);

        h.AdvanceUntil(() => h.Point(1).Snapshot().State == SaleState.Idle && h.Point(2).Snapshot().State == SaleState.Idle, TimeSpan.FromMinutes(3));

        var p1 = h.Snapshots.Last(s => s.SaleId == s1 && s.State == SaleState.Completing);
        var p2 = h.Snapshots.Last(s => s.SaleId == s2 && s.State == SaleState.Completing);
        Assert.Equal(3000, p1.Money.Cents);
        Assert.Equal(1850, p2.Money.Cents);
        Assert.Equal(Grade.Regular, p1.Grade);
        Assert.Equal(Grade.Premium, p2.Grade);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(goodBefore - new Money(3000), h.Sim.Issuer.AvailableBalance(good.Pan));
        Assert.Equal(lowBefore - new Money(1850), h.Sim.Issuer.AvailableBalance(low.Pan));

        // Each island counted only its own fuel.
        Assert.Equal(p1.Volume, h.Point(1).LocalDispenser.Totalizer.Total);
        Assert.Equal(p2.Volume, h.Point(2).LocalDispenser.Totalizer.Total);

        // One ledger, and every sale line says which pump by its sale id.
        var opens = h.Sim.Ledger.Snapshot().Where(e => e.Kind == "sale.open").Select(e => e.Detail.Split(' ')[0]).ToList();
        Assert.Equal(["P1-S0001", "P2-S0001"], opens.Order());
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Pump_2_waits_behind_pump_1s_slow_bank_call_only_under_the_threaded_strategy(string strategy)
    {
        using var h = new SimHarness(strategy, TwoPumps);
        h.Sim.Network.Latency = TimeSpan.FromSeconds(2);
        h.Present(1, "Good");
        h.Advance(TimeSpan.FromMilliseconds(100));
        h.Present(2, "Good");

        var waited = h.AdvanceUntilState(2, SaleState.ReadyToPump, TimeSpan.FromSeconds(10));

        if (strategy == "threaded")
        {
            // The payment worker's one thread sits on pump 1's call for the whole latency; pump 2's request waits in the box behind it.
            Assert.True(waited >= TimeSpan.FromSeconds(3.8), $"threaded pump 2 approved after {waited}, expected head-of-line blocking");
        }
        else
        {
            // The call continues on a callback. Pump 2's request leaves while pump 1's is still out.
            Assert.True(waited <= TimeSpan.FromSeconds(2.5), $"{strategy} pump 2 approved after {waited}, expected the calls to overlap");
        }
        Assert.Equal(SaleState.ReadyToPump, h.Point(1).Snapshot().State);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_token_signed_for_pump_1_is_refused_by_pump_2(string strategy)
    {
        using var h = new SimHarness(strategy, TwoPumps);
        h.Present(1, "Good");
        h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        var token = h.Point(1).LastToken!;

        h.Point(2).AttackArm(token);
        h.Settle();

        Assert.Equal(PumpPhase.Disarmed, h.Point(2).LocalPump.Phase);
        Assert.Null(h.Point(2).View.Armed);
        var refused = h.Sim.Ledger.Snapshot().Where(e => e.Kind == "arm.refused").ToList();
        // The authority's reason is the token check that failed: the token names pump 1, this is pump 2.
        Assert.Contains(refused, e => e.Detail.StartsWith("P2-S0001 ", StringComparison.Ordinal) && e.Detail.Contains("WrongPump", StringComparison.Ordinal));
        // Pump 1 still holds its authorization; the attack touched nothing on its island.
        Assert.Equal(PumpPhase.Armed, h.Point(1).LocalPump.Phase);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Closing_pump_2_for_sales_leaves_pump_1_selling(string strategy)
    {
        using var h = new SimHarness(strategy, TwoPumps);
        h.Point(2).ClosePump("operator");
        h.Settle();
        Assert.Equal(PumpPhase.Closed, h.Point(2).LocalPump.Phase);
        Assert.False(h.Point(2).View.Service.InService);
        Assert.True(h.Point(1).View.Service.InService);

        var refused = h.TryPresent(2, "Good");
        Assert.False(refused.Ok);
        Assert.Contains(DiagnosticCodes.PumpClosed.Code, refused.Reason);

        var ok = h.TryPresent(1, "Good");
        Assert.True(ok.Ok);
        h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(["P1-S0001"], h.Sim.Issuer.OpenHolds);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_drive_off_on_pump_1_faults_only_pump_1s_sale(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present(1, "Good");
        h.Present(2, "Good");
        h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.AdvanceUntilState(2, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Point(1).StartFueling();
        h.Point(2).StartFueling();
        h.AdvanceUntilState(1, SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.AdvanceUntilState(2, SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));

        h.Point(1).DriveOff();
        h.AdvanceUntilState(1, SaleState.Faulted, TimeSpan.FromSeconds(2));
        Assert.Equal(DiagnosticCodes.PumpHoseBreakaway.Code, h.Point(1).Snapshot().FaultCode);
        Assert.False(h.Point(1).View.Service.InService);

        // Pump 2 never heard about it.
        Assert.Equal(SaleState.Fueling, h.Point(2).Snapshot().State);
        Assert.True(h.Point(2).View.Service.InService);
        Assert.Equal(PumpPhase.Dispensing, h.Point(2).LocalPump.Phase);
        h.AdvanceUntil(() => h.Point(2).Snapshot().State == SaleState.Idle, TimeSpan.FromMinutes(3));
        Assert.Equal(3000, h.Snapshots.Last(s => s.SaleId == "P2-S0001" && s.State == SaleState.Completing).Money.Cents);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void The_price_board_is_written_to_both_islands(string strategy)
    {
        using var h = new SimHarness(strategy, TwoPumps);
        Assert.True(h.Sim.AdminSetPrice("1234", Grade.Regular, 3999).Ok);
        h.Settle();
        Assert.Equal(3999, h.Point(1).View.Prices![Grade.Regular]);
        Assert.Equal(3999, h.Point(2).View.Prices![Grade.Regular]);
        Assert.Equal(2, h.Sim.Ledger.Snapshot().Count(e => e.Kind == "price.set"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Station_power_loss_with_both_pumps_fueling_closes_both_sales_out(string strategy)
    {
        Money money1, money2;
        using (var h = new SimHarness(strategy, Journaled("RUN-1")))
        {
            h.Present(1, "Good");
            h.Present(2, "Good");
            h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.AdvanceUntilState(2, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Point(1).StartFueling(Grade.Regular);
            h.Point(2).StartFueling(Grade.Plus);
            h.AdvanceUntilState(1, SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.AdvanceUntilState(2, SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.Advance(TimeSpan.FromSeconds(4));
            money1 = h.Point(1).Snapshot().Money;
            money2 = h.Point(2).Snapshot().Money;
            Assert.True(money1 > Money.Zero && money2 > Money.Zero);
            h.Sim.SimulatePowerLoss();
        }

        var snapshot = StateJournal.Load(Path.Combine(_dir, "state.json"))!;
        Assert.Equal(JournalSnapshot.CurrentVersion, snapshot.Version);
        Assert.Equal(2, snapshot.Points.Count);
        Assert.Equal(["P1-S0001", "P2-S0001"], snapshot.Points.Select(p => p.Sale.SaleId));

        using (var h2 = new SimHarness(strategy, Journaled("RUN-2")))
        {
            h2.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(2, h2.Sim.Recoveries.Count);
            Assert.All(h2.Sim.Recoveries, r => Assert.Equal(RecoveryKind.ClosedOut, r.Kind));
            Assert.Equal(["P1-S0001", "P2-S0001"], h2.Sim.Recoveries.Select(r => r.SaleId));
            Assert.Equal(money1, h2.Sim.Recoveries[0].Money);
            Assert.Equal(money2, h2.Sim.Recoveries[1].Money);
            Assert.Empty(h2.Sim.Issuer.OpenHolds);
            Assert.Contains(h2.Sim.Issuer.Settlements, s => s.SaleId == "P1-S0001" && s.Final == money1);
            Assert.Contains(h2.Sim.Issuer.Settlements, s => s.SaleId == "P2-S0001" && s.Final == money2);
            // Each pump's sale counter carried over, so the next sales are the second on each pump.
            Assert.Equal("P1-S0002", h2.Point(1).CurrentSaleId);
            Assert.Equal("P2-S0002", h2.Point(2).CurrentSaleId);
        }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void E_stop_cuts_both_pumps_and_the_reset_settles_both_sales_with_nothing_unrecorded(string strategy)
    {
        using (var h = new SimHarness(strategy, Journaled("RUN-1")))
        {
            h.Present(1, "Good");
            h.Present(2, "DebitGoodPin", tender: Tender.Debit);
            h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.AdvanceUntilState(2, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Point(1).StartFueling();
            h.Point(2).StartFueling();
            h.AdvanceUntilState(1, SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.AdvanceUntilState(2, SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.Advance(TimeSpan.FromSeconds(3));
            h.Sim.EmergencyStop("test");
            Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "station.estop" && e.Detail.Contains("P1-S0001") && e.Detail.Contains("P2-S0001"));
        }

        using (var h2 = new SimHarness(strategy, Journaled("RUN-2")))
        {
            h2.Advance(TimeSpan.FromSeconds(3));
            Assert.All(h2.Sim.Recoveries, r => { Assert.Equal(RecoveryKind.ClosedOut, r.Kind); Assert.False(r.UnderRecorded); });
            Assert.Empty(h2.Sim.Issuer.OpenHolds);
            Assert.Equal(2, h2.Sim.Issuer.Settlements.Count(s => s.SaleId is "P1-S0001" or "P2-S0001"));
        }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_sale_record_that_cleared_to_Idle_after_a_fault_is_never_reused_by_the_next_card(string strategy)
    {
        // Found by the Playwright suite over two processes: the dispenser came back, the faulted sale cleared to Idle, the next card
        // reused its id, and the old sale's unanswered Close faulted the new sale 3 s later.
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        var first = h.Present(1, "Good");
        h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Point(1).StartFueling();
        h.AdvanceUntilState(1, SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(2));
        h.Point(1).DriveOff();
        h.AdvanceUntilState(1, SaleState.Faulted, TimeSpan.FromSeconds(2));
        h.Point(1).ResetBreakaway();
        h.AdvanceUntilState(1, SaleState.Idle, TimeSpan.FromSeconds(40));
        Assert.Equal(first, h.Point(1).CurrentSaleId);            // the cleared screen still belongs to the old record

        var second = h.Present(1, "Good");
        Assert.NotEqual(first, second);
        h.AdvanceUntilState(1, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(second, h.Point(1).CurrentSaleId);
        Assert.Single(h.Sim.Issuer.OpenHolds);
        Assert.Equal(second, h.Sim.Issuer.OpenHolds.Single());
    }

    [Fact]
    public void A_journaled_pump_the_station_no_longer_has_still_gets_its_hold_reversed()
    {
        // Two pumps, pump 2 approved and untouched, then the station restarts with GASPUMP_PUMPS=1.
        using (var h = new SimHarness("channel", Journaled("RUN-1")))
        {
            h.Present(2, "Good");
            h.AdvanceUntilState(2, SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            Assert.Equal(["P2-S0001"], h.Sim.Issuer.OpenHolds);
            h.Sim.SimulatePowerLoss();
        }
        using var one = new SimHarness("channel", Journaled("RUN-2") with { PumpCount = 1 });
        one.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(one.Sim.Points);
        Assert.Empty(one.Sim.Issuer.OpenHolds);
        Assert.Contains(one.Sim.Ledger.Snapshot(), e => e.Kind == "recovery.orphan-pump" && e.Detail.Contains("PUMP-02") && e.Detail.Contains("Reversed"));
    }

    [Fact]
    public void A_version_1_journal_is_carried_forward_as_pump_1_with_its_money()
    {
        // The one-pump build left this: a sale fueling on PUMP-07, a completion still owed to the bank, change owed at the till.
        Directory.CreateDirectory(_dir);
        var issuer = IssuerSimulator.WithTestCards();
        // The bank still holds S0002's money; the queued completion is what settles it.
        Assert.True(issuer.Authorize("S0002", TestCards.Get("Good") with { Zip = TestCards.Zip }, new Money(1234), SimHarness.Start).Approved);
        var v1 = new JournalSnapshotV1(1, SimHarness.Start.AddSeconds(-2), "RUN-OLD", "PUMP-07", 3,
            new JournalSale("S0003", "Fueling", "Credit", "Plus", true, "Good", 3000, 1_500, 570, 167, "Held", 0),
            "Dispensing", "Plus",
            [new JournalPending("S0002", "Completion", 1234)], new() { ["S0002"] = "Credit", ["S0003"] = "Credit" },
            new(), new() { ["S0001"] = 250 }, 7500, 5000, 1, null, 9_999, ["nonce-a"], issuer.Export(), null, null, new() { [Grade.Regular] = 3499, [Grade.Plus] = 3851, [Grade.Premium] = 4099 });
        File.WriteAllText(Path.Combine(_dir, "state.json"), System.Text.Json.JsonSerializer.Serialize(v1, new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));

        using var h = new SimHarness("channel", Journaled("RUN-2"));
        Assert.Contains(h.Sim.Log.Snapshot(), l => l.Source == "journal" && l.Message.Contains("version 1", StringComparison.Ordinal) && l.Message.Contains("carried forward", StringComparison.Ordinal));
        // Pump 1 inherits the old sale; pump 2 has nothing to close.
        Assert.Equal(RecoveryKind.ClosedOut, h.Point(1).LastRecovery.Kind);
        Assert.Equal("S0003", h.Point(1).LastRecovery.SaleId);
        Assert.Equal(new Money(570), h.Point(1).LastRecovery.Money);
        Assert.Equal(RecoveryKind.CleanStart, h.Point(2).LastRecovery.Kind);
        // The site's money came with it: the queued completion drains, the change due is still owed, the limits and the board held.
        h.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(h.Sim.Gateway.Pending);
        Assert.Contains(h.Sim.Issuer.Settlements, s => s.SaleId == "S0002" && s.Final == new Money(1234));
        Assert.Equal(new Money(250), h.Sim.Drawer.ChangeDue["S0001"]);
        Assert.Equal(Money.FromDollars(75), h.Sim.Site.CreditHold);
        Assert.Equal(1, h.Sim.Site.FailedPins);
        Assert.Equal(3851, h.Sim.Prices.PriceOf(Grade.Plus));
        Assert.Equal(new Volume(9_999), h.Point(1).LocalDispenser.Totalizer.Total);
        // Legacy ids are pump 1's, and the counter carries on past them.
        Assert.Same(h.Point(1), h.Sim.PointOf("S0003"));
        Assert.Equal("P1-S0004", h.Point(1).CurrentSaleId);
        Assert.Equal("P2-S0001", h.Point(2).CurrentSaleId);
    }
}
