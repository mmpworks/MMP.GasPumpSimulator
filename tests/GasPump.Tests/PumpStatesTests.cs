using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Pump;
using GasPump.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The two fuelling-point states the forecourt standard has and the pump
/// lacked. Calling: the nozzle is off the boot with no authorization, and the
/// pump is asking for one. Closed: the operator's switch, every part works,
/// every arm is refused. Tested on the controller alone, on the sale machine
/// alone, end to end under every dispatch strategy, and across a restart.
/// </summary>
public class PumpStatesTests : IDisposable
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
    private static readonly byte[] Ikm = Enumerable.Range(1, 32).Select(i => (byte)(i * 11)).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-states-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static SimulationOptions ThirtyDollarHold => SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) };

    // ----- The controller alone.

    private sealed class Rig
    {
        public FakeTimeProvider Clock = new(SimHarness.Start);
        public EventLedger Ledger;
        public PumpController Pump;
        public Hydraulics Hydraulics = new(FlowRate.GallonsPerMinute(10));
        public Meter Meter = new();
        public HmacAuthorizationSigner Signer = new(Key);

        public Rig()
        {
            Ledger = new EventLedger(Clock);
            var authority = new PumpAuthority(new HmacAuthorizationVerifier(Key, "PUMP-07", new NonceStore(), TimeSpan.FromSeconds(5)), HardLimits.Default, Ledger, Clock);
            Pump = new PumpController(authority, Hydraulics, Meter, new Totalizer(), Ledger, Pricing.Default, TaperPolicy.Default, Clock);
        }

        public string Token(string saleId) => Signer.Issue(saleId, "PUMP-07", Money.FromDollars(30), Volume.FromGallons(50), Clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();
        public bool HasLedger(string kind) => Ledger.Snapshot().Any(e => e.Kind == kind);
    }

    [Fact]
    public void A_lift_with_no_authorization_is_a_call_and_hanging_up_withdraws_it()
    {
        var rig = new Rig();
        Assert.Equal(PumpPhase.Disarmed, rig.Pump.Phase);
        Assert.True(rig.Pump.LiftNozzle());                      // the station should hear this one
        Assert.Equal(PumpPhase.Calling, rig.Pump.Phase);
        Assert.True(rig.Pump.NozzleLifted);
        Assert.True(rig.HasLedger("nozzle.lift.calling"));
        Assert.False(rig.Pump.SqueezeLever().Ok);                // nothing opens without an authorization
        Assert.False(rig.Pump.HangNozzle());                     // no delivery ended
        Assert.Equal(PumpPhase.Disarmed, rig.Pump.Phase);
        Assert.False(rig.Pump.NozzleLifted);
        Assert.True(rig.HasLedger("nozzle.hang.calling"));
    }

    [Fact]
    public void A_pump_that_is_calling_arms_with_the_nozzle_already_out_and_fuels_without_a_second_lift()
    {
        var rig = new Rig();
        rig.Pump.LiftNozzle();
        rig.Pump.InsertSpout();                                  // the driver got ahead of the station
        var (ok, reason, _) = rig.Pump.Arm("S1", rig.Token("S1"));
        Assert.True(ok, reason);
        Assert.Equal(PumpPhase.Armed, rig.Pump.Phase);
        Assert.True(rig.Pump.NozzleLifted);                      // the arm keeps what the driver already did
        Assert.True(rig.Pump.SpoutInserted);
        Assert.True(rig.Pump.SelectGrade(Grade.Regular).Ok);
        var squeeze = rig.Pump.SqueezeLever();
        Assert.True(squeeze.Ok, squeeze.Reason);
        Assert.True(squeeze.FirstPour);
        Assert.Equal(PumpPhase.Dispensing, rig.Pump.Phase);
    }

    [Fact]
    public void Closing_an_idle_pump_refuses_every_arm_with_its_own_code_and_opening_admits_the_next()
    {
        var rig = new Rig();
        rig.Pump.CloseForSales("operator");
        Assert.Equal(PumpPhase.Closed, rig.Pump.Phase);
        Assert.True(rig.Pump.ClosedForSales);
        Assert.True(rig.Pump.Service.Holds(DiagnosticCodes.PumpClosed.Code));
        Assert.False(rig.Pump.Service.InService);
        Assert.True(rig.HasLedger("pump.closed"));

        var (ok, reason, _) = rig.Pump.Arm("S1", rig.Token("S1"));
        Assert.False(ok);
        Assert.Equal(DiagnosticCodes.PumpClosed.Code, reason);
        Assert.Contains(rig.Ledger.Snapshot(), e => e.Kind == "arm.refused" && e.Detail.Contains("PUMP.OPR.CLOSED"));

        Assert.False(rig.Pump.LiftNozzle());                     // a lift on a closed pump is recorded and goes nowhere
        Assert.Equal(PumpPhase.Closed, rig.Pump.Phase);

        rig.Pump.OpenForSales("cashier");
        Assert.Equal(PumpPhase.Disarmed, rig.Pump.Phase);
        Assert.True(rig.Pump.Service.InService);
        Assert.True(rig.Pump.Arm("S2", rig.Token("S2")).Ok);
    }

    [Fact]
    public void Closing_mid_delivery_waits_for_the_stop_and_then_takes_effect()
    {
        var rig = new Rig();
        Assert.True(rig.Pump.Arm("S1", rig.Token("S1")).Ok);
        rig.Pump.SelectGrade(Grade.Regular); rig.Pump.LiftNozzle(); rig.Pump.InsertSpout();
        Assert.True(rig.Pump.SqueezeLever().Ok);
        for (int i = 0; i < 20; i++) { rig.Clock.Advance(TimeSpan.FromMilliseconds(100)); rig.Pump.Tick(TimeSpan.FromMilliseconds(100)); }
        Assert.True(rig.Pump.Volume > Volume.Zero);

        rig.Pump.CloseForSales("operator");
        Assert.Equal(PumpPhase.Dispensing, rig.Pump.Phase);      // the driver is never cut off
        Assert.True(rig.Pump.ClosedForSales);
        for (int i = 0; i < 5; i++) { rig.Clock.Advance(TimeSpan.FromMilliseconds(100)); rig.Pump.Tick(TimeSpan.FromMilliseconds(100)); }
        Assert.Equal(PumpPhase.Dispensing, rig.Pump.Phase);      // still pouring

        Assert.True(rig.Pump.HangNozzle());
        Assert.Equal(PumpPhase.Closed, rig.Pump.Phase);          // the pending close lands at the stop
        Assert.False(rig.Pump.Arm("S2", rig.Token("S2")).Ok);
    }

    [Fact]
    public void A_close_during_a_fault_shows_once_the_part_is_fixed()
    {
        var rig = new Rig();
        rig.Hydraulics.SupplyAvailable = false;
        Assert.True(rig.Pump.Arm("S1", rig.Token("S1")).Ok == false);   // supply fault blocks the arm
        rig.Pump.EmergencyStop(DiagnosticCodes.PumpSupplyNoFlow, "test");
        Assert.Equal(PumpPhase.Faulted, rig.Pump.Phase);
        rig.Pump.CloseForSales("operator");
        Assert.Equal(PumpPhase.Faulted, rig.Pump.Phase);         // the fault still shows; the close is remembered
        rig.Hydraulics.SupplyAvailable = true;
        Assert.True(rig.Pump.ReturnToService());
        Assert.Equal(PumpPhase.Closed, rig.Pump.Phase);          // fixed, and closed
        rig.Pump.OpenForSales("cashier");
        Assert.Equal(PumpPhase.Disarmed, rig.Pump.Phase);
    }

    [Fact]
    public void Closed_sorts_first_among_the_conditions_so_a_refusal_names_it()
    {
        var rig = new Rig();
        rig.Hydraulics.BreakawaySeparated = true;
        rig.Pump.CloseForSales("operator");
        Assert.Equal([DiagnosticCodes.PumpClosed.Code, DiagnosticCodes.PumpHoseBreakaway.Code], rig.Pump.Service.Codes);
    }

    // ----- The sale machine alone.

    [Fact]
    public void An_idle_sale_notes_a_lift_shows_a_calling_notice_and_forgets_it_on_the_hang()
    {
        var m = new SaleStateMachine("S1");
        var c = m.Apply(new SaleEvent.NozzleLifted("S1"));
        Assert.Equal(SaleState.Idle, m.State);
        Assert.True(m.NozzleOut);
        Assert.DoesNotContain(c, x => x is not SaleCommand.Log);
        Assert.StartsWith("Nozzle lifted", m.Notice().Headline);
        Assert.Contains("Hang the nozzle back up to cancel", m.Notice().Detail);

        m.Apply(new SaleEvent.NozzleHung("S1"));
        Assert.False(m.NozzleOut);
        Assert.StartsWith("Insert or tap your card", m.Notice().Headline);
    }

    [Fact]
    public void A_card_after_the_lift_carries_the_nozzle_state_through_to_the_pump_prompt()
    {
        var m = new SaleStateMachine("S1");
        m.Apply(new SaleEvent.NozzleLifted("S1"));
        m.Apply(new SaleEvent.CardPresented("S1", "Good"));
        Assert.Equal(SaleState.CardRead, m.State);
        Assert.True(m.NozzleOut);
        m.Apply(new SaleEvent.SpoutInserted("S1"));             // still legal while the keypad waits
        Assert.True(m.SpoutInserted);
        m.Apply(new SaleEvent.CodeEntered("S1", TestCards.Zip));
        m.Apply(new SaleEvent.Approved("S1", Money.FromDollars(30), "tok"));
        Assert.Equal(SaleState.ReadyToPump, m.State);
        Assert.Contains("Select a grade", m.Notice().Detail);
        m.Apply(new SaleEvent.GradeSelected("S1", Grade.Regular));
        Assert.DoesNotContain("Lift the nozzle", m.Notice().Detail);   // the lift already happened
        Assert.Contains("Squeeze the lever", m.Notice().Detail);
    }

    [Fact]
    public void A_closed_pump_reads_as_out_of_service_on_an_idle_screen_with_the_operators_sentence()
    {
        var m = new SaleStateMachine("S1", service: ServiceStatus.Of([DiagnosticCodes.PumpClosed]));
        Assert.Equal("Out of service: pump switch", m.Notice().Headline);
        Assert.Contains("The operator closed this pump for sales", m.Notice().Detail);
        Assert.Equal(DriverAction.UseAnotherPump, m.Notice().Action);
    }

    // ----- End to end, under every dispatch strategy.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Lift_first_then_pay_arms_a_pump_whose_nozzle_is_already_out(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.LiftNozzle();
        h.Settle();
        Assert.Equal(PumpPhase.Calling.ToString(), h.Sim.View.Phase);
        Assert.Equal(SaleState.Idle, h.State.State);
        Assert.True(h.State.NozzleOut);
        Assert.StartsWith("Nozzle lifted", h.State.Notice.Headline);

        h.Present("Good", source: CardSource.PumpTap);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(PumpPhase.Armed.ToString(), h.Sim.View.Phase);
        Assert.True(h.State.NozzleOut);                          // the pump told the new sale the nozzle is out
        Assert.True(h.Sim.View.NozzleLifted);

        h.Sim.SelectGrade(Grade.Regular);
        h.Sim.InsertSpout();
        h.Sim.SqueezeLever();                                    // no second lift
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(3));
        Assert.True(h.State.Volume > Volume.Zero);
        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(0, h.Sim.Ledger.Verify());
        Assert.Empty(h.Sim.Issuer.OpenHolds);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Hanging_up_a_calling_pump_withdraws_the_call_and_the_screen_goes_back_to_idle(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.LiftNozzle();
        h.Settle();
        Assert.Equal(PumpPhase.Calling.ToString(), h.Sim.View.Phase);
        h.Sim.HangNozzle();
        h.Settle();
        Assert.Equal(PumpPhase.Disarmed.ToString(), h.Sim.View.Phase);
        Assert.False(h.State.NozzleOut);
        Assert.StartsWith("Insert or tap your card", h.State.Notice.Headline);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void The_operator_closes_the_pump_and_the_reader_and_the_counter_both_refuse_until_it_opens(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.ClosePump("operator");
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(PumpPhase.Closed.ToString(), h.Sim.View.Phase);
        Assert.True(h.Sim.View.Service.Holds(DiagnosticCodes.PumpClosed.Code));
        Assert.Equal("Out of service: pump switch", h.State.Notice.Headline);

        var (ok, _, reason) = h.Sim.TryPresentCard("Good", source: CardSource.PumpTap);
        Assert.False(ok);
        Assert.Contains("PUMP.OPR.CLOSED", reason);
        var (ok2, _, reason2) = h.Sim.TryPresentCard("Good", source: CardSource.Station);
        Assert.False(ok2);
        Assert.Contains("PUMP.OPR.CLOSED", reason2);
        Assert.Empty(h.Sim.Issuer.OpenHolds);                    // nothing went to the bank

        // A lift on a closed pump changes nothing on the screen.
        h.Sim.LiftNozzle();
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(PumpPhase.Closed.ToString(), h.Sim.View.Phase);
        Assert.Equal("Out of service: pump switch", h.State.Notice.Headline);
        h.Sim.HangNozzle();

        h.Sim.OpenPump("cashier");
        h.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(h.Sim.View.Service.InService);
        Assert.Equal(PumpPhase.Disarmed.ToString(), h.Sim.View.Phase);
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "pump.closed");
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "pump.opened");
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Closing_while_a_driver_is_fueling_lets_the_sale_finish_and_then_shuts_the_door(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Fueling, TimeSpan.FromSeconds(1));
        h.Advance(TimeSpan.FromSeconds(3));

        h.Sim.ClosePump("operator");
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SaleState.Fueling, h.State.State);          // the sale is untouched
        Assert.Equal(PumpPhase.Dispensing.ToString(), h.Sim.View.Phase);
        var before = h.State.Volume;
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.True(h.State.Volume > before);                    // fuel still moves

        h.Sim.HangNozzle();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromSeconds(5));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(PumpPhase.Closed.ToString(), h.Sim.View.Phase);
        Assert.False(h.Sim.TryPresentCard("Good").Ok);
        Assert.Equal(h.State.Volume, h.Sim.Totalizer.Total);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_pump_closed_while_a_card_waits_for_approval_ends_that_sale_and_gives_the_hold_back(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Single(h.Sim.Issuer.OpenHolds);
        h.Sim.ClosePump("operator");
        h.AdvanceUntil(() => h.State.State is SaleState.Faulted, TimeSpan.FromSeconds(5));
        Assert.Equal(DiagnosticCodes.PumpClosed.Code, h.State.FaultCode);
        h.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(h.Sim.Issuer.OpenHolds);                    // the hold came back
        Assert.Contains("out of service", h.State.Notice.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pump switch", h.State.Notice.Detail);
    }

    // ----- Across a restart.

    [Fact]
    public void A_closed_pump_is_still_closed_after_the_station_restarts()
    {
        var options = ThirtyDollarHold with { Journal = new JournalOptions(_dir, "PUMP-07", Ikm, "RUN-A") };
        using (var h = new SimHarness("channel", options))
        {
            h.Sim.ClosePump("operator");
            h.Advance(TimeSpan.FromSeconds(2));                  // past one journal period
            Assert.Equal(PumpPhase.Closed.ToString(), h.Sim.View.Phase);
            h.Sim.SimulatePowerLoss();
        }
        using var h2 = new SimHarness("channel", options with { Journal = new JournalOptions(_dir, "PUMP-07", Ikm, "RUN-B") });
        h2.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(h2.Sim.View.Service.Holds(DiagnosticCodes.PumpClosed.Code));
        Assert.Equal(PumpPhase.Closed.ToString(), h2.Sim.View.Phase);
        Assert.False(h2.Sim.TryPresentCard("Good").Ok);
        h2.Sim.OpenPump("cashier");
        h2.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(h2.TryPresent("Good").Ok);
    }
}
