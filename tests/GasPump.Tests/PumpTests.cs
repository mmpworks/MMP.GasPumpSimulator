using GasPump.Core;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GasPump.Tests;

public class PumpTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private sealed class Rig
    {
        public FakeTimeProvider Clock = new(SimHarness.Start);
        public EventLedger Ledger;
        public Totalizer Totalizer = new();
        public Meter Meter = new();
        public PumpAuthority Authority;
        public PumpController Pump;
        public HmacAuthorizationSigner Signer = new(Key);
        public HardLimits Limits;

        public Rig(HardLimits? limits = null, TaperPolicy? taper = null)
        {
            Limits = limits ?? HardLimits.Default;
            Ledger = new EventLedger(Clock);
            Authority = new PumpAuthority(new HmacAuthorizationVerifier(Key, "PUMP-07", new NonceStore(), TimeSpan.FromSeconds(5)), Limits, Ledger, Clock);
            Pump = new PumpController(Authority, new Hydraulics(FlowRate.GallonsPerMinute(10)), Meter, Totalizer, Ledger, Pricing.Default, taper ?? TaperPolicy.Default, Clock);
        }

        public string Token(string saleId, Money ceiling, Volume? max = null) =>
            Signer.Issue(saleId, "PUMP-07", ceiling, max ?? Volume.FromGallons(50), Clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();

        public List<PumpTick> RunToStop(int maxTicks = 20_000)
        {
            var trace = new List<PumpTick>();
            for (int i = 0; i < maxTicks; i++)
            {
                Clock.Advance(Tick);
                var t = Pump.Tick(Tick);
                trace.Add(t);
                if (t.Stopped) return trace;
            }
            throw new TimeoutException("pump never stopped");
        }
    }

    [Fact]
    public void Taper_policy_is_full_then_falls_then_floors()
    {
        var taper = TaperPolicy.Default;
        var ceiling = Money.FromDollars(30);
        Assert.Equal(1.0, taper.Opening(Money.FromDollars(30), ceiling));
        Assert.Equal(1.0, taper.Opening(Money.FromDollars(6), ceiling));          // taper starts at $6 left
        Assert.InRange(taper.Opening(Money.FromDollars(3), ceiling), 0.49, 0.51);
        Assert.Equal(0.08, taper.Opening(new Money(1), ceiling));
        Assert.Equal(0.0, taper.Opening(Money.Zero, ceiling));
    }

    [Fact]
    public void Thirty_dollar_sale_lands_on_3000_cents_never_over_with_a_visible_taper()
    {
        var rig = new Rig();
        var (ok, _, armed) = rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(30)), Grade.Regular);
        Assert.True(ok);
        Assert.False(armed!.Clamped);
        Assert.True(rig.Pump.LiftNozzle());

        var trace = rig.RunToStop();

        Assert.All(trace, t => Assert.True(t.Money.Cents <= 3000, $"{t.Money} over ceiling"));
        var last = trace[^1];
        Assert.Equal(3000, last.Money.Cents);
        Assert.True(last.CeilingReached);
        Assert.Equal(Pricing.Default.MaxVolumeFor(Grade.Regular, Money.FromDollars(30)), last.Volume);
        Assert.Equal(last.Volume, rig.Totalizer.Total);

        var phases = trace.Select(t => t.TaperPhase).Distinct().ToList();
        Assert.Equal(["full", "taper", "creep", "stopped"], phases);

        var fullFlow = trace.First().Flow.MilliGallonsPerSecond;
        var taperFlows = trace.Where(t => t.TaperPhase == "taper").Select(t => t.Flow.MilliGallonsPerSecond).ToList();
        Assert.NotEmpty(taperFlows);
        Assert.All(taperFlows, f => Assert.True(f < fullFlow));
        for (int i = 1; i < taperFlows.Count; i++) Assert.True(taperFlows[i] <= taperFlows[i - 1], "taper flow must never rise");

        // The taper costs time. Full flow alone would finish in volume / rate.
        var volume = last.Volume.MilliGallons;
        var fullOnlyTicks = (int)Math.Ceiling(volume / 17.0);
        // Taper covers the last 20 percent at an average opening near one half, so expect about 1.2x.
        Assert.True(trace.Count > fullOnlyTicks * 1.12, $"taper should add time: {trace.Count} ticks vs {fullOnlyTicks} at full flow");

        Assert.Equal(0, rig.Ledger.Verify());
    }

    [Fact]
    public void Volume_cap_stops_the_sale_before_the_money_does()
    {
        var rig = new Rig();
        // $500 ceiling, 50 gallon cap. 50 gal of regular is $174.95, so volume wins.
        rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(500)), Grade.Regular);
        rig.Pump.LiftNozzle();
        var last = rig.RunToStop(40_000)[^1];
        Assert.Equal(Volume.FromGallons(50), last.Volume);
        Assert.Equal(17495, last.Money.Cents);
    }

    [Fact]
    public void Spoofed_pulses_during_dispensing_stop_the_pump_dead()
    {
        var rig = new Rig();
        rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(30)), Grade.Regular);
        rig.Pump.LiftNozzle();
        rig.Clock.Advance(Tick); rig.Pump.Tick(Tick);
        rig.Meter.InjectPulses(500);   // half a gallon in 100 ms, five times what the valve can pass
        rig.Clock.Advance(Tick);
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(PumpPhase.Faulted, t.Phase);
        Assert.Contains("pulser reported", t.Fault);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "pump.fault");
    }

    [Fact]
    public void Fuel_moving_while_disarmed_is_a_fault()
    {
        var rig = new Rig();
        rig.Meter.InjectPulses(5);
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(PumpPhase.Faulted, t.Phase);
        Assert.Contains("while Disarmed", t.Fault);
    }

    [Fact]
    public void Nozzle_lift_without_authorization_opens_nothing()
    {
        var rig = new Rig();
        Assert.False(rig.Pump.LiftNozzle());
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(Volume.Zero, t.Volume);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "nozzle.lift.ignored");
    }

    [Fact]
    public void Sale_stops_at_the_hard_duration_limit()
    {
        var rig = new Rig(HardLimits.Default with { MaxSaleDuration = TimeSpan.FromSeconds(2) });
        rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(500)), Grade.Regular);
        rig.Pump.LiftNozzle();
        var trace = rig.RunToStop();
        Assert.InRange(trace.Count, 19, 22);
        Assert.False(trace[^1].CeilingReached);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Detail.Contains("max sale duration"));
    }

    [Fact]
    public void Hang_mid_sale_closes_the_valve_and_keeps_the_money_so_far()
    {
        var rig = new Rig();
        rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(30)), Grade.Regular);
        rig.Pump.LiftNozzle();
        for (int i = 0; i < 50; i++) { rig.Clock.Advance(Tick); rig.Pump.Tick(Tick); }
        var before = rig.Pump.Money;
        rig.Pump.HangNozzle();
        Assert.Equal(PumpPhase.Stopped, rig.Pump.Phase);
        rig.Clock.Advance(Tick);
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(before, t.Money);
        Assert.True(before > Money.Zero);
    }
}
