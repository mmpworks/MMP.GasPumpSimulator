using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using Xunit;
using Xunit.Abstractions;

namespace GasPump.Tests;

/// <summary>
/// One perfect sale, recorded event by event: card, keypad, bank, pump, the
/// hand sequence, a pause for a phone call, the hang-up, the settlement. The
/// trace is what the "happy path" article walks through, so the article and
/// the code are checked against the same list.
/// </summary>
public class HappyPathTraceTests(ITestOutputHelper output)
{
    /// <summary>A telemetry listener that writes down every sale event it is handed, in order.</summary>
    private sealed class Recorder : IStationCapability
    {
        public string Name => "trace";
        public List<string> Events { get; } = [];
        public void OnSaleEvent(SaleEvent e) { lock (Events) Events.Add(e.GetType().Name); }
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void The_happy_path_hits_the_events_in_this_order(string strategy)
    {
        using var h = new SimHarness(strategy, SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) });
        var trace = new Recorder();
        h.Sim.Capabilities.Register(trace);
        var states = new List<string>();
        h.Sim.SaleChanged += s => { lock (states) if (states.Count == 0 || states[^1] != $"{s.State}/{s.Phase}") states.Add($"{s.State}/{s.Phase}"); };

        // 1. Card tapped at the pump. 2. ZIP typed. 3. Bank approves, pump arms.
        h.Sim.PresentCard("Good", source: CardSource.PumpTap);
        h.Settle();
        h.Sim.EnterCode(TestCards.Zip);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));

        // 4. Grade, lift, insert, squeeze. Fuel moves.
        h.Sim.SelectGrade(Grade.Regular);
        h.Sim.LiftNozzle();
        h.Sim.InsertSpout();
        h.Sim.SqueezeLever();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(5));

        // 5. The phone rings. Let go, talk, squeeze again.
        h.Sim.ReleaseLever();
        h.Advance(TimeSpan.FromSeconds(10));
        h.Sim.SqueezeLever();
        h.Advance(TimeSpan.FromSeconds(5));

        // 6. Hang up the nozzle. 7. The bank settles the real amount.
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));
        h.Settle();

        string[] expected =
        [
            nameof(SaleEvent.CardPresented),
            nameof(SaleEvent.CodeEntered),
            nameof(SaleEvent.Approved),
            nameof(SaleEvent.PumpArmed),
            nameof(SaleEvent.GradeSelected),
            nameof(SaleEvent.NozzleLifted),
            nameof(SaleEvent.SpoutInserted),
            nameof(SaleEvent.FlowStarted),
            nameof(SaleEvent.Progress),
            nameof(SaleEvent.FlowPaused),
            nameof(SaleEvent.FlowResumed),
            nameof(SaleEvent.Progress),
            nameof(SaleEvent.NozzleHung),
            nameof(SaleEvent.CompletionSent),
        ];

        // Collapse runs of Progress so the shape of the sale is one line long.
        var shape = new List<string>();
        foreach (var e in trace.Events) if (shape.Count == 0 || shape[^1] != e || e != nameof(SaleEvent.Progress)) shape.Add(e);

        output.WriteLine("events:  " + string.Join(" > ", shape));
        output.WriteLine("states:  " + string.Join(" > ", states));
        output.WriteLine($"ticks:   {trace.Events.Count(e => e == nameof(SaleEvent.Progress))} Progress events");
        output.WriteLine($"money:   {h.State.Money} for {h.State.Volume}, hold {h.State.Hold}");

        Assert.Equal(expected, shape);
        Assert.Equal(
            ["CardRead/idle", "Authorizing/idle", "ReadyToPump/idle", "ReadyToPump/armed", "Fueling/full", "Fueling/paused", "Fueling/full", "Completing/hung", "Idle/idle"],
            states.ToArray());
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(h.State.Money, h.Sim.Issuer.Settlements.Single().Final);
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }
}
