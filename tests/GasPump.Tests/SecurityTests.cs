using GasPump.Core;
using GasPump.Payment;
using GasPump.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GasPump.Tests;

public class SecurityTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();

    private static (HmacAuthorizationVerifier Verifier, HmacAuthorizationSigner Signer, FakeTimeProvider Clock, NonceStore Nonces) Rig()
    {
        var clock = new FakeTimeProvider(SimHarness.Start);
        var nonces = new NonceStore();
        return (new HmacAuthorizationVerifier(Key, "PUMP-07", nonces, TimeSpan.FromSeconds(5)), new HmacAuthorizationSigner(Key), clock, nonces);
    }

    private static string Issue(HmacAuthorizationSigner signer, FakeTimeProvider clock, string pump = "PUMP-07", long cents = 3000, decimal gallons = 50) =>
        signer.Issue("S1", pump, new Money(cents), Volume.FromGallons(gallons), clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();

    [Fact]
    public void Good_token_verifies_once()
    {
        var (v, s, clock, _) = Rig();
        var wire = Issue(s, clock);
        var (auth, failure) = v.Verify(wire, clock.GetUtcNow());
        Assert.Equal(VerifyFailure.None, failure);
        Assert.Equal(new Money(3000), auth!.Ceiling);
    }

    [Fact]
    public void Tampered_ceiling_fails_the_signature()
    {
        var (v, s, clock, _) = Rig();
        var token = AuthorizationToken.Parse(Issue(s, clock));
        var forged = (token with { CeilingCents = 200_000 }).Serialize();
        Assert.Equal(VerifyFailure.BadSignature, v.Verify(forged, clock.GetUtcNow()).Failure);
    }

    [Fact]
    public void Token_signed_with_another_key_fails_the_signature()
    {
        var (v, _, clock, _) = Rig();
        var wire = Issue(new HmacAuthorizationSigner(OtherKey), clock);
        Assert.Equal(VerifyFailure.BadSignature, v.Verify(wire, clock.GetUtcNow()).Failure);
    }

    [Fact]
    public void Replayed_token_is_refused_the_second_time()
    {
        var (v, s, clock, _) = Rig();
        var wire = Issue(s, clock);
        Assert.Equal(VerifyFailure.None, v.Verify(wire, clock.GetUtcNow()).Failure);
        Assert.Equal(VerifyFailure.Replayed, v.Verify(wire, clock.GetUtcNow()).Failure);
    }

    [Fact]
    public void Token_for_another_pump_is_refused()
    {
        var (v, s, clock, _) = Rig();
        Assert.Equal(VerifyFailure.WrongPump, v.Verify(Issue(s, clock, pump: "PUMP-03"), clock.GetUtcNow()).Failure);
    }

    [Fact]
    public void Expired_token_is_refused_and_a_forged_one_never_burns_a_nonce()
    {
        var (v, s, clock, nonces) = Rig();
        var wire = Issue(s, clock);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(VerifyFailure.Expired, v.Verify(wire, clock.GetUtcNow()).Failure);

        var token = AuthorizationToken.Parse(wire);
        var forged = (token with { ExpiresAtUnixMs = clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds() }).Serialize();
        Assert.Equal(VerifyFailure.BadSignature, v.Verify(forged, clock.GetUtcNow()).Failure);
        Assert.True(nonces.TryConsume(token.Nonce), "a forged token must not consume the nonce");
    }

    [Fact]
    public void Garbage_is_a_format_failure_not_an_exception()
    {
        var (v, _, clock, _) = Rig();
        Assert.Equal(VerifyFailure.BadFormat, v.Verify("", clock.GetUtcNow()).Failure);
        Assert.Equal(VerifyFailure.BadFormat, v.Verify("a|b|c", clock.GetUtcNow()).Failure);
    }

    [Fact]
    public void Authority_clamps_a_signed_600_gallon_2000_dollar_token_to_the_hard_limits()
    {
        var (v, s, clock, _) = Rig();
        var ledger = new EventLedger(clock);
        var authority = new PumpAuthority(v, HardLimits.Default, ledger, clock);
        var wire = s.Issue("S1", "PUMP-07", Money.FromDollars(2000), Volume.FromGallons(600), clock.GetUtcNow(), TimeSpan.FromMinutes(5)).Serialize();

        var (armed, refusal) = authority.TryArm("S1", wire);

        Assert.Equal("", refusal);
        Assert.True(armed!.Clamped);
        Assert.Equal(Money.FromDollars(500), armed.Ceiling);
        Assert.Equal(Volume.FromGallons(50), armed.MaxVolume);
        Assert.Contains("clamped", armed.ClampNote);
        Assert.Contains(ledger.Snapshot(), e => e.Kind == "arm.ok" && e.Detail.Contains("clamped"));
    }

    [Fact]
    public void Authority_refuses_a_token_for_a_different_sale()
    {
        var (v, s, clock, _) = Rig();
        var authority = new PumpAuthority(v, HardLimits.Default, new EventLedger(clock), clock);
        var (armed, refusal) = authority.TryArm("S9", Issue(s, clock));
        Assert.Null(armed);
        Assert.Equal("TokenForOtherSale", refusal);
    }

    [Fact]
    public void Ledger_verifies_intact_and_detects_an_edited_entry()
    {
        var clock = new FakeTimeProvider(SimHarness.Start);
        var ledger = new EventLedger(clock);
        for (int i = 0; i < 5; i++) { ledger.Append("sale", $"entry {i}"); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(0, ledger.Verify());

        var copy = ledger.Snapshot().ToList();
        copy[2] = copy[2] with { Detail = "entry 2 but 600 gallons went missing" };
        Assert.Equal(3, EventLedger.Verify(copy));

        var rehashed = copy[2] with { Hash = LedgerEntry.Compute(copy[2].Seq, copy[2].At, copy[2].Kind, copy[2].Detail, copy[2].PrevHash) };
        copy[2] = rehashed;
        Assert.Equal(4, EventLedger.Verify(copy));   // fixing one hash breaks the next link
    }

    [Fact]
    public void Totalizer_only_goes_up()
    {
        var t = new Totalizer();
        t.Add(Volume.FromGallons(1));
        t.Add(Volume.FromGallons(2));
        Assert.Equal(Volume.FromGallons(3), t.Total);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Add(new Volume(-1)));
        Assert.Null(typeof(Totalizer).GetMethod("Reset"));
    }
}
