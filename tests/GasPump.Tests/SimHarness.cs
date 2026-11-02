using System.Diagnostics;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using Microsoft.Extensions.Time.Testing;

namespace GasPump.Tests;

/// <summary>
/// Drives a Simulation with a fake clock. Advance moves time in tick-sized
/// steps and waits for every worker to go quiet after each one, so a test
/// reads top to bottom with no sleeps and no races.
/// </summary>
public sealed class SimHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public SimHarness(string strategy, SimulationOptions? options = null, IssuerSimulator? issuer = null)
    {
        Clock = new FakeTimeProvider(Start);
        Options = options ?? SimulationOptions.Default;
        IDispatchStrategy dispatch = strategy switch
        {
            "threaded" => new ThreadedDispatch(Clock),
            "channel" => new ChannelDispatch(Clock),
            "asyncproxy" => new AsyncProxyDispatch(Clock),
            _ => throw new ArgumentException(strategy),
        };
        Sim = new Simulation(dispatch, Clock, Options, issuer);
        Sim.SaleChanged += s => Snapshots.Add(s);
    }

    public FakeTimeProvider Clock { get; }
    public Simulation Sim { get; }
    public SimulationOptions Options { get; }
    public List<SaleSnapshot> Snapshots { get; } = [];

    public static IEnumerable<object[]> Strategies => [["threaded"], ["channel"], ["asyncproxy"]];

    /// <summary>Pump 1's sale. The one-pump tests read this; the two-pump tests read <see cref="Point"/>.</summary>
    public SaleSnapshot State => Sim.Snapshot();

    /// <summary>Fuelling point <paramref name="number"/> of the station, 1-based.</summary>
    public FuellingPoint Point(int number) => Sim.Point(number);

    /// <summary>The whole reader step as a driver does it, at pump 1: card in, then the keypad. Credit types the test ZIP, debit the test PIN.</summary>
    public string Present(string card, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert, string? code = null) =>
        Present(1, card, grade, tender, source, code);

    /// <summary>The reader step at pump <paramref name="pump"/>.</summary>
    public string Present(int pump, string card, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert, string? code = null)
    {
        var id = Point(pump).PresentCard(card, grade, tender, source);
        Settle();
        Point(pump).EnterCode(code ?? CodeFor(tender));
        return id;
    }

    /// <summary>Same as <see cref="Present(string, Grade?, Tender, CardSource, string?)"/>, but reports a refused reader instead of logging it.</summary>
    public (bool Ok, string SaleId, string Reason) TryPresent(string card, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert, string? code = null) =>
        TryPresent(1, card, grade, tender, source, code);

    public (bool Ok, string SaleId, string Reason) TryPresent(int pump, string card, Grade? grade = null, Tender tender = Tender.Credit, CardSource source = CardSource.PumpInsert, string? code = null)
    {
        var r = Point(pump).TryPresentCard(card, grade, tender, source);
        if (!r.Ok) return r;
        Settle();
        Point(pump).EnterCode(code ?? CodeFor(tender));
        return r;
    }

    public static string CodeFor(Tender tender) => tender == Tender.Debit ? TestCards.Pin : TestCards.Zip;

    /// <summary>
    /// Let the workers finish whatever is queued, without moving the clock.
    /// Quiet must hold for a short stretch of real time, because a pool
    /// continuation can be a few hundred microseconds from landing while every
    /// counter still reads zero.
    /// </summary>
    public void Settle(double quietForMs = 1.0, int realMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        long quietSince = -1;
        var spin = new SpinWait();
        while (sw.ElapsedMilliseconds < realMs)
        {
            if (Sim.IsQuiet)
            {
                if (quietSince < 0) quietSince = sw.ElapsedTicks;
                else if ((sw.ElapsedTicks - quietSince) * 1000.0 / Stopwatch.Frequency >= quietForMs) return;
            }
            else quietSince = -1;
            // SpinWait yields the core after a few spins. Thread.Sleep(1) would cost 15 ms on Windows.
            spin.SpinOnce(sleep1Threshold: -1);
            if (spin.Count > 50) { Thread.Yield(); spin.Reset(); }
        }
        throw new TimeoutException($"simulation did not go idle within {realMs} ms; state {State.State}\n{TailLog()}");
    }

    /// <summary>Move the clock forward in tick-sized steps, settling after each.</summary>
    public void Advance(TimeSpan total)
    {
        var step = Options.TickPeriod;
        var moved = TimeSpan.Zero;
        Settle();
        while (moved < total)
        {
            var slice = total - moved < step ? total - moved : step;
            Clock.Advance(slice);
            moved += slice;
            Settle();
        }
    }

    /// <summary>Advance until the predicate holds or the budget runs out. Returns simulated time spent.</summary>
    public TimeSpan AdvanceUntil(Func<bool> done, TimeSpan budget)
    {
        var step = Options.TickPeriod;
        var moved = TimeSpan.Zero;
        Settle();
        while (!done())
        {
            if (moved >= budget) throw new TimeoutException($"condition not met within {budget}; state {State.State} {State.Phase} {State.Money} {State.LastMessage}\n{TailLog()}");
            Clock.Advance(step);
            moved += step;
            Settle();
        }
        return moved;
    }

    public string TailLog(int n = 20) =>
        string.Join("\n", Sim.Log.Snapshot().TakeLast(n).Select(l => $"  {l.At:HH:mm:ss.fff} [{l.Source}] {l.Message}"))
        + $"\n  quiet={Sim.IsQuiet} idle={Sim.IsIdle} pump={Sim.Pump.Phase} {Sim.Counters} clock={Clock.GetUtcNow():HH:mm:ss.fff}";

    /// <summary>Advance until pump 1's sale is in <paramref name="state"/>.</summary>
    public TimeSpan AdvanceUntilState(SaleState state, TimeSpan? budget = null) =>
        AdvanceUntil(() => State.State == state, budget ?? TimeSpan.FromMinutes(15));

    /// <summary>Advance until pump <paramref name="pump"/>'s sale is in <paramref name="state"/>.</summary>
    public TimeSpan AdvanceUntilState(int pump, SaleState state, TimeSpan? budget = null) =>
        AdvanceUntil(() => Point(pump).Snapshot().State == state, budget ?? TimeSpan.FromMinutes(15));

    public void Dispose() => Sim.Dispose();
}
