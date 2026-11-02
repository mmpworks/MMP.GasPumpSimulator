using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using MMP.Herald.Compliance.Audit;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The pump loses power and comes back. The journal has to be enough to
/// close the open sale for what was recorded, say so, and carry on. The
/// custody file for each run has to verify under TesseraSeal's verifier.
/// </summary>
public class RecoveryTests : IDisposable
{
    private static readonly byte[] Ikm = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-journal-" + Guid.NewGuid().ToString("N")[..8]);

    private SimulationOptions Options(string runId) => SimulationOptions.Default with
    {
        HoldAmount = Money.FromDollars(30),
        Journal = new JournalOptions(_dir, "PUMP-07", Ikm, runId),
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* a sink may still be closing on Windows */ }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void The_operators_price_board_rides_the_journal_so_a_restarted_station_bills_by_it(string strategy)
    {
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            Assert.True(h.Sim.AdminSetPrice("1234", Grade.Plus, 3851).Ok);
            h.Settle();
            h.Sim.SaveJournal();
            h.Sim.SimulatePowerLoss();
        }
        var snapshot = StateJournal.Load(Path.Combine(_dir, "state.json"))!;
        Assert.Equal(3851, snapshot.Prices![Grade.Plus]);

        using (var h2 = new SimHarness(strategy, Options("RUN-2")))
        {
            Assert.Equal(3851, h2.Sim.Prices.PriceOf(Grade.Plus));
            Assert.Equal(3851, ((Pricing)h2.Sim.Pump.Prices).PriceOf(Grade.Plus));     // the in-process pump was handed the same board
            Assert.Equal(3851, h2.Sim.View.Prices![Grade.Plus]);
            Assert.DoesNotContain(h2.Sim.Ledger.Snapshot(), e => e.Kind == "price.set");   // a restore is not a change
        }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Power_loss_mid_fuel_closes_the_sale_for_the_recorded_money_and_flags_under_delivery(string strategy)
    {
        var card = TestCards.Get("Good");
        Money before, recordedMoney;
        Volume recordedVolume, totalizer;
        string saleId;
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            before = h.Sim.Issuer.AvailableBalance(card.Pan);
            saleId = h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Sim.StartFueling(Grade.Plus);
            h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.Advance(TimeSpan.FromSeconds(6));      // six one-second snapshots land
            recordedMoney = h.State.Money;
            recordedVolume = h.State.Volume;
            totalizer = h.Sim.Totalizer.Total;
            Assert.True(recordedVolume > Volume.Zero);
            h.Sim.SimulatePowerLoss();               // no close-out, no final snapshot
        }

        var snapshot = StateJournal.Load(Path.Combine(_dir, "state.json"));
        Assert.NotNull(snapshot);
        Assert.Equal("Fueling", snapshot!.Point(1)!.Sale.State);
        Assert.Equal("Plus", snapshot.Point(1)!.Sale.Grade);

