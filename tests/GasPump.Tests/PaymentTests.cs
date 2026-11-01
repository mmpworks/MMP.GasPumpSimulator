using GasPump.Core;
using GasPump.Payment;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GasPump.Tests;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = SimHarness.Start;
    private static readonly Money Hold = Money.FromDollars(75);

    [Theory]
    [InlineData("Good", true, 7500, DeclineReason.None)]
    [InlineData("LowBalance", true, 1850, DeclineReason.None)]
    [InlineData("InsufficientFunds", false, 0, DeclineReason.InsufficientFunds)]
    [InlineData("Stolen", false, 0, DeclineReason.LostOrStolen)]
    [InlineData("Frozen", false, 0, DeclineReason.DoNotHonor)]
    [InlineData("DebitGoodPin", true, 7500, DeclineReason.None)]
    [InlineData("DebitBadPin", false, 0, DeclineReason.InvalidPin)]
    public void Issuer_decides_each_named_card_as_its_name_says(string card, bool approved, long cents, DeclineReason reason)
    {
        var issuer = IssuerSimulator.WithTestCards();
        var d = issuer.Authorize("S1", TestCards.Get(card), Hold, Now);
        Assert.Equal(approved, d.Approved);
        Assert.Equal(new Money(cents), d.ApprovedAmount);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void Partial_approval_is_flagged_when_the_balance_is_below_the_hold()
    {
        var issuer = IssuerSimulator.WithTestCards();
        var d = issuer.Authorize("S1", TestCards.Get("LowBalance"), Hold, Now);
        Assert.True(d.IsPartial(Hold));
        Assert.False(issuer.Authorize("S2", TestCards.Get("Good"), Hold, Now).IsPartial(Hold));
    }

    [Fact]
    public void Velocity_card_declines_on_the_third_use_in_ten_minutes()
    {
        var issuer = IssuerSimulator.WithTestCards();
        var card = TestCards.Get("Velocity");
        Assert.True(issuer.Authorize("S1", card, Hold, Now).Approved);
        Assert.True(issuer.Authorize("S2", card, Hold, Now.AddMinutes(1)).Approved);
        var third = issuer.Authorize("S3", card, Hold, Now.AddMinutes(2));
        Assert.Equal(DeclineReason.VelocityExceeded, third.Reason);
        Assert.True(issuer.Authorize("S4", card, Hold, Now.AddMinutes(12)).Approved);
    }

    [Fact]
    public void Hold_comes_off_the_balance_and_completion_refunds_the_unspent_part()
    {
        var issuer = IssuerSimulator.WithTestCards();
        var card = TestCards.Get("Good");
        var before = issuer.AvailableBalance(card.Pan);
        issuer.Authorize("S1", card, Hold, Now);
        Assert.Equal(before - Hold, issuer.AvailableBalance(card.Pan));
        issuer.Complete("S1", Money.FromDollars(30));
        Assert.Equal(before - Money.FromDollars(30), issuer.AvailableBalance(card.Pan));
        issuer.Complete("S1", Money.FromDollars(30));   // duplicate completion changes nothing
        Assert.Equal(before - Money.FromDollars(30), issuer.AvailableBalance(card.Pan));
    }

    [Fact]
    public void Reversal_restores_the_whole_hold()
    {
        var issuer = IssuerSimulator.WithTestCards();
        var card = TestCards.Get("Good");
        var before = issuer.AvailableBalance(card.Pan);
        issuer.Authorize("S1", card, Hold, Now);
        issuer.Reverse("S1");
        Assert.Equal(before, issuer.AvailableBalance(card.Pan));
    }

    private static (PaymentGateway Gateway, NetworkFaultPolicy Net, FakeTimeProvider Clock, IssuerSimulator Issuer) Gateway()
    {
        var (gw, net, clock, issuer, _) = GatewayWithWire();
        return (gw, net, clock, issuer);
    }

    private static (PaymentGateway Gateway, NetworkFaultPolicy Net, FakeTimeProvider Clock, IssuerSimulator Issuer, NetworkSimulator Wire) GatewayWithWire()
    {
        var clock = new FakeTimeProvider(Now);
        var issuer = IssuerSimulator.WithTestCards();
        var net = new NetworkFaultPolicy();
        var wire = new NetworkSimulator(net, clock);
        var gw = new PaymentGateway(issuer, wire, new HmacAuthorizationSigner(new byte[32]), clock, "PUMP-07");
        return (gw, net, clock, issuer, wire);
    }

    [Fact]
    public async Task Gateway_approves_after_the_network_latency()
    {
        var (gw, _, clock, _) = Gateway();
        var task = gw.PreAuthorizeAsync("S1", TestCards.Get("Good"), Hold, CancellationToken.None);
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var r = await task;
        Assert.True(r.Approved);
        Assert.Equal(Hold, r.Ceiling);
        Assert.NotNull(r.Token);
    }

    [Fact]
    public async Task Gateway_reports_a_network_failure_when_the_host_is_down()
    {
        var (gw, net, clock, _) = Gateway();
        net.Mode = NetworkMode.Down;
        var task = gw.PreAuthorizeAsync("S1", TestCards.Get("Good"), Hold, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var r = await task;
        Assert.False(r.Approved);
        Assert.True(r.NetworkFailure);
        Assert.Equal("ConnectionRefused", r.ReasonCode);
    }

    [Fact]
    public async Task Gateway_times_out_after_thirty_seconds_of_silence()
    {
        var (gw, net, clock, _) = Gateway();
        net.Mode = NetworkMode.Timeout;
        var task = gw.PreAuthorizeAsync("S1", TestCards.Get("Good"), Hold, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var r = await task;
        Assert.Equal("TimedOut", r.ReasonCode);
    }

    [Fact]
    public async Task Completion_queues_while_the_host_is_down_and_drains_when_it_returns()
    {
        var (gw, net, clock, issuer) = Gateway();
        var card = TestCards.Get("Good");
        var pre = gw.PreAuthorizeAsync("S1", card, Hold, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await pre;

        net.Mode = NetworkMode.Down;
        var complete = gw.CompleteAsync("S1", Money.FromDollars(30), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var c = await complete;
        Assert.True(c.Queued);
        Assert.Single(gw.PendingCompletions);

        net.Mode = NetworkMode.Up;
        var drain = gw.DrainPendingAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Single(await drain);
        Assert.Empty(gw.PendingCompletions);
        Assert.Equal(Money.FromDollars(5000) - Money.FromDollars(30), issuer.AvailableBalance(card.Pan));
    }

    [Fact]
    public async Task Reversal_that_cannot_reach_the_host_is_queued_once_and_drains_before_completions()
    {
        var (gw, net, clock, issuer) = Gateway();
        var card = TestCards.Get("Good");
        var before = issuer.AvailableBalance(card.Pan);
        var pre = gw.PreAuthorizeAsync("S1", card, Hold, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        await pre;

        net.Mode = NetworkMode.Down;
        var r1 = gw.ReverseAsync("S1", CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True((await r1).Queued);
        var r2 = gw.ReverseAsync("S1", CancellationToken.None);   // a retry must never double the queue
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True((await r2).Queued);
        Assert.Single(gw.PendingReversals);
        Assert.Equal(before - Hold, issuer.AvailableBalance(card.Pan));

        net.Mode = NetworkMode.Up;
        var drain = gw.DrainPendingAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var sent = await drain;
        Assert.Single(sent);
        Assert.Equal(PendingKind.Reversal, sent[0].Kind);
        Assert.Empty(gw.Pending);
        Assert.Equal(before, issuer.AvailableBalance(card.Pan));
    }

    [Fact]
    public void Reversal_that_arrives_before_its_authorization_voids_the_sale_at_the_issuer()
    {
        // The late-network race in the other order: our reversal beats our own authorization to the bank.
        var issuer = IssuerSimulator.WithTestCards();
        var card = TestCards.Get("Good");
        var before = issuer.AvailableBalance(card.Pan);
        issuer.Reverse("S1");
        var late = issuer.Authorize("S1", card, Hold, Now);
        Assert.False(late.Approved);
        Assert.Contains("voided", late.Message);
        Assert.Equal(before, issuer.AvailableBalance(card.Pan));
        Assert.Empty(issuer.OpenHolds);
    }

    [Fact]
    public async Task Late_mode_delivers_the_answer_after_the_caller_gave_up()
    {
        var (gw, net, clock, issuer, wire) = GatewayWithWire();
        var card = TestCards.Get("Good");
        net.Mode = NetworkMode.Late;
        var task = gw.PreAuthorizeAsync("S1", card, Hold, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        // The issuer call and the second wait run on a pool continuation after Advance returns. Wait for the
        // second timer to be parked on the clock before moving the clock again, or the test races itself.
        Assert.True(SpinWait.SpinUntil(() => wire.WaitingOnClock == 1 && issuer.OpenHolds.Count == 1, TimeSpan.FromSeconds(5)), "late wait never parked");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(task.IsCompleted);             // and we still have no answer at the 30 s mark
        clock.Advance(TimeSpan.FromSeconds(15));
        var r = await task;
        Assert.True(r.Approved);
    }

    [Fact]
    public async Task Expired_card_never_touches_the_network()
    {
        var (gw, net, _, _) = Gateway();
        net.Mode = NetworkMode.Down;
        var r = await gw.PreAuthorizeAsync("S1", TestCards.Get("Expired"), Hold, CancellationToken.None);
        Assert.Equal("Expired", r.ReasonCode);
        Assert.False(r.NetworkFailure);
    }
}
