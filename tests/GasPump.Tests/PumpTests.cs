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
        public Hydraulics Hydraulics = new(FlowRate.GallonsPerMinute(10));
        public HmacAuthorizationSigner Signer = new(Key);
        public HardLimits Limits;

        public Rig(HardLimits? limits = null, TaperPolicy? taper = null, IPriceSchedule? prices = null, IReadOnlyList<IDeliveryStage>? stages = null)
        {
            Limits = limits ?? HardLimits.Default;
            Ledger = new EventLedger(Clock);
            Authority = new PumpAuthority(new HmacAuthorizationVerifier(Key, "PUMP-07", new NonceStore(), TimeSpan.FromSeconds(5)), Limits, Ledger, Clock);
            Pump = new PumpController(Authority, Hydraulics, Meter, Totalizer, Ledger, prices ?? Pricing.Default, taper ?? TaperPolicy.Default, Clock, stages);
        }

        /// <summary>What the station does once the money is settled: clear the island's transaction so the next arm is allowed.</summary>
        public void ClearTransactions()
        {
            foreach (var t in Pump.Transactions.Open) Pump.Transactions.Clear(t.TxSeq, "test station");
        }

        public string Token(string saleId, Money ceiling, Volume? max = null) =>
            Signer.Issue(saleId, "PUMP-07", ceiling, max ?? Volume.FromGallons(50), Clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();

        /// <summary>Arm, then the driver's hand sequence: grade, lift, insert, squeeze.</summary>
        public ArmedAuthorization ArmAndPour(Money ceiling, Grade grade = Grade.Regular)
        {
            var (ok, reason, armed) = Pump.Arm("S1", Token("S1", ceiling));
            Assert.True(ok, reason);
            Assert.True(Pump.SelectGrade(grade).Ok);
            Assert.True(Pump.LiftNozzle());
            Assert.True(Pump.InsertSpout());
            var squeeze = Pump.SqueezeLever();
            Assert.True(squeeze.Ok, squeeze.Reason);
            Assert.True(squeeze.FirstPour);
            return armed!;
        }

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

        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) { Clock.Advance(Tick); Pump.Tick(Tick); }
        }
    }

    /// <summary>A part downstream of the meter, as an additive injector would be. Counts what it saw and can be broken.</summary>
    private sealed class CountingStage : IDeliveryStage
    {
        public static readonly DiagnosticCode Code = new("PUMP.TST.BROKEN", "test stage", "the test stage cannot run", ServiceAction.AttendantReset);
        public string Name => "counter";
        public Volume Seen = Volume.Zero;
        public int Arms;
        public bool Broken;
        public void OnArmed(string saleId) { Arms++; Seen = Volume.Zero; }
        public void OnDelivered(Grade grade, Volume delivered) => Seen += delivered;
        public IEnumerable<DiagnosticCode> Conditions() { if (Broken) yield return Code; }
        public bool TryRestoreCondition(string code) { if (code != Code.Code) return false; Broken = true; return true; }
    }

    /// <summary>A price schedule that wraps the price board and adds one flat fee to the sale, as a detergent additive would.</summary>
    private sealed class FlatFee(IPriceSchedule inner, Money fee) : IPriceSchedule
    {
        public Money CentsFor(Grade grade, Volume volume) => inner.CentsFor(grade, volume) + fee;
        // The fee comes off the top, then the board decides how much fuel fits in what is left.
        public Volume MaxVolumeFor(Grade grade, Money money) => money <= fee ? Volume.Zero : inner.MaxVolumeFor(grade, money - fee);
        public string Display(Grade grade) => inner.Display(grade);
        public IReadOnlyList<ChargeLine> Lines(Grade grade, Volume volume) => [.. inner.Lines(grade, volume), new ChargeLine("ADDITIVE", "detergent additive", fee)];
    }

    /// <summary>
    /// A stage that takes the hose in slugs, as an additive injector would. After
    /// the first three seconds of fuel it delivers 16 oz of its product, then lets
    /// 16 oz of fuel through, and repeats until its total is delivered.
    /// </summary>
    private sealed class SlugDiverter(Volume total, Volume slug) : IDeliveryStage
    {
        public string Name => "additive";
        public Volume Delivered = Volume.Zero;
        public int Slugs;
        private bool _holding;
        private Volume _inSlug = Volume.Zero;
        public static readonly TimeSpan Lead = TimeSpan.FromSeconds(3);

        public void OnArmed(string saleId) { Delivered = Volume.Zero; Slugs = 0; _holding = false; _inSlug = Volume.Zero; }

        public void OnDelivered(Grade grade, Volume delivered)
        {
            // Fuel slug: count fuel until a slug's worth has passed, then take the hose again.
            if (_holding || Delivered >= total || Delivered == Volume.Zero) return;
            _inSlug += delivered;
        }

        public bool ClaimsHose(TimeSpan pouredFor)
        {
            if (Delivered >= total || pouredFor < Lead) return false;
            if (_holding) return true;
            if (Delivered == Volume.Zero || _inSlug >= slug) { _holding = true; _inSlug = Volume.Zero; Slugs++; return true; }
            return false;
        }

        public Volume Pour(TimeSpan dt, FlowRate hoseFlow)
        {
            var moved = Volume.Min(hoseFlow.Over(dt), Volume.Min(slug - _inSlug, total - Delivered));
            _inSlug += moved;
            Delivered += moved;
            if (_inSlug >= slug || Delivered >= total) { _holding = false; _inSlug = Volume.Zero; }
            return moved;
        }
    }

    [Fact]
    public void A_stage_that_takes_the_hose_moves_its_product_unmetered_and_every_switch_is_in_the_ledger()
    {
        var oz16 = new Volume(125);                      // 16 fl oz = 0.125 gal
        var stage = new SlugDiverter(total: new Volume(250), slug: oz16);   // 32 oz in two slugs
        var rig = new Rig(stages: [stage]);
        rig.ArmAndPour(Money.FromDollars(30));
        var trace = rig.RunToStop();
        var last = trace[^1];

        // All of the additive went out, in two slugs, and none of it was metered or billed as fuel.
        Assert.Equal(new Volume(250), stage.Delivered);
        Assert.Equal(2, stage.Slugs);
        Assert.Equal(last.Volume, rig.Totalizer.Total);
        Assert.Equal(Pricing.Default.CentsFor(Grade.Regular, last.Volume), last.Money);
        Assert.Equal(3000, last.Money.Cents);
        Assert.True(last.CeilingReached);

        // While the stage held the hose the fuel volume stood still and the phase named the stage.
        var additiveTicks = trace.Where(t => t.TaperPhase == "additive").ToList();
        Assert.NotEmpty(additiveTicks);
        for (int i = 1; i < trace.Count; i++)
            if (trace[i].TaperPhase == "additive") Assert.Equal(trace[i - 1].Volume, trace[i].Volume);

        // Nothing took the hose before three seconds of fuel had run (the 30th tick is the three-second mark, and the first additive slice).
        Assert.All(trace.Take(29), t => Assert.NotEqual("additive", t.TaperPhase));
        Assert.Equal("additive", trace[29].TaperPhase);

        // Four switches: to additive, back to fuel, to additive, back to fuel. Each one is a ledger entry.
        var switches = rig.Ledger.Snapshot().Where(e => e.Kind == "hose.switch").ToList();
        Assert.Equal(4, switches.Count);
        Assert.Contains("fuel -> additive", switches[0].Detail);
        Assert.Contains("additive -> fuel", switches[1].Detail);
        Assert.Equal(0, rig.Ledger.Verify());
    }

    [Fact]
    public void A_fault_while_a_stage_holds_the_hose_bills_only_the_fuel_that_was_metered()
    {
        var stage = new SlugDiverter(total: new Volume(1_000), slug: new Volume(125));
        var rig = new Rig(stages: [stage]);
        rig.ArmAndPour(Money.FromDollars(30));

        PumpTick? t = null;
        for (int i = 0; i < 200 && t?.TaperPhase != "additive"; i++) { rig.Clock.Advance(Tick); t = rig.Pump.Tick(Tick); }
        Assert.Equal("additive", t!.TaperPhase);
        Assert.NotNull(rig.Pump.HoseOwner);
        var fuelBefore = rig.Pump.Volume;

        rig.Pump.EmergencyStop(DiagnosticCodes.PumpHoseBreakaway, "drive-off mid-additive");

        Assert.Equal(PumpPhase.Faulted, rig.Pump.Phase);
        Assert.Equal(fuelBefore, rig.Pump.Volume);
        Assert.Equal(fuelBefore, rig.Totalizer.Total);
        Assert.Equal(Pricing.Default.CentsFor(Grade.Regular, fuelBefore), rig.Pump.Money);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "hose.switch" && e.Detail.Contains("fuel -> additive"));
    }

    [Fact]
    public void Every_tick_itemizes_the_money_and_the_lines_always_sum_to_it()
    {
        var fee = Money.FromDollars(1.50m);
        var rig = new Rig(prices: new FlatFee(Pricing.Default, fee));
        rig.ArmAndPour(Money.FromDollars(30));
        var trace = rig.RunToStop();

        Assert.All(trace.Where(t => t.Volume > Volume.Zero), t => Assert.Equal(t.Money, ChargeLine.Total(t.Lines)));
        var last = trace[^1];
        Assert.Equal(2, last.Lines.Count);
        Assert.Equal(ChargeLine.Fuel, last.Lines[0].Code);
        Assert.Equal(Pricing.Default.CentsFor(Grade.Regular, last.Volume), last.Lines[0].Amount);
        Assert.Equal("ADDITIVE", last.Lines[1].Code);
        Assert.Equal(fee, last.Lines[1].Amount);

        // The plain board answers with one fuel line, and it is the whole money.
        var plain = new Rig();
        plain.ArmAndPour(Money.FromDollars(20));
        var end = plain.RunToStop()[^1];
        Assert.Single(end.Lines);
        Assert.Equal(end.Money, end.Lines[0].Amount);
    }

    [Fact]
    public void A_delivery_stage_sees_every_metered_slice_and_their_sum_is_the_sale_volume()
    {
        var stage = new CountingStage();
        var rig = new Rig(stages: [stage]);
        rig.ArmAndPour(Money.FromDollars(20));
        var last = rig.RunToStop()[^1];

        Assert.Equal(1, stage.Arms);
        Assert.Equal(last.Volume, stage.Seen);
        Assert.Equal(rig.Totalizer.Total, stage.Seen);
        Assert.Equal(2000, last.Money.Cents);
    }

    [Fact]
    public void A_broken_delivery_stage_holds_the_pump_out_of_service_until_it_is_fixed()
    {
        var stage = new CountingStage { Broken = true };
        var rig = new Rig(stages: [stage]);

        var (ok, reason, _) = rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(20)));
        Assert.False(ok);
        Assert.Equal(CountingStage.Code.Code, reason);
        Assert.True(rig.Pump.Service.Holds(CountingStage.Code.Code));
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains(CountingStage.Code.Code));

        stage.Broken = false;
        Assert.True(rig.Pump.Service.InService);
        Assert.True(rig.Pump.Arm("S2", rig.Token("S2", Money.FromDollars(20))).Ok);

        // A saved code comes back to the stage that owns it, and only that one.
        Assert.False(stage.TryRestoreCondition(DiagnosticCodes.PumpHoseBreakaway.Code));
        Assert.True(stage.TryRestoreCondition(CountingStage.Code.Code));
        Assert.True(stage.Broken);
    }

    [Fact]
    public void A_wrapped_price_schedule_changes_the_money_and_the_sale_still_lands_under_the_ceiling()
    {
        // A $1.50 detergent fee on top of the board, once per sale.
        var fee = Money.FromDollars(1.50m);
        var rig = new Rig(prices: new FlatFee(Pricing.Default, fee));
        rig.ArmAndPour(Money.FromDollars(30));
        var trace = rig.RunToStop();
        var last = trace[^1];

        Assert.All(trace, t => Assert.True(t.Money.Cents <= 3000, $"{t.Money} over ceiling"));
        Assert.InRange(last.Money.Cents, 2999, 3000);
        Assert.True(last.CeilingReached);
        // The fee was billed: the same volume on the plain board costs the fee less.
        Assert.Equal(Pricing.Default.CentsFor(Grade.Regular, last.Volume) + fee, last.Money);
        // And the fuel that fits under $30 is the fuel that fits under $28.50 on the board.
        Assert.Equal(Pricing.Default.MaxVolumeFor(Grade.Regular, Money.FromDollars(28.50m)), last.Volume);
        Assert.Equal(0, rig.Ledger.Verify());
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
        var armed = rig.ArmAndPour(Money.FromDollars(30));
        Assert.False(armed.Clamped);

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
        rig.ArmAndPour(Money.FromDollars(500));
        var last = rig.RunToStop(40_000)[^1];
        Assert.Equal(Volume.FromGallons(50), last.Volume);
        Assert.Equal(17495, last.Money.Cents);
    }

    [Fact]
    public void Spoofed_pulses_during_dispensing_stop_the_pump_dead()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(1);
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
        // With no authorization the lift is a call: the station hears it, and the valve stays shut.
        Assert.True(rig.Pump.LiftNozzle());
        Assert.Equal(PumpPhase.Calling, rig.Pump.Phase);
        rig.Pump.InsertSpout();
        Assert.False(rig.Pump.SqueezeLever().Ok);
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(Volume.Zero, t.Volume);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "nozzle.lift.calling");
    }

    [Fact]
    public void Sale_stops_at_the_hard_duration_limit()
    {
        var rig = new Rig(HardLimits.Default with { MaxSaleDuration = TimeSpan.FromSeconds(2) });
        rig.ArmAndPour(Money.FromDollars(500));
        var trace = rig.RunToStop();
        Assert.InRange(trace.Count, 19, 22);
        Assert.False(trace[^1].CeilingReached);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Detail.Contains("max sale duration"));
    }

    [Fact]
    public void Hang_mid_sale_closes_the_valve_and_keeps_the_money_so_far()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(50);
        var before = rig.Pump.Money;
        Assert.True(rig.Pump.HangNozzle());
        Assert.Equal(PumpPhase.Stopped, rig.Pump.Phase);
        rig.Clock.Advance(Tick);
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(before, t.Money);
        Assert.True(before > Money.Zero);
    }

    // ----- Interlocks: grade, spout, lever.

    [Fact]
    public void Lever_does_nothing_until_grade_lift_and_spout_are_all_true()
    {
        var rig = new Rig();
        rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(30)));

        var r1 = rig.Pump.SqueezeLever();
        Assert.False(r1.Ok); Assert.Contains("grade", r1.Reason);

        rig.Pump.SelectGrade(Grade.Plus);
        var r2 = rig.Pump.SqueezeLever();
        Assert.False(r2.Ok); Assert.Contains("lift", r2.Reason);

        rig.Pump.LiftNozzle();
        var r3 = rig.Pump.SqueezeLever();
        Assert.False(r3.Ok); Assert.Contains("spout", r3.Reason);

        rig.Run(5);
        Assert.Equal(Volume.Zero, rig.Pump.Volume);
        Assert.Equal(PumpPhase.Armed, rig.Pump.Phase);

        rig.Pump.InsertSpout();
        var r4 = rig.Pump.SqueezeLever();
        Assert.True(r4.Ok);
        Assert.True(r4.FirstPour);
        rig.Run(5);
        Assert.True(rig.Pump.Volume > Volume.Zero);
        Assert.Equal(3, rig.Ledger.Snapshot().Count(e => e.Kind == "lever.ignored"));
    }

    [Fact]
    public void Release_pauses_and_squeeze_resumes_as_many_times_as_the_driver_likes()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(10);
        var afterFirst = rig.Pump.Volume;

        Assert.True(rig.Pump.ReleaseLever());
        Assert.Equal(PumpPhase.Paused, rig.Pump.Phase);
        rig.Run(10);
        Assert.Equal(afterFirst, rig.Pump.Volume);   // nothing flows while paused

        var again = rig.Pump.SqueezeLever();
        Assert.True(again.Ok);
        Assert.False(again.FirstPour);
        rig.Run(10);
        Assert.True(rig.Pump.Volume > afterFirst);

        rig.Pump.ReleaseLever();
        rig.Pump.SqueezeLever();
        rig.Pump.ReleaseLever();
        Assert.Equal(PumpPhase.Paused, rig.Pump.Phase);
        Assert.Equal(3, rig.Ledger.Snapshot().Count(e => e.Kind == "valve.pause"));
        Assert.Equal(0, rig.Ledger.Verify());
    }

    [Fact]
    public void Pulling_the_nozzle_out_of_the_tank_stops_the_flow()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(5);
        Assert.True(rig.Pump.WithdrawSpout());
        Assert.Equal(PumpPhase.Paused, rig.Pump.Phase);
        Assert.False(rig.Pump.SqueezeLever().Ok);   // still out of the tank
        rig.Pump.InsertSpout();
        Assert.True(rig.Pump.SqueezeLever().Ok);
    }

    [Fact]
    public void Hang_before_any_pour_does_not_end_the_sale()
    {
        var rig = new Rig();
        rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(30)));
        rig.Pump.SelectGrade(Grade.Regular);
        rig.Pump.LiftNozzle();
        Assert.False(rig.Pump.HangNozzle());
        Assert.Equal(PumpPhase.Armed, rig.Pump.Phase);
        Assert.NotNull(rig.Pump.Armed);
    }

    [Fact]
    public void Hang_after_a_paused_pour_ends_the_sale()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(5);
        rig.Pump.ReleaseLever();
        Assert.True(rig.Pump.HangNozzle());
        Assert.Equal(PumpPhase.Stopped, rig.Pump.Phase);
        Assert.Null(rig.Pump.Armed);
    }

    // ----- Failed parts, each with its code.

    [Fact]
    public void Tank_out_mid_sale_stops_with_NOFLOW_after_the_window_and_keeps_the_metered_volume()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(20);
        var metered = rig.Pump.Volume;
        Assert.True(metered > Volume.Zero);

        rig.Hydraulics.SupplyAvailable = false;          // the STP lost prime, or the gauge shut the line
        var trace = rig.RunToStop();
        var last = trace[^1];
        Assert.Equal(PumpPhase.Faulted, last.Phase);
        Assert.Equal(DiagnosticCodes.PumpSupplyNoFlow.Code, last.FaultCode);
        Assert.Contains("[PUMP.STP.NOFLOW]", last.Fault);
        Assert.InRange(trace.Count, 29, 32);             // the three-second no-flow window at 100 ms ticks
        Assert.Equal(metered, rig.Pump.Volume);           // nothing was counted that did not move
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "pump.fault" && e.Detail.Contains("[PUMP.STP.NOFLOW]"));
    }

    [Fact]
    public void Dead_pulser_faults_with_NOPULSE_and_the_unmetered_volume_is_in_the_record()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(10);
        var metered = rig.Pump.Volume;
        var totalizer = rig.Totalizer.Total;

        rig.Meter.PulserDead = true;                      // fuel keeps moving, the count stands still
        var trace = rig.RunToStop();
        var last = trace[^1];
        Assert.Equal(DiagnosticCodes.PumpMeterNoPulse.Code, last.FaultCode);
        Assert.InRange(trace.Count, 3, 4);                // the 300 ms no-pulse window
        Assert.Equal(metered, rig.Pump.Volume);
        Assert.Equal(totalizer, rig.Totalizer.Total);     // the sealed counter only counts what the meter counted
        Assert.True(rig.Meter.UnmeteredVolume > Volume.Zero);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "pump.fault" && e.Detail.Contains("unmetered"));
    }

    [Fact]
    public void Drive_off_faults_with_BREAKAWAY_and_arming_is_refused_until_the_attendant_resets()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(5);
        rig.Hydraulics.BreakawaySeparated = true;         // the coupling sensor
        rig.Clock.Advance(Tick);
        var t = rig.Pump.Tick(Tick);
        Assert.Equal(PumpPhase.Faulted, t.Phase);
        Assert.Equal(DiagnosticCodes.PumpHoseBreakaway.Code, t.FaultCode);

        var (ok, reason, _) = rig.Pump.Arm("S2", rig.Token("S2", Money.FromDollars(30)));
        Assert.False(ok);
        Assert.Equal(DiagnosticCodes.PumpHoseBreakaway.Code, reason);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains("[PUMP.HOSE.BREAKAWAY]"));

        rig.Pump.ResetBreakaway();
        rig.ClearTransactions();   // the station settled the last delivery
        var (okAfter, _, _) = rig.Pump.Arm("S3", rig.Token("S3", Money.FromDollars(30)));
        Assert.True(okAfter);
    }

    [Fact]
    public void Attendant_reset_returns_a_breakaway_faulted_pump_to_disarmed_and_clears_the_fault()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(5);
        rig.Hydraulics.BreakawaySeparated = true;
        rig.Run(1);
        Assert.Equal(PumpPhase.Faulted, rig.Pump.Phase);
        Assert.False(rig.Pump.ReturnToService());        // the coupling is still apart

        rig.Pump.ResetBreakaway();
        Assert.True(rig.Pump.ReturnToService());
        Assert.Equal(PumpPhase.Disarmed, rig.Pump.Phase);
        Assert.Null(rig.Pump.LastFault);
        Assert.Null(rig.Pump.LastFaultCode);
        Assert.True(rig.Pump.Service.InService);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "pump.in-service" && e.Detail.Contains("[PUMP.HOSE.BREAKAWAY]"));
    }

    [Fact]
    public void A_second_condition_keeps_the_pump_faulted_after_the_first_is_cleared()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(5);
        rig.Hydraulics.BreakawaySeparated = true;
        rig.Run(1);
        rig.Hydraulics.SupplyAvailable = false;          // the tank runs dry while the hose is apart

        rig.Pump.ResetBreakaway();
        Assert.False(rig.Pump.ReturnToService());
        Assert.Equal(PumpPhase.Faulted, rig.Pump.Phase);
        Assert.Equal([DiagnosticCodes.PumpSupplyNoFlow.Code], rig.Pump.Service.Codes);
        var (ok, reason, _) = rig.Pump.Arm("S2", rig.Token("S2", Money.FromDollars(30)));
        Assert.False(ok);
        Assert.Equal(DiagnosticCodes.PumpSupplyNoFlow.Code, reason);

        rig.Hydraulics.SupplyAvailable = true;
        Assert.True(rig.Pump.ReturnToService());
        rig.ClearTransactions();   // the station settled the last delivery
        Assert.True(rig.Pump.Arm("S3", rig.Token("S3", Money.FromDollars(30))).Ok);
    }

    [Fact]
    public void Arming_is_refused_while_the_tank_is_dry_or_the_pulser_is_dead()
    {
        var rig = new Rig();
        rig.Hydraulics.SupplyAvailable = false;
        Assert.Equal(DiagnosticCodes.PumpSupplyNoFlow.Code, rig.Pump.Arm("S1", rig.Token("S1", Money.FromDollars(30))).Reason);
        rig.Hydraulics.SupplyAvailable = true;
        rig.Meter.PulserDead = true;
        Assert.Equal(DiagnosticCodes.PumpMeterNoPulse.Code, rig.Pump.Arm("S2", rig.Token("S2", Money.FromDollars(30))).Reason);
        rig.Meter.PulserDead = false;
        rig.ClearTransactions();   // the station settled the last delivery
        Assert.True(rig.Pump.Arm("S3", rig.Token("S3", Money.FromDollars(30))).Ok);
    }

    [Fact]
    public void A_per_sale_fault_stays_faulted_until_the_next_arm()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30));
        rig.Run(5);
        rig.Meter.InjectPulses(5000);
        rig.Run(1);
        Assert.Equal(DiagnosticCodes.PumpMeterOverPulse.Code, rig.Pump.LastFaultCode);
        Assert.True(rig.Pump.Service.InService);
        Assert.False(rig.Pump.ReturnToService());        // nothing to repair; the next arm is the reset
        Assert.Equal(PumpPhase.Faulted, rig.Pump.Phase);
        rig.ClearTransactions();   // the station settled the last delivery
        Assert.True(rig.Pump.Arm("S2", rig.Token("S2", Money.FromDollars(30))).Ok);
        Assert.Null(rig.Pump.LastFaultCode);
    }

    [Fact]
    public void Only_the_per_sale_codes_clear_on_the_next_arm()
    {
        Assert.Equal(
            ["PUMP.CTL.MAXTIME", "PUMP.CTL.OVERCEILING", "PUMP.MTR.OVERPULSE", "PUMP.MTR.STRAY"],
            DiagnosticCodes.All.Where(c => !c.OutOfService).Select(c => c.Code).Order());
    }

    [Fact]
    public void Every_diagnostic_code_names_a_part_and_formats_with_brackets()
    {
        Assert.All(DiagnosticCodes.All, c =>
        {
            Assert.Matches("^[A-Z]+(\\.[A-Z]+){1,2}$", c.Code);   // SUBSYSTEM.PART.FAULT, or SUBSYSTEM.FAULT when the subsystem is the part
            Assert.False(string.IsNullOrWhiteSpace(c.Part));
            Assert.StartsWith($"[{c.Code}] ", c.Format("x"));
            Assert.Same(c, DiagnosticCodes.Find(c.Code));
        });
    }

    [Fact]
    public void Grade_locks_after_the_first_pour()
    {
        var rig = new Rig();
        rig.ArmAndPour(Money.FromDollars(30), Grade.Regular);
        rig.Run(5);
        rig.Pump.ReleaseLever();
        var change = rig.Pump.SelectGrade(Grade.Premium);
        Assert.False(change.Ok);
        Assert.Equal(Grade.Regular, rig.Pump.SelectedGrade);
    }
}
