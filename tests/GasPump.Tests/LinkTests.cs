using System.Diagnostics;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Security;
using MMP.Herald.Compliance.Audit;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// Tests the station-to-dispenser seam over a real socket. A DispenserServer
/// listens on an ephemeral loopback port, and a station Simulation reaches it
/// over TCP. Both sides use the real clock, because two processes can't share
/// a fake one. The flow is fast and the hold is small, so a sale lasts about
/// a second.
/// </summary>
public sealed class LinkTests : IDisposable
{
    private static readonly byte[] Ikm = Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-link-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    // 30 gal/min at 100 ms ticks is 50 pulses a tick, under the 60-pulse sanity limit. A $2 hold is about 0.65 gal, about 1.3 s.
    private static readonly DispenserOptions FastPump = new("PUMP-07", HardLimits.Default, Pricing.Default, TaperPolicy.Default, FlowRate.GallonsPerMinute(30), TimeSpan.FromMilliseconds(100));

    private DispenserServer NewDispenser(int port = 0, string? runId = null, TimeSpan? heartbeat = null, string pumpId = "PUMP-07") =>
        new(new DispenserServerOptions(FastPump with { PumpId = pumpId }, port, _dir, Ikm, RunId: runId, Heartbeat: heartbeat), TimeProvider.System, new ThreadedDispatch(TimeProvider.System));

    /// <summary>A one-pump station on PUMP-07, as every socket test here began. <paramref name="pumps"/> above one drives PUMP-01, PUMP-02, ... on consecutive ports.</summary>
    private Simulation NewStation(int port, string runId, TimeSpan? heartbeat = null, TimeSpan? commandTimeout = null, int pumps = 1) => new(
        new ChannelDispatch(TimeProvider.System), TimeProvider.System,
        SimulationOptions.Default with
        {
            PumpId = pumps == 1 ? "PUMP-07" : "PUMP-01",
            PumpCount = pumps,
            HoldAmount = Money.FromDollars(2),
            NominalFlow = FastPump.NominalFlow,
            TickPeriod = FastPump.TickPeriod,
            Journal = new JournalOptions(_dir, "STATION", Ikm, runId),
            Dispenser = new DispenserLinkOptions("127.0.0.1", port, heartbeat),
            CommandTimeout = commandTimeout,
        });

