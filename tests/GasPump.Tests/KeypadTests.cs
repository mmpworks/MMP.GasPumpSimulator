using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The keypad step between the card and the bank. A debit card wants its PIN,
/// a credit card wants the billing ZIP code. Nothing goes to the bank until
/// the digits are in, and the bank is the one that checks them.
/// </summary>
public class KeypadTests
{
    private static SimulationOptions ThirtyDollarHold => SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) };

    // ----- The pure machine.

    [Fact]
    public void A_credit_card_asks_for_a_five_digit_zip_and_a_debit_card_asks_for_a_four_digit_pin()
    {
        var credit = new SaleStateMachine("S1");
        credit.Apply(new SaleEvent.CardPresented("S1", "Good"));
        Assert.Equal(SaleState.CardRead, credit.State);
        Assert.Equal(HoldStatus.None, credit.Hold);
        Assert.Equal(DriverAction.EnterZip, credit.Notice().Action);
        Assert.Contains("5-digit billing ZIP", credit.Notice().Headline);

        var debit = new SaleStateMachine("S2");
        debit.Apply(new SaleEvent.CardPresented("S2", "DebitGoodPin", Tender: Tender.Debit));
        Assert.Equal(DriverAction.EnterPin, debit.Notice().Action);
        Assert.Contains("4-digit PIN", debit.Notice().Headline);
    }

    [Theory]
    [InlineData("1234")]      // too short for a ZIP
    [InlineData("123456")]    // too long
    [InlineData("12a45")]     // a letter
    public void A_code_of_the_wrong_shape_is_refused_at_the_keypad_and_never_reaches_the_bank(string typed)
    {
        var m = new SaleStateMachine("S1");
        m.Apply(new SaleEvent.CardPresented("S1", "Good"));
        var c = m.Apply(new SaleEvent.CodeEntered("S1", typed));
        Assert.Equal(SaleState.CardRead, m.State);
        Assert.DoesNotContain(c, x => x is SaleCommand.RequestAuthorization);
        // The digits themselves never land in a log line.
        Assert.All(c.OfType<SaleCommand.Log>(), l => Assert.DoesNotContain(typed, l.Message));
    }

    [Fact]
    public void The_right_shape_sends_the_code_to_the_bank_and_only_then_requests_the_hold()
    {
        var m = new SaleStateMachine("S1");
        m.Apply(new SaleEvent.CardPresented("S1", "DebitGoodPin", Tender: Tender.Debit));
        var c = m.Apply(new SaleEvent.CodeEntered("S1", TestCards.Pin));
        Assert.Equal(SaleState.Authorizing, m.State);
        Assert.Equal(HoldStatus.Requested, m.Hold);
        Assert.Contains(c, x => x is SaleCommand.RequestAuthorization { Tender: Tender.Debit, Code: TestCards.Pin });
        Assert.All(c.OfType<SaleCommand.Log>(), l => Assert.DoesNotContain(TestCards.Pin, l.Message));
    }

    [Fact]
    public void Walking_away_from_the_keypad_cancels_the_sale_with_nothing_to_reverse()
    {
        var m = new SaleStateMachine("S1");
        m.Apply(new SaleEvent.CardPresented("S1", "Good"));
        var c = m.Apply(new SaleEvent.Timeout("S1", SaleState.CardRead));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.Equal(EndReason.NoCodeEntered, m.End);
        Assert.Equal(HoldStatus.None, m.Hold);
        Assert.DoesNotContain(c, x => x is SaleCommand.Reverse);          // nothing was ever at the bank
        Assert.Contains("Nothing was sent to your bank", m.Notice().Detail);
    }

    [Fact]
    public void Cancel_at_the_keypad_sends_no_reversal_either()
    {
        var m = new SaleStateMachine("S1");
        m.Apply(new SaleEvent.CardPresented("S1", "Good"));
        var c = m.Apply(new SaleEvent.Cancel("S1", "driver pressed cancel"));
        Assert.Equal(SaleState.Cancelled, m.State);
        Assert.DoesNotContain(c, x => x is SaleCommand.Reverse);
    }

    // ----- The bank.

    [Fact]
    public async Task A_wrong_zip_is_an_address_check_decline_and_holds_nothing()
    {
        var (gw, clock, issuer) = Gateway();
        var task = gw.PreAuthorizeAsync("S1", TestCards.Get("Good"), Tender.Credit, "99999", CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var r = await task;
        Assert.False(r.Approved);
        Assert.Equal(nameof(DeclineReason.ZipMismatch), r.ReasonCode);
        Assert.Empty(issuer.OpenHolds);
    }

    [Fact]
    public async Task A_wrong_pin_is_a_pin_decline_and_holds_nothing()
    {
        var (gw, clock, issuer) = Gateway();
        var task = gw.PreAuthorizeAsync("S1", TestCards.Get("DebitGoodPin"), Tender.Debit, "0000", CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var r = await task;
        Assert.False(r.Approved);
        Assert.Equal(nameof(DeclineReason.InvalidPin), r.ReasonCode);
        Assert.Empty(issuer.OpenHolds);
    }

    [Fact]
    public void The_bank_remembers_the_zip_on_file_across_a_journal_round_trip()
    {
        var issuer = IssuerSimulator.WithTestCards();
        var restored = new IssuerSimulator();
        restored.Import(issuer.Export());
        var good = TestCards.Get("Good");
        Assert.True(restored.Authorize("S1", good with { Zip = TestCards.Zip }, Money.FromDollars(30), SimHarness.Start).Approved);
        Assert.Equal(DeclineReason.ZipMismatch, restored.Authorize("S2", good with { Zip = "00000" }, Money.FromDollars(30), SimHarness.Start).Reason);
    }

    private static (PaymentGateway Gateway, Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock, IssuerSimulator Issuer) Gateway()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(SimHarness.Start);
        var issuer = IssuerSimulator.WithTestCards();
        var wire = new NetworkSimulator(new NetworkFaultPolicy(), clock);
        var gw = new PaymentGateway(issuer, wire, new HmacAuthorizationSigner(new byte[32]), clock, "PUMP-07");
        return (gw, clock, issuer);
    }

    // ----- End to end, under every dispatch strategy.

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_credit_card_with_the_right_zip_fuels_and_settles(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Sim.PresentCard("Good", source: CardSource.PumpTap);
        h.Settle();
        Assert.Equal(SaleState.CardRead, h.State.State);
        Assert.Equal(DriverAction.EnterZip, h.State.Notice.Action);
        Assert.Empty(h.Sim.Issuer.OpenHolds);                      // the bank has not heard from us yet
        Assert.NotNull(h.State.DeadlineAt);

        h.Sim.EnterCode(TestCards.Zip);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Single(h.Sim.Issuer.OpenHolds);

        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromMinutes(3));
        Assert.Equal(HoldStatus.Settled, h.State.Hold);
        Assert.Equal(3000, h.State.Money.Cents);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Equal(0, h.Sim.Ledger.Verify());
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_wrong_zip_is_declined_by_the_bank_and_the_screen_says_so(string strategy)
    {
        using var h = new SimHarness(strategy, ThirtyDollarHold);
        h.Present("Good", code: "54321");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.Equal(EndReason.CardDeclined, h.State.End);
        Assert.Equal("Card declined", h.State.Notice.Headline);
        Assert.Contains("ZIP does not match", h.State.Notice.Detail);   // "Billing ZIP does not match." as a sentence
        Assert.Equal(HoldStatus.None, h.State.Hold);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Empty(h.Sim.Gateway.Pending);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_debit_card_takes_its_pin_and_a_wrong_pin_is_declined(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.PresentCard("DebitGoodPin", tender: Tender.Debit);
        h.Settle();
        Assert.Equal(DriverAction.EnterPin, h.State.Notice.Action);
        h.Sim.EnterCode("0000");
        h.AdvanceUntilState(SaleState.Declined, TimeSpan.FromSeconds(5));
        Assert.Contains("Invalid PIN", h.State.Notice.Detail);
        Assert.Empty(h.Sim.Issuer.OpenHolds);

        // The next driver, with the right PIN, is approved for the debit ceiling.
        h.Present("DebitGoodPin", tender: Tender.Debit);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(h.Options.DebitHold, h.State.Ceiling);
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_card_left_in_the_reader_with_nobody_typing_cancels_and_frees_the_pump(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Sim.PresentCard("Good");
        h.Advance(h.Options.Timeouts.CodeEntry + TimeSpan.FromSeconds(1));
        Assert.Equal(SaleState.Cancelled, h.State.State);
        Assert.Equal(EndReason.NoCodeEntered, h.State.End);
        Assert.Empty(h.Sim.Issuer.OpenHolds);
        Assert.Empty(h.Sim.Gateway.Pending);                       // no reversal was needed, so none was queued

        // The pump is free for the next card at once.
        Assert.True(h.TryPresent("Good").Ok);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_second_card_at_the_keypad_replaces_the_first_with_nothing_to_reverse(string strategy)
    {
        using var h = new SimHarness(strategy);
        var first = h.Sim.PresentCard("Good");
        h.Settle();
        var (ok, second, _) = h.TryPresent("DebitGoodPin", tender: Tender.Debit);
        Assert.True(ok);
        Assert.NotEqual(first, second);
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        Assert.Equal(Tender.Debit, h.State.Tender);
        Assert.Single(h.Sim.Issuer.OpenHolds);
        Assert.Contains(h.Sim.Ledger.Snapshot(), e => e.Kind == "sale.superseded" && e.Detail.Contains(first) && e.Detail.Contains("CardRead"));
    }
}
