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
/// The seam as a socket. A DispenserServer on an ephemeral loopback port and
/// a station Simulation that reaches it over TCP. Real clock on both sides,
/// because two processes do not share a fake one; the flow is fast and the
/// hold is small so a sale lasts about a second.
/// </summary>
public sealed class LinkTests : IDisposable
{
    private static readonly byte[] Ikm = Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-link-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    // 30 gal/min at 100 ms ticks is 50 pulses a tick, under the 60-pulse sanity limit. A $2 hold is about 0.65 gal, about 1.3 s.
    private static readonly DispenserOptions FastPump = new("PUMP-07", HardLimits.Default, Pricing.Default, TaperPolicy.Default, FlowRate.GallonsPerMinute(30), TimeSpan.FromMilliseconds(100));

    private DispenserServer NewDispenser(int port = 0, string? runId = null) =>
        new(new DispenserServerOptions(FastPump, port, _dir, Ikm, RunId: runId), TimeProvider.System, new ThreadedDispatch(TimeProvider.System));

    private Simulation NewStation(int port, string runId) => new(
        new ChannelDispatch(TimeProvider.System), TimeProvider.System,
        SimulationOptions.Default with
        {
            HoldAmount = Money.FromDollars(2),
            NominalFlow = FastPump.NominalFlow,
            TickPeriod = FastPump.TickPeriod,
            Journal = new JournalOptions(_dir, "PUMP-07", Ikm, runId),
            Dispenser = new DispenserLinkOptions("127.0.0.1", port),
        });

    private static void WaitFor(Func<bool> done, TimeSpan budget, Func<string> onTimeout)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > budget) throw new TimeoutException(onTimeout());
            Thread.Sleep(20);
        }
    }

    /// <summary>The sink holds its file open for append; a reader must say it is fine with that.</summary>
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
        station.PresentCard("Good");
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
        WaitFor(() => dispenser.Ledger.Snapshot().Count == station.DispenserLedgerSnapshot().Count, TimeSpan.FromSeconds(3), () => $"ledger mirror short: {station.DispenserLedgerSnapshot().Count} of {dispenser.Ledger.Snapshot().Count}");

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
        station.PresentCard("Good");
        WaitFor(() => station.Snapshot().State == SaleState.ReadyToPump, TimeSpan.FromSeconds(5), () => "never armed\n" + Tail(station));
        station.StartFueling();
        WaitFor(() => station.Snapshot().Volume > Volume.Zero && station.Snapshot().Money > new Money(50), TimeSpan.FromSeconds(5), () => "no fuel recorded\n" + Tail(station));

        dispenser.Dispose();                                   // the plug, pulled: no record, no close-out
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

    /// <summary>The port the last dispenser held can sit in TIME_WAIT for a moment after it closed. Retry the bind briefly.</summary>
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
        station.PresentCard("Good");
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
        Assert.True(station.TryPresentCard("Good").Ok);
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
            station.PresentCard("Good");
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
}