        using (var h2 = new SimHarness(strategy, Options("RUN-2")))
        {
            var r = h2.Sim.LastRecovery;
            Assert.Equal(RecoveryKind.ClosedOut, r.Kind);
            Assert.Equal(saleId, r.SaleId);
            Assert.Equal(Grade.Plus, r.Grade);
            Assert.True(r.UnderRecorded);
            Assert.True(r.UnrecordedUpTo > Volume.Zero, "one snapshot period of flow is the honest upper bound");
            Assert.Contains("UNDER-RECORDED", r.Note.ToUpperInvariant());

            h2.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(SaleState.Idle, h2.State.State);
            Assert.Equal(totalizer, h2.Sim.Totalizer.Total);
            // The bank's hold was restored from the journal and settled to the recorded money, no more.
            Assert.Equal(before - h2.Sim.Issuer.AvailableBalance(card.Pan), snapshot.Point(1)!.Sale.MoneyCents == recordedMoney.Cents ? recordedMoney : new Money(snapshot.Point(1)!.Sale.MoneyCents));
            Assert.Empty(h2.Sim.Issuer.OpenHolds);
            var kinds = h2.Sim.Ledger.Snapshot().Select(e => e.Kind).ToList();
            Assert.Contains("recovery.restart", kinds);
            Assert.Contains("recovery.closeout", kinds);
            Assert.Contains(h2.Sim.Log.Snapshot(), l => l.Source == "recovery" && l.Severity == LogSeverity.Error);

            // The next driver is unaffected.
            h2.Present("Good");
            h2.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            Assert.NotEqual(saleId, h2.State.SaleId);
        }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Power_loss_while_armed_with_no_fuel_reverses_the_hold(string strategy)
    {
        var card = TestCards.Get("Good");
        Money before;
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            before = h.Sim.Issuer.AvailableBalance(card.Pan);
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(before - Money.FromDollars(30), h.Sim.Issuer.AvailableBalance(card.Pan));
            h.Sim.SimulatePowerLoss();
        }
        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        Assert.Equal(RecoveryKind.Reversed, h2.Sim.LastRecovery.Kind);
        h2.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before, h2.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Empty(h2.Sim.Issuer.OpenHolds);
        Assert.Contains(h2.Sim.Ledger.Snapshot(), e => e.Kind == "recovery.reversed");
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Power_loss_during_a_cash_sale_owes_change_for_the_unpumped_part(string strategy)
    {
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            h.Sim.PayCash(new Money(2_000));
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(1));
            h.Sim.StartFueling();
            h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.Advance(TimeSpan.FromSeconds(4));
            h.Sim.SimulatePowerLoss();
        }
        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        var r = h2.Sim.LastRecovery;
        Assert.Equal(RecoveryKind.ClosedOut, r.Kind);
        Assert.Equal(Tender.Cash, r.Tender);
        h2.Advance(TimeSpan.FromSeconds(1));
        var change = h2.Sim.Drawer.ChangeDue[r.SaleId!];
        Assert.Equal(new Money(2_000) - r.Money, change);
        Assert.Empty(h2.Sim.Drawer.Open);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Journal_carries_site_limits_lockout_nonces_and_the_bank_link_queue_across_a_restart(string strategy)
    {
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            h.Sim.AdminSetHold("1234", Tender.Debit, Money.FromDollars(35));
            h.Sim.AttackBruteForceAdminPin();          // locks the panel
            h.Sim.Network.Mode = NetworkMode.Down;
            h.Present("Good");                  // refused at once, nothing queued
            h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(2));
            h.Advance(TimeSpan.FromSeconds(1));
            h.Sim.SimulatePowerLoss();
        }
        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        Assert.Equal(Money.FromDollars(35), h2.Sim.Site.DebitHold);
        Assert.NotNull(h2.Sim.Site.LockedUntil);        // a reboot is not a way around the lockout
        Assert.Equal(RecoveryKind.CleanStart, h2.Sim.LastRecovery.Kind);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Replayed_token_is_still_refused_after_a_restart(string strategy)
    {
        string token;
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Advance(TimeSpan.FromSeconds(1));
            token = h.Sim.LastToken!;
            h.Sim.SimulatePowerLoss();
        }
        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        h2.Advance(TimeSpan.FromSeconds(1));
        var (ok, reason, _) = h2.Sim.Pump.Arm("S9999", token);
        Assert.False(ok);
        Assert.Equal("Replayed", reason);
    }

    [Fact]
    public void Custody_file_verifies_under_TesseraSeal_and_a_tampered_byte_fails_at_the_right_entry()
    {
        string path;
        using (var h = new SimHarness("channel", Options("RUN-1")))
        {
            path = h.Sim.CustodyPath!;
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Sim.StartFueling();
            h.AdvanceUntil(() => h.State.State == SaleState.Idle, TimeSpan.FromMinutes(3));
            var (ok, seq, reason, lines) = h.Sim.VerifyCustody();
            Assert.True(ok, reason);
            Assert.Null(seq);
            Assert.True(lines > 10, $"expected the session's evidence in the file, got {lines} lines");
        }
        // The sink is closed now; the file can be read and rewritten directly.
        var written = File.ReadAllLines(path);
        Assert.Contains(written, l => l.Contains("sale.open"));
        Assert.Contains(written, l => l.Contains("sale.close"));
        Assert.Contains(written, l => l.Contains("valve.close"));
        Assert.DoesNotContain(written, l => l.Contains("\"category\":\"pump\""));   // debug chatter stays out

        // Tamper one character of an entry's detail. The chain breaks there, by name.
        var all = File.ReadAllLines(path);
        var idx = Array.FindIndex(all, l => l.Contains("sale.open"));
        all[idx] = all[idx].Replace("sale.open", "sale.OPEN");
        File.WriteAllText(path, string.Join('\n', all) + "\n");
        var tampered = ImmutableAuditFileSink.VerifyChain(path, Ikm, out var failSeq, out var failReason);
        Assert.False(tampered);
        Assert.Equal(idx, failSeq);   // header is line 0, so entry seq == line index
        Assert.Contains("MAC mismatch", failReason);

        // Cut the final newline, as a mid-write crash would. The verifier refuses the file outright.
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^1]);
        Assert.False(ImmutableAuditFileSink.VerifyChain(path, Ikm, out _, out var midLine));
        Assert.Contains("mid-line", midLine);
    }

    [Fact]
    public void Each_run_is_its_own_chain_and_both_verify()
    {
        using (var h = new SimHarness("channel", Options("RUN-1")))
        {
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Advance(TimeSpan.FromSeconds(2));
            h.Sim.SimulatePowerLoss();
        }
        using var h2 = new SimHarness("channel", Options("RUN-2"));
        h2.Advance(TimeSpan.FromSeconds(1));
        // Run 1's file is closed; run 2's is live, so it goes through the copy-and-verify path.
        Assert.True(ImmutableAuditFileSink.VerifyChain(Path.Combine(_dir, "custody-RUN-1.ndjson"), Ikm, out _, out var r1), r1);
        var (ok2, _, r2, _) = h2.Sim.VerifyCustody();
        Assert.True(ok2, r2);
        using var live = new FileStream(Path.Combine(_dir, "custody-RUN-2.ndjson"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(live);
        Assert.Contains("recovery.restart", reader.ReadToEnd());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void E_stop_writes_the_record_before_the_power_drops_and_the_reset_closes_the_sale_exactly(string strategy)
    {
        var card = TestCards.Get("Good");
        Money before, atStop;
        string custodyPath;
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            before = h.Sim.Issuer.AvailableBalance(card.Pan);
            custodyPath = h.Sim.CustodyPath!;
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Sim.StartFueling();
            h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
            h.Advance(TimeSpan.FromSeconds(4));
            atStop = h.State.Money;
            h.Sim.EmergencyStop("test harness");       // record, final snapshot, then power
        }

        var custody = File.ReadAllText(custodyPath);
        Assert.Contains("station.estop", custody);
        Assert.Contains("STATION.ESTOP", custody);
        var snapshot = StateJournal.Load(Path.Combine(_dir, "state.json"))!;
        Assert.Equal("ESTOP", snapshot.StopReason);

        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        var r = h2.Sim.LastRecovery;
        Assert.Equal(RecoveryKind.ClosedOut, r.Kind);
        Assert.Equal(Volume.Zero, r.UnrecordedUpTo);      // the E-stop saved after the last tick, so nothing is missing
        Assert.False(r.UnderRecorded);
        Assert.Contains("E-stop", r.Note);
        Assert.Contains("STATION.ESTOP", r.Note);
        h2.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before - atStop, h2.Sim.Issuer.AvailableBalance(card.Pan));
    }

    [Fact]
    public void Custody_stream_hands_out_the_same_bytes_the_file_holds_and_a_copy_built_from_it_verifies()
    {
        var copy = Path.Combine(_dir, "station-copy.ndjson");
        var received = new List<string>();
        using (var h = new SimHarness("channel", Options("RUN-1")))
        {
            Directory.CreateDirectory(_dir);
            // A late subscriber, as a station server that connects after the pump booted. Replay gives it the header too.
            h.Sim.Custody!.SubscribeWithReplay(line => { received.Add(line); File.AppendAllText(copy, line + "\n"); });
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Sim.StartFueling();
            h.AdvanceUntil(() => h.State.State == SaleState.Idle, TimeSpan.FromMinutes(3));
            h.Sim.TankRunsDry();                               // a tank event is evidence too
            h.Advance(TimeSpan.FromMilliseconds(300));
        }
        var original = File.ReadAllLines(Path.Combine(_dir, "custody-RUN-1.ndjson"));
        Assert.Equal(original, File.ReadAllLines(copy));
        Assert.True(ImmutableAuditFileSink.VerifyChain(copy, Ikm, out _, out var reason), reason);
        Assert.Contains(received, l => l.Contains("TANK.FUELOUT"));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_restart_does_not_repair_a_separated_hose(string strategy)
    {
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            h.Sim.DriveOff();
            h.Advance(TimeSpan.FromMilliseconds(300));
            Assert.False(h.Sim.View.Service.InService);
            h.Advance(TimeSpan.FromSeconds(1));                 // one journal period, so the conditions are on disk
            h.Sim.SimulatePowerLoss();
        }

        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        h2.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(h2.Sim.View.Service.Holds("PUMP.HOSE.BREAKAWAY"));
        Assert.Equal("Out of service: hose breakaway coupling", h2.State.Notice.Headline);
        var (ok, _, reason) = h2.TryPresent("Good");
        Assert.False(ok);
        Assert.Contains("PUMP.HOSE.BREAKAWAY", reason);

        h2.Sim.ResetBreakaway();
        h2.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(h2.Sim.View.Service.InService);
        Assert.True(h2.TryPresent("Good").Ok);
        h2.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_crash_between_a_pre_pour_fault_and_its_reversal_leaves_no_hold_after_the_restart(string strategy)
    {
        var card = TestCards.Get("Good");
        Money before;
        string saleId;
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            before = h.Sim.Issuer.AvailableBalance(card.Pan);
            saleId = h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Sim.Network.Latency = TimeSpan.FromSeconds(2);      // the reversal will sit on the wire
            h.Sim.TankRunsDry();
            h.AdvanceUntilState(SaleState.Faulted, TimeSpan.FromSeconds(1));
            Assert.Equal([saleId], h.Sim.Issuer.OpenHolds);        // still held: the bank has not answered
            h.Sim.SimulatePowerLoss();                               // the crash, in the window Iolo named
        }

        var snapshot = StateJournal.Load(Path.Combine(_dir, "state.json"))!;
        Assert.Contains(snapshot.Pending, p => p.SaleId == saleId && p.Kind == "Reversal");   // the intent reached disk first

        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        h2.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(h2.Sim.Issuer.OpenHolds);
        Assert.Equal(before, h2.Sim.Issuer.AvailableBalance(card.Pan));
        Assert.Empty(h2.Sim.Gateway.Pending);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_crash_right_after_a_second_card_superseded_the_first_leaves_neither_hold(string strategy)
    {
        var good = TestCards.Get("Good");
        var debit = TestCards.Get("DebitGoodPin");
        Money goodBefore, debitBefore;
        using (var h = new SimHarness(strategy, Options("RUN-1")))
        {
            goodBefore = h.Sim.Issuer.AvailableBalance(good.Pan);
            debitBefore = h.Sim.Issuer.AvailableBalance(debit.Pan);
            h.Present("Good");
            h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
            h.Sim.Network.Latency = TimeSpan.FromSeconds(2);
            Assert.True(h.TryPresent("DebitGoodPin", tender: Tender.Debit).Ok);   // first reversed on the wire, second authorizing on the wire
            h.Advance(TimeSpan.FromMilliseconds(200));
            h.Sim.SimulatePowerLoss();
        }

        using var h2 = new SimHarness(strategy, Options("RUN-2"));
        h2.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(h2.Sim.Issuer.OpenHolds);
        Assert.Equal(goodBefore, h2.Sim.Issuer.AvailableBalance(good.Pan));
        Assert.Equal(debitBefore, h2.Sim.Issuer.AvailableBalance(debit.Pan));
        Assert.Empty(h2.Sim.Gateway.Pending);
    }

    [Fact]
    public void Recovery_plan_table()
    {
        var now = SimHarness.Start.AddSeconds(3);
        static JournalSnapshot Snap(string state, long volume, long money, long flow, string tender = "Credit", string hold = "Held") => new(
            JournalSnapshot.CurrentVersion, SimHarness.Start, "RUN-0",
            [new JournalPoint(1, "PUMP-01", 3, new JournalSale("P1-S0003", state, tender, "Regular", true, "Good", 3000, volume, money, flow, hold, tender == "Cash" ? 2000 : 0), "Dispensing", "Regular", 0, [])],
            [], new() { ["P1-S0003"] = tender }, new(), new(), 3000, 5000, 0, null, new([], [], []));
        static RecoveryOutcome Plan(JournalSnapshot? s, DateTimeOffset now) => RecoveryPlan.Build(s, 1, now, TimeSpan.FromSeconds(1));

        // A sale that ended with the bank still holding the money: the compensation is finished on the restart.
        Assert.Equal(RecoveryKind.Reversed, Plan(Snap("Faulted", 0, 0, 0), now).Kind);
        Assert.Equal(RecoveryKind.Reversed, Plan(Snap("Cancelled", 0, 0, 0, hold: "Requested"), now).Kind);
        Assert.Equal(RecoveryKind.CompletionResent, Plan(Snap("Faulted", 1_500, 525, 0), now).Kind);
        Assert.Equal(RecoveryKind.CleanStart, Plan(Snap("Faulted", 0, 0, 0, hold: "Released"), now).Kind);
        Assert.Equal(RecoveryKind.CleanStart, Plan(Snap("StoreAndForward", 1_500, 525, 0, hold: "SettlePending"), now).Kind);

        Assert.Equal(RecoveryKind.CleanStart, Plan(null, now).Kind);
        // A journal from a station with fewer pumps: the new pump has nothing to close.
        Assert.Equal(RecoveryKind.CleanStart, RecoveryPlan.Build(Snap("Fueling", 1_500, 525, 167), 2, now, TimeSpan.FromSeconds(1)).Kind);
        Assert.Equal(RecoveryKind.CleanStart, Plan(Snap("Idle", 0, 0, 0), now).Kind);
        Assert.Equal(RecoveryKind.Reversed, Plan(Snap("ReadyToPump", 0, 0, 0), now).Kind);
        Assert.Equal(RecoveryKind.Reversed, Plan(Snap("Fueling", 0, 0, 167), now).Kind);
        var closed = Plan(Snap("Fueling", 1_500, 525, 167), now);
        Assert.Equal(RecoveryKind.ClosedOut, closed.Kind);
        Assert.Equal(new Volume(167), closed.UnrecordedUpTo);     // one second at 10 gpm
        Assert.Equal(TimeSpan.FromSeconds(3), closed.SnapshotAge);
        Assert.Equal(RecoveryKind.CompletionResent, Plan(Snap("Completing", 1_500, 525, 0), now).Kind);
    }
}