    /// <summary>Two islands on consecutive ports, PUMP-01 and PUMP-02, the way a two-pump station expects them. Tries another pair when the second port is taken.</summary>
    private (DispenserServer Pump1, DispenserServer Pump2) NewPair()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var first = NewDispenser(pumpId: "PUMP-01");
            try { return (first, NewDispenser(first.Port + 1, pumpId: "PUMP-02")); }
            catch (System.Net.Sockets.SocketException) { first.Dispose(); }
        }
        throw new InvalidOperationException("no free pair of consecutive loopback ports after five tries");
    }

    /// <summary>Card in, wait for the keypad prompt to cross the wire, type the test ZIP.</summary>
    private static void Present(Simulation station, string card)
    {
        station.PresentCard(card);
        WaitFor(() => station.Snapshot().State == SaleState.CardRead, TimeSpan.FromSeconds(5), () => "keypad never asked\n" + Tail(station));
        station.EnterCode(TestCards.Zip);
    }

    private static (bool Ok, string SaleId, string Reason) TryPresent(Simulation station, string card)
    {
        var r = station.TryPresentCard(card);
        if (!r.Ok) return r;
        WaitFor(() => station.Snapshot().State == SaleState.CardRead, TimeSpan.FromSeconds(5), () => "keypad never asked\n" + Tail(station));
        station.EnterCode(TestCards.Zip);
        return r;
    }

    private static void WaitFor(Func<bool> done, TimeSpan budget, Func<string> onTimeout)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > budget) throw new TimeoutException(onTimeout());
            Thread.Sleep(20);
        }
    }

    /// <summary>The sink keeps its file open for append, so a reader has to open it with sharing allowed.</summary>
    private static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static string Tail(Simulation s) => string.Join("\n", s.Log.Snapshot().TakeLast(15).Select(l => $"  [{l.Source}] {l.Message}")) + $"\n  {s.Counters} state={s.Snapshot().State} view={s.View.Phase}";

    [Fact]
    public void A_full_sale_runs_across_the_socket_and_each_side_keeps_only_its_own_records()
    {
        using var dispenser = NewDispenser();
        using var station = NewStation(dispenser.Port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        Assert.Equal("socket", station.Link.Kind);
        Assert.Equal(dispenser.RunId, station.Link.RemoteRunId);
        Assert.False(station.DispenserIsLocal);
        Assert.Throws<InvalidOperationException>(() => station.Pump);

        var card = TestCards.Get("Good");
        var before = station.Issuer.AvailableBalance(card.Pan);
        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling();
        WaitFor(() => station.Snapshot().State == SaleState.Fueling, TimeSpan.FromSeconds(5), () => "never fueled\n" + Tail(station));
        WaitFor(() => station.Snapshot().State is SaleState.Idle or SaleState.Completing && station.Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(20), () => "never settled\n" + Tail(station));

        var s = station.Snapshot();
        Assert.Equal(Money.FromDollars(2), s.Money);
        Assert.Equal(before - Money.FromDollars(2), station.Issuer.AvailableBalance(card.Pan));

        // The dispenser's ledger has the valve; the station's has the sale and the money; neither has the other's.
        var pumpKinds = dispenser.Ledger.Snapshot().Select(e => e.Kind).ToList();
        var stationKinds = station.Ledger.Snapshot().Select(e => e.Kind).ToList();
        Assert.Contains("valve.open", pumpKinds);
        Assert.Contains("valve.close", pumpKinds);
        Assert.DoesNotContain(pumpKinds, k => k.StartsWith("sale."));
        Assert.Contains("sale.open", stationKinds);
        Assert.Contains("sale.close", stationKinds);
        Assert.DoesNotContain(stationKinds, k => k.StartsWith("valve."));
        // The island's last words for this sale are the lock and the clear the station sends once the money lands. Wait for
        // them, so the chain compared below is finished rather than still growing.
        WaitFor(() => dispenser.Dispenser.Pump.Transactions.Open.Count == 0, TimeSpan.FromSeconds(3), () => "the island still holds the delivery\n" + Tail(station));
        WaitFor(() => dispenser.Ledger.Snapshot().Count == station.DispenserLedgerSnapshot().Count, TimeSpan.FromSeconds(3), () => $"ledger mirror short: {station.DispenserLedgerSnapshot().Count} of {dispenser.Ledger.Snapshot().Count}");
        Thread.Sleep(300);

        // The station's copy of the dispenser's chain is the same bytes, and verifies with the same key.
        var copy = Path.Combine(_dir, "copy.ndjson");
        var received = new List<string>();
        using (station.SubscribeDispenserCustody((runId, line) => { Assert.Equal(dispenser.RunId, runId); received.Add(line); }))
        {
            WaitFor(() => received.Count >= ReadShared(dispenser.CustodyPath!).Length, TimeSpan.FromSeconds(5), () => $"copy short: {received.Count}");
        }
        File.WriteAllLines(copy, received);
        Assert.Equal(ReadShared(dispenser.CustodyPath!), File.ReadAllLines(copy));
        Assert.True(ImmutableAuditFileSink.VerifyChain(copy, Ikm, out _, out var reason), reason);
        Assert.Contains(received, l => l.Contains("valve.close"));
        Assert.DoesNotContain(received, l => l.Contains("sale.open"));
    }

    [Fact]
    public void Dispenser_power_loss_mid_fuel_faults_the_station_sale_with_LINK_DISPENSER_LOST_and_settles_for_the_recorded_fuel()
    {
        var dispenser = NewDispenser();
        using var station = NewStation(dispenser.Port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        var card = TestCards.Get("Good");
        var before = station.Issuer.AvailableBalance(card.Pan);
        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling();
        WaitFor(() => station.Snapshot().Volume > Volume.Zero && station.Snapshot().Money > new Money(50), TimeSpan.FromSeconds(5), () => "no fuel recorded\n" + Tail(station));

        dispenser.Dispose();                                   // power cut: no record and no close-out
        WaitFor(() => station.Snapshot().State == SaleState.Faulted, TimeSpan.FromSeconds(5), () => "station did not fault\n" + Tail(station));
        var s = station.Snapshot();
        Assert.Equal("LINK.DISPENSER.LOST", s.FaultCode);
        Assert.True(s.Money > Money.Zero);
        Assert.Equal(DriverAction.SeeAttendant, s.Notice.Action);
        WaitFor(() => station.Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(5), () => "hold not settled\n" + Tail(station));
        Assert.Equal(before - s.Money, station.Issuer.AvailableBalance(card.Pan));
        Assert.Equal("Offline", station.View.Phase);
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "link.down" && e.Detail.Contains("[LINK.DISPENSER.LOST]"));
    }

    [Fact]
    public void A_dispenser_that_restarts_with_a_separated_hose_still_reports_it_and_a_new_station_refuses_cards_until_the_reset()
    {
        var dispenser = NewDispenser();
        var port = dispenser.Port;
        Simulation station = NewStation(port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        station.DriveOff();
        WaitFor(() => station.View.Service.Holds("PUMP.HOSE.BREAKAWAY"), TimeSpan.FromSeconds(3), () => "breakaway never reached the station\n" + Tail(station));

        // The dispenser process crashes and comes back on its own state file. The hose is still apart.
        dispenser.Dispose();
        WaitFor(() => !station.Link.Connected, TimeSpan.FromSeconds(3), () => "link never dropped");
        dispenser = NewDispenserOnSamePort(port);
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(8), () => "station never reconnected\n" + Tail(station));
        Assert.True(dispenser.Dispenser.Pump.Service.Holds("PUMP.HOSE.BREAKAWAY"));
        WaitFor(() => station.View.Service.Holds("PUMP.HOSE.BREAKAWAY") && !station.View.Service.Holds("LINK.DISPENSER.LOST"), TimeSpan.FromSeconds(3), () => $"station view {station.View.Service}\n" + Tail(station));

        // The station restarts too. A fresh station learns the condition from the first view, before any card.
        station.Dispose();
        station = NewStation(port, "STN-2");
        using var _ = station;
        WaitFor(() => station.Link.Connected && station.View.Service.Holds("PUMP.HOSE.BREAKAWAY"), TimeSpan.FromSeconds(8), () => $"second station view {station.View.Service}\n" + Tail(station));
        var (ok, _, reason) = station.TryPresentCard("Good");
        Assert.False(ok);
        Assert.Contains("PUMP.HOSE.BREAKAWAY", reason);

        station.ResetBreakaway();
        WaitFor(() => station.View.Service.InService, TimeSpan.FromSeconds(3), () => "reset never reached the station\n" + Tail(station));
        Assert.True(TryPresent(station, "Good").Ok);
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed after the reset\n" + Tail(station));
        dispenser.Dispose();
    }

    [Fact]
    public void E_stop_over_the_socket_keeps_its_final_snapshot_and_the_reset_closes_the_sale_with_no_hold_left()
    {
        var dispenser = NewDispenser();
        var port = dispenser.Port;
        var poweredOff = new ManualResetEventSlim();
        dispenser.PoweredOff += poweredOff.Set;
        var card = TestCards.Get("Good");
        Money before, atStop;
        using (var station = NewStation(port, "STN-1"))
        {
            WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
            before = station.Issuer.AvailableBalance(card.Pan);
            Present(station, "Good");
            WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
            station.StartFueling();
            WaitFor(() => station.Snapshot().Money > new Money(50), TimeSpan.FromSeconds(5), () => "no fuel recorded\n" + Tail(station));
            atStop = station.Snapshot().Money;
            station.EmergencyStop("test");                     // record, final snapshot, power off, down
            Assert.True(poweredOff.Wait(TimeSpan.FromSeconds(5)), "the dispenser never heard the power go");
        }
        var snapshot = StateJournal.Load(Path.Combine(_dir, "state.json"))!;
        Assert.Equal("ESTOP", snapshot.StopReason);              // the station's own link drop did not overwrite it
        Assert.Equal("Fueling", snapshot.Point(1)!.Sale.State);

        // The cashier's reset: power back to the island, then a new station.
        dispenser = NewDispenserOnSamePort(port);
        using var station2 = NewStation(port, "STN-2");
        WaitFor(() => station2.Link.Connected, TimeSpan.FromSeconds(8), () => "second station never connected\n" + Tail(station2));
        Assert.Equal(RecoveryKind.ClosedOut, station2.LastRecovery.Kind);
        Assert.Equal(Volume.Zero, station2.LastRecovery.UnrecordedUpTo);
        // The island kept the delivery across the power cut, so the new station settles from the island's number, which
        // can run a tick past what the old station had seen, and never below it.
        WaitFor(() => station2.Issuer.OpenHolds.Count == 0 && station2.Issuer.Settlements.Any(s => s.SaleId == "P1-S0001"), TimeSpan.FromSeconds(8), () => $"holds {string.Join(",", station2.Issuer.OpenHolds)}\n" + Tail(station2));
        var settled = station2.Issuer.Settlements.Single(s => s.SaleId == "P1-S0001").Final;
        Assert.True(settled >= atStop, $"settled {settled} below the station's own {atStop}");
        Assert.Equal(before - settled, station2.Issuer.AvailableBalance(card.Pan));
        Assert.Contains(station2.Ledger.Snapshot(), e => e.Kind == "recovery.reconciled");
        WaitFor(() => dispenser.Dispenser.Pump.Transactions.Open.Count == 0, TimeSpan.FromSeconds(3), () => "the island still holds the delivery");
        dispenser.Dispose();
    }

    /// <summary>The port the last dispenser held can sit in TIME_WAIT for a moment after it closed. Retry the bind briefly.</summary>
    [Fact]
    public void A_closed_pump_stays_closed_across_a_dispenser_restart_because_the_island_saved_it()
    {
        var dispenser = NewDispenser();
        var port = dispenser.Port;
        using var station = NewStation(port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));

        station.ClosePump("operator");
        WaitFor(() => station.View.Phase == "Closed", TimeSpan.FromSeconds(5), () => "never closed\n" + Tail(station));
        Assert.False(station.TryPresentCard("Good").Ok);

        dispenser.Dispose();                                     // the island reboots
        using var back = NewDispenserOnSamePort(port);
        WaitFor(() => station.Link.Connected && station.View.Phase == "Closed", TimeSpan.FromSeconds(8), () => $"closed state lost on restart: {station.View.Phase} {station.View.Service}\n" + Tail(station));
        Assert.True(station.View.Service.Holds(DiagnosticCodes.PumpClosed.Code));
        Assert.False(station.TryPresentCard("Good").Ok);

        station.OpenPump("cashier");
        WaitFor(() => station.View.Service.InService, TimeSpan.FromSeconds(5), () => "never opened\n" + Tail(station));
        Assert.True(TryPresent(station, "Good").Ok);
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed after opening\n" + Tail(station));
    }

    private DispenserServer NewDispenserOnSamePort(int port)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { return NewDispenser(port); }
            catch (System.Net.Sockets.SocketException) when (sw.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(100); }
        }
    }

    [Fact]
    public void Dispenser_link_lost_then_restored_clears_the_LINK_fault_and_admits_the_next_card()
    {
        var dispenser = NewDispenser();
        var port = dispenser.Port;
        using var station = NewStation(port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling();
        WaitFor(() => station.Snapshot().Volume > Volume.Zero, TimeSpan.FromSeconds(5), () => "no fuel recorded\n" + Tail(station));

        dispenser.Dispose();
        WaitFor(() => station.Snapshot().State == SaleState.Faulted && station.Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(5), () => "station did not fault and settle\n" + Tail(station));
        Assert.Equal("LINK.DISPENSER.LOST", station.Snapshot().FaultCode);
        var (ok, _, reason) = station.TryPresentCard("Good");
        Assert.False(ok);
        Assert.Contains("LINK.DISPENSER.LOST", reason);

        using var back = NewDispenserOnSamePort(port);
        WaitFor(() => station.Snapshot().State == SaleState.Idle, TimeSpan.FromSeconds(5), () => "LINK fault never cleared\n" + Tail(station));
        Assert.Equal("Pump back in service", station.Snapshot().Notice.Headline);
        Assert.True(station.View.Service.InService);
        Assert.True(TryPresent(station, "Good").Ok);
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed after the link returned\n" + Tail(station));
    }

    [Fact]
    public void Station_restart_sends_Stop_first_so_a_valve_left_open_closes_and_the_journal_closes_the_sale()
    {
        using var dispenser = NewDispenser();
        Money atLoss;
        using (var station = NewStation(dispenser.Port, "STN-1"))
        {
            WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
            Present(station, "Good");
            WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
            station.StartFueling();
            WaitFor(() => station.Snapshot().Volume > Volume.Zero && station.Snapshot().Money > new Money(50), TimeSpan.FromSeconds(5), () => "no fuel recorded\n" + Tail(station));
            atLoss = station.Snapshot().Money;
            station.SimulatePowerLoss();                       // the station dies with the valve open on the dispenser
        }
        Assert.Equal(PumpPhase.Dispensing, dispenser.Dispenser.Pump.Phase);   // the dispenser is still pouring on its own limits

        using var station2 = NewStation(dispenser.Port, "STN-2");
        WaitFor(() => station2.Link.Connected, TimeSpan.FromSeconds(5), () => "second station never connected\n" + Tail(station2));
        WaitFor(() => dispenser.Dispenser.Pump.Phase == PumpPhase.Disarmed, TimeSpan.FromSeconds(5), () => $"valve still {dispenser.Dispenser.Pump.Phase}\n" + Tail(station2));
        Assert.Equal(RecoveryKind.ClosedOut, station2.LastRecovery.Kind);
        Assert.True(station2.LastRecovery.Money >= atLoss);
        Assert.True(station2.LastRecovery.UnderRecorded);      // the dispenser kept pouring after the last snapshot; the note says so
        WaitFor(() => dispenser.Log.Snapshot().Any(l => l.Message.Contains("disarmed: station connected")), TimeSpan.FromSeconds(3), () => "no disarm line in the dispenser log");
        Assert.Contains(station2.Ledger.Snapshot(), e => e.Kind == "link.up");
    }

    // ----- The envelope on the wire: numbered frames, answered commands, heartbeats, and the island's transactions.

    [Fact]
    public void Every_command_across_the_socket_is_answered_and_the_frames_arrive_numbered_with_no_gaps()
    {
        using var dispenser = NewDispenser();
        using var station = NewStation(dispenser.Port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        var answers = new List<WireAnswer>();
        station.Link.Answered += a => { lock (answers) answers.Add(a); };

        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling();
        WaitFor(() => station.Snapshot().State is SaleState.Idle && station.Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(20), () => "never settled\n" + Tail(station));
        WaitFor(() => dispenser.Dispenser.Pump.Transactions.Open.Count == 0, TimeSpan.FromSeconds(3), () => "the island still holds the delivery\n" + Tail(station));
        // The clear's answer is the last frame of the sale; it lands a moment after the island forgets the delivery.
        WaitFor(() => station.Ledger.Snapshot().Any(e => e.Kind == "cmd.answer" && e.Detail.StartsWith("Clear P1-S0001")), TimeSpan.FromSeconds(3), () => "the clear was never answered\n" + Tail(station));

        // Arm, Lock and Clear each earned a Done, matched by correlation id, and the ledger has the same story.
        lock (answers) Assert.All(answers, a => Assert.Equal(AnswerCode.Done, a.Code));
        var details = station.Ledger.Snapshot().Where(e => e.Kind == "cmd.answer").Select(e => e.Detail).ToList();
        Assert.Contains(details, d => d.StartsWith("Arm P1-S0001 Done"));
        Assert.Contains(details, d => d.StartsWith("Lock P1-S0001 Done"));
        Assert.Contains(details, d => d.StartsWith("Clear P1-S0001 Done"));
        Assert.DoesNotContain(station.Ledger.Snapshot(), e => e.Kind == "cmd.unanswered");

        // Both sides number their frames from one; nothing was lost or doubled on a healthy socket.
        Assert.True(station.Link.LastSeqSent > 0);
        Assert.True(station.Link.LastSeqReceived > 0);
        Assert.Equal(0, station.Link.Gaps);
        Assert.Equal(0, station.Link.Duplicates);
        Assert.Equal(station.Link.LastSeqSent, dispenser.LastSeqReceived);
        Assert.Equal(dispenser.LastSeqSent, station.Link.LastSeqReceived);
        Assert.NotNull(station.Link.LastHeartbeatAt);
        Assert.True(station.Link.PeerAliveNow);
        Assert.NotNull(dispenser.StationHeartbeatAt);
        // The dispenser answered every command the station ever sent, the connect-time Stop included.
        Assert.Equal(station.Ledger.Snapshot().Count(e => e.Kind == "cmd.answer"), dispenser.Answers);
    }

    [Fact]
    public void A_refused_command_answers_Refused_with_the_reason_and_a_command_past_its_time_to_live_answers_Stale_and_never_runs()
    {
        using var dispenser = NewDispenser();
        using var station = NewStation(dispenser.Port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        var answers = new Dictionary<string, WireAnswer>();
        station.Link.Answered += a => { lock (answers) answers[a.CorrelationId] = a; };

        // A forged token: the authority says no, and the answer says why.
        var refused = station.Link.Command(new PumpMessage.Arm("S9", "not-a-token", null), "S9", TimeSpan.FromSeconds(3));
        WaitFor(() => { lock (answers) return answers.ContainsKey(refused); }, TimeSpan.FromSeconds(3), () => "no answer to the forged arm\n" + Tail(station));
        Assert.Equal(AnswerCode.Refused, answers[refused].Code);
        Assert.False(string.IsNullOrEmpty(answers[refused].Detail));
        Assert.Equal(PumpPhase.Disarmed, dispenser.Dispenser.Pump.Phase);

        // One tick of time to live is gone before the frame crosses the loopback. The pump never sees the close.
        var stale = station.Link.Command(new PumpMessage.CloseForSales("tester"), "S9", TimeSpan.FromTicks(1));
        WaitFor(() => { lock (answers) return answers.ContainsKey(stale); }, TimeSpan.FromSeconds(3), () => "no answer to the stale command\n" + Tail(station));
        Assert.Equal(AnswerCode.Stale, answers[stale].Code);
        Assert.Equal(1, dispenser.StaleCommands);
        Thread.Sleep(200);
        Assert.False(dispenser.Dispenser.Pump.ClosedForSales);
        Assert.Equal(PumpPhase.Disarmed, dispenser.Dispenser.Pump.Phase);
    }

    [Fact]
    public void A_dispenser_that_stops_its_heartbeat_with_the_socket_open_faults_the_sale_as_SILENT_and_is_welcomed_back()
    {
        var beat = TimeSpan.FromMilliseconds(200);                 // three missed beats is 600 ms
        using var dispenser = NewDispenser(heartbeat: beat);
        using var station = NewStation(dispenser.Port, "STN-1", heartbeat: beat);
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));

        dispenser.HeartbeatsPaused = true;
        WaitFor(() => station.Snapshot().State == SaleState.Faulted, TimeSpan.FromSeconds(5), () => "station never noticed the silence\n" + Tail(station));
        Assert.Equal("LINK.DISPENSER.SILENT", station.Snapshot().FaultCode);
        Assert.False(station.Link.PeerAliveNow);
        Assert.True(station.Link.Connected);                      // the socket never closed; the device went quiet
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "link.silent");

        dispenser.HeartbeatsPaused = false;
        WaitFor(() => station.Ledger.Snapshot().Any(e => e.Kind == "link.alive"), TimeSpan.FromSeconds(3), () => "heartbeats never came back\n" + Tail(station));
        Assert.True(station.Link.PeerAliveNow);
    }

    [Fact]
    public void A_dispenser_that_swallows_commands_leaves_the_arm_unanswered_and_the_station_faults_the_sale_instead_of_waiting_forever()
    {
        using var dispenser = NewDispenser();
        using var station = NewStation(dispenser.Port, "STN-1", commandTimeout: TimeSpan.FromSeconds(1));
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));

        dispenser.CommandsDropped = true;
        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.Faulted, TimeSpan.FromSeconds(6), () => "the unanswered arm never faulted the sale\n" + Tail(station));
        Assert.Equal("LINK.DISPENSER.SILENT", station.Snapshot().FaultCode);
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "cmd.unanswered" && e.Detail.StartsWith("Arm "));
        Assert.Equal(PumpPhase.Disarmed, dispenser.Dispenser.Pump.Phase);
        WaitFor(() => station.Issuer.OpenHolds.Count == 0, TimeSpan.FromSeconds(5), () => "the hold was never given back\n" + Tail(station));
    }

    [Fact]
    public void A_lost_clear_is_found_again_after_the_island_reboots_because_the_buffer_is_on_disk()
    {
        var dispenser = NewDispenser();
        var port = dispenser.Port;
        using var station = NewStation(port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Phase != "Offline", TimeSpan.FromSeconds(5), () => "station never connected\n" + Tail(station));
        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling();
        WaitFor(() => station.Snapshot().Volume > Volume.Zero, TimeSpan.FromSeconds(5), () => "no fuel recorded\n" + Tail(station));

        // The island stops hearing the station just as the driver hangs up: the delivery is written, the clear is lost.
        dispenser.CommandsDropped = true;
        station.HangNozzle();                                     // this message is dropped too; the pump's own limits end the pour
        WaitFor(() => dispenser.Dispenser.Pump.Transactions.Open.Count == 1, TimeSpan.FromSeconds(10), () => "the island never wrote the delivery\n" + Tail(station));
        WaitFor(() => station.Snapshot().State == SaleState.Idle && station.Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(20), () => "station never settled\n" + Tail(station));
        var payable = dispenser.Dispenser.Pump.Transactions.Open.Single();
        Assert.Equal("P1-S0001", payable.SaleId);
        Assert.Equal(TransactionState.Payable, payable.State);
        WaitFor(() => station.Ledger.Snapshot().Any(e => e.Kind == "cmd.unanswered" && e.Detail.StartsWith("Clear ")), TimeSpan.FromSeconds(6), () => "the dropped clear was never reported\n" + Tail(station));

        dispenser.Dispose();                                     // the island reboots with the delivery still on its disk
        using var back = NewDispenserOnSamePort(port);
        Assert.Equal(["P1-S0001"], back.Dispenser.Pump.Transactions.Open.Select(t => t.SaleId));
        WaitFor(() => station.Link.Connected && station.Link.RemoteRunId == back.RunId, TimeSpan.FromSeconds(8), () => "station never reconnected\n" + Tail(station));

        // On connect the station asks what the island holds, recognises its own finished sale, and clears it.
        WaitFor(() => back.Dispenser.Pump.Transactions.Open.Count == 0, TimeSpan.FromSeconds(5), () => "the delivery was never cleared\n" + Tail(station));
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "tx.reported" && e.Detail.Contains("P1-S0001"));
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "tx.clear" && e.Detail.Contains("clear sent again"));
        Assert.DoesNotContain(station.Ledger.Snapshot(), e => e.Kind == "tx.orphan");
        Assert.True(TryPresent(station, "Good").Ok);
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed after the reboot\n" + Tail(station));
    }

    // ----- Slice 4: the price board is a write the island answers and keeps; the totals are a read.

    [Fact]
    public void The_station_writes_its_price_board_on_connect_and_a_change_survives_an_island_reboot_and_a_station_restart()
    {
        var dispenser = NewDispenser();
        var port = dispenser.Port;
        var station = NewStation(port, "STN-1");
        WaitFor(() => station.Link.Connected && station.View.Prices is not null, TimeSpan.FromSeconds(5), () => "no price board in the view\n" + Tail(station));
        Assert.True(Pricing.Default.SameAs(new Pricing(station.View.Prices!)));
        WaitFor(() => station.Ledger.Snapshot().Any(e => e.Kind == "cmd.answer" && e.Detail.StartsWith("SetPrices") && e.Detail.Contains("Done unchanged")), TimeSpan.FromSeconds(3), () => "the connect-time write was never answered\n" + Tail(station));

        // The back office reprices Regular. The island answers, bills by it, and the station's view shows the island's board.
        var (ok, reason) = station.AdminSetPrice("1234", Grade.Regular, 3999);
        Assert.True(ok, reason);
        WaitFor(() => station.View.Prices?[Grade.Regular] == 3999, TimeSpan.FromSeconds(5), () => "the new price never came back in the view\n" + Tail(station));
        Assert.Equal(3999, ((Pricing)dispenser.Dispenser.Pump.Prices).PriceOf(Grade.Regular));
        WaitFor(() => station.Ledger.Snapshot().Count(e => e.Kind == "cmd.answer" && e.Detail.StartsWith("SetPrices") && e.Detail.EndsWith(" Done")) == 1, TimeSpan.FromSeconds(3), () => "the write was never answered Done\n" + Tail(station));
        Assert.Contains(dispenser.Ledger.Snapshot(), e => e.Kind == "price.set" && e.Detail.StartsWith("admin:") && e.Detail.Contains("Regular $3.999"));
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "admin.prices" && e.Detail == "Regular $3.499 -> $3.999");

        // The island reboots. Its state file hands the board back before any station speaks to it.
        dispenser.Dispose();
        WaitFor(() => !station.Link.Connected, TimeSpan.FromSeconds(3), () => "link never dropped");
        var back = NewDispenserOnSamePort(port);
        Assert.Equal(3999, ((Pricing)back.Dispenser.Pump.Prices).PriceOf(Grade.Regular));
        Assert.Contains(back.Log.Snapshot(), l => l.Message.Contains("prices from the state file"));
        WaitFor(() => station.Link.Connected && station.Link.RemoteRunId == back.RunId && station.View.Prices?[Grade.Regular] == 3999, TimeSpan.FromSeconds(8), () => "station never reconnected with the board\n" + Tail(station));

        // The station restarts. Its journal carries the operator's board, so the connect-time write is the new price, never the built-in one.
        station.Dispose();
        station = NewStation(port, "STN-2");
        using var _ = station;
        Assert.Equal(3999, station.Prices.PriceOf(Grade.Regular));
        WaitFor(() => station.Link.Connected && station.View.Prices?[Grade.Regular] == 3999, TimeSpan.FromSeconds(8), () => "second station lost the board\n" + Tail(station));
        Thread.Sleep(300);
        Assert.Equal(3999, ((Pricing)back.Dispenser.Pump.Prices).PriceOf(Grade.Regular));
        Assert.DoesNotContain(back.Ledger.Snapshot(), e => e.Kind == "price.set");    // the restore and both connect-time writes changed nothing
        back.Dispose();
    }

    [Fact]
    public void A_read_of_totals_over_the_socket_returns_the_islands_counters_after_a_sale()
    {
        using var dispenser = NewDispenser();
        using var station = NewStation(dispenser.Port, "STN-1");
        WaitFor(() => station.Link.Connected && station.LastTotals is not null, TimeSpan.FromSeconds(5), () => "the connect-time read of totals never answered\n" + Tail(station));
        Assert.Equal(Volume.Zero, station.LastTotals!.Totalizer);
        Assert.Empty(station.LastTotals.ByGrade);

        Present(station, "Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling(Grade.Premium);
        WaitFor(() => station.Snapshot().State is SaleState.Idle && station.Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(20), () => "never settled\n" + Tail(station));
        var sale = station.Snapshot();

        station.ReadTotals();
        WaitFor(() => station.LastTotals!.LastTxSeq == 1, TimeSpan.FromSeconds(3), () => "the read never answered\n" + Tail(station));
        var totals = station.LastTotals!;
        Assert.Equal(dispenser.Dispenser.Totalizer.Total, totals.Totalizer);
        Assert.Equal(sale.Volume, totals.Totalizer);
        var premium = Assert.Single(totals.ByGrade);
        Assert.Equal((Grade.Premium, sale.Volume, sale.Money, 1L), (premium.Grade, premium.Volume, premium.Money, premium.Deliveries));
        Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "totals.read" && e.Detail.Contains($"Premium {sale.Volume} {sale.Money} in 1") && e.Detail.EndsWith("last tx #1"));
        // A read changes nothing on the island: no ledger line there for it.
        Assert.DoesNotContain(dispenser.Ledger.Snapshot(), e => e.Kind.StartsWith("totals"));
    }

    [Fact]
    public void Two_islands_over_two_sockets_fuel_at_once_and_losing_one_faults_only_its_own_sale()
    {
        var (pump1, pump2) = NewPair();
        using var station = NewStation(pump1.Port, "STN-1", pumps: 2);
        try
        {
            WaitFor(() => station.Points.All(p => p.Link.Connected && p.View.Phase != "Offline"), TimeSpan.FromSeconds(5), () => "a station link never connected\n" + Tail(station));
            Assert.Equal(pump1.RunId, station.Point(1).Link.RemoteRunId);
            Assert.Equal(pump2.RunId, station.Point(2).Link.RemoteRunId);
            Assert.Equal(["PUMP-01", "PUMP-02"], station.Points.Select(p => p.View.PumpId));
            // Each island was addressed as its own node and refused nothing.
            Assert.DoesNotContain(pump1.Log.Snapshot(), l => l.Message.Contains("refused: WrongDest"));
            Assert.DoesNotContain(pump2.Log.Snapshot(), l => l.Message.Contains("refused: WrongDest"));

            Present(station.Point(1), "Good");
            Present(station.Point(2), "Good");
            WaitFor(() => station.Points.All(p => p.Snapshot().State == SaleState.ReadyToPump), TimeSpan.FromSeconds(5), () => "a pump never armed\n" + Tail(station));
            station.Point(1).StartFueling(Grade.Regular);
            station.Point(2).StartFueling(Grade.Premium);
            WaitFor(() => station.Points.All(p => p.Snapshot().State == SaleState.Fueling), TimeSpan.FromSeconds(5), () => "a pump never fueled\n" + Tail(station));
            WaitFor(() => station.Point(2).Snapshot().Volume > Volume.Zero, TimeSpan.FromSeconds(5), () => "pump 2 moved nothing\n" + Tail(station));

            // Pump 1's island dies mid-pour. Only pump 1's sale hears about it.
            pump1.Dispose();
            WaitFor(() => station.Point(1).Snapshot().State == SaleState.Faulted || station.Point(1).Snapshot().FaultCode == DiagnosticCodes.LinkDispenserLost.Code, TimeSpan.FromSeconds(5), () => "pump 1 never faulted\n" + Tail(station));
            Assert.Equal(DiagnosticCodes.LinkDispenserLost.Code, station.Point(1).Snapshot().FaultCode);
            Assert.True(station.Point(2).Link.Connected);
            Assert.Equal(SaleState.Fueling, station.Point(2).Snapshot().State);
            Assert.Null(station.Point(2).Snapshot().FaultCode);

            // Pump 2 finishes its $2 on its own.
            WaitFor(() => station.Point(2).Snapshot().State is SaleState.Idle or SaleState.Completing && station.Point(2).Snapshot().Hold == HoldStatus.Settled, TimeSpan.FromSeconds(20), () => "pump 2 never settled\n" + Tail(station));
            Assert.Equal(Money.FromDollars(2), station.Point(2).Snapshot().Money);
            Assert.Equal(station.Point(2).Snapshot().Volume, pump2.Dispenser.Totalizer.Total);
            // Pump 1's sale settled for what its island recorded before the link went.
            WaitFor(() => station.Point(1).Snapshot().Hold is HoldStatus.Settled or HoldStatus.Released, TimeSpan.FromSeconds(10), () => "pump 1 never settled\n" + Tail(station));
            Assert.Empty(station.Issuer.OpenHolds);

            // The one ledger: pump 2's link never dropped, pump 1's did, and each line says which.
            var downs = station.Ledger.Snapshot().Where(e => e.Kind == "link.down").Select(e => e.Detail).ToList();
            Assert.Single(downs);
            Assert.Contains("PUMP-01", downs[0]);
            Assert.Contains(station.Ledger.Snapshot(), e => e.Kind == "sale.close" && e.Detail.StartsWith("P2-S0001 "));
        }
        finally
        {
            pump1.Dispose();
            pump2.Dispose();
        }
    }

    private static void Present(FuellingPoint point, string card)
    {
        point.PresentCard(card);
        WaitFor(() => point.Snapshot().State == SaleState.CardRead, TimeSpan.FromSeconds(5), () => $"keypad never asked on pump {point.Number}");
        point.EnterCode(TestCards.Zip);
    }
}
