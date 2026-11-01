using System.Security.Cryptography;
using System.Text;
using GasPump.Core;
using GasPump.Payment;

namespace GasPump.Security;

public enum VerifyFailure { None, BadFormat, BadSignature, WrongPump, Expired, NotYetValid, Replayed }

public sealed record VerifiedAuthorization(string SaleId, Money Ceiling, Volume MaxVolume, DateTimeOffset ExpiresAt);

public sealed record ArmedAuthorization(string SaleId, Money Ceiling, Volume MaxVolume, TimeSpan MaxDuration, bool Clamped, string ClampNote);

public interface IAuthorizationVerifier
{
    (VerifiedAuthorization? Auth, VerifyFailure Failure) Verify(string wire, DateTimeOffset now);
}

/// <summary>Single-use nonces. A token presented twice is a replay, and the second one dies here.</summary>
public sealed class NonceStore
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public bool TryConsume(string nonce)
    {
        lock (_gate) return _seen.Add(nonce);
    }

    /// <summary>For the journal. A restart must remember every nonce it burned, or a token replays across the reboot.</summary>
    public IReadOnlyList<string> Export() { lock (_gate) return _seen.ToArray(); }

    public void Import(IEnumerable<string> nonces)
    {
        lock (_gate) foreach (var n in nonces) _seen.Add(n);
    }
}

/// <summary>Checks the HMAC, the pump id, the time window, and the nonce. In that order, so a forged token never touches the nonce store.</summary>
public sealed class HmacAuthorizationVerifier(byte[] key, string pumpId, NonceStore nonces, TimeSpan clockSkew) : IAuthorizationVerifier
{
    public (VerifiedAuthorization? Auth, VerifyFailure Failure) Verify(string wire, DateTimeOffset now)
    {
        AuthorizationToken token;
        try { token = AuthorizationToken.Parse(wire); }
        catch (Exception) { return (null, VerifyFailure.BadFormat); }

        var expected = HmacAuthorizationSigner.Sign(key, token.Payload());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(token.Signature)))
            return (null, VerifyFailure.BadSignature);

        if (!string.Equals(token.PumpId, pumpId, StringComparison.Ordinal))
            return (null, VerifyFailure.WrongPump);

        var issued = DateTimeOffset.FromUnixTimeMilliseconds(token.IssuedAtUnixMs);
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(token.ExpiresAtUnixMs);
        if (now < issued - clockSkew) return (null, VerifyFailure.NotYetValid);
        if (now > expires + clockSkew) return (null, VerifyFailure.Expired);

        if (!nonces.TryConsume(token.Nonce)) return (null, VerifyFailure.Replayed);

        return (new VerifiedAuthorization(token.SaleId, token.Ceiling, token.MaxVolume, expires), VerifyFailure.None);
    }
}

/// <summary>
/// The one place that decides whether the valve may open and how far. It
/// verifies the token, then clamps every number to the hard limits it owns.
/// Nothing else in the system can arm the pump. There is no "set limit"
/// message, so there is nothing for an attacker to send.
///
/// GEM:decide-at-authoritative-boundary — the pump has the context (its own
/// limits, its own clock, its own nonce history), so the pump decides. The
/// payment host proposes a ceiling; it never dictates one.
/// </summary>
public sealed class PumpAuthority(IAuthorizationVerifier verifier, HardLimits limits, EventLedger ledger, TimeProvider clock)
{
    public HardLimits Limits => limits;

    public (ArmedAuthorization? Armed, string Refusal) TryArm(string saleId, string tokenWire)
    {
        var now = clock.GetUtcNow();
        var (auth, failure) = verifier.Verify(tokenWire, now);
        if (auth is null)
        {
            ledger.Append("arm.refused", $"{saleId} {failure}");
            return (null, failure.ToString());
        }
        if (auth.SaleId != saleId)
        {
            ledger.Append("arm.refused", $"{saleId} token is for sale {auth.SaleId}");
            return (null, "TokenForOtherSale");
        }

        var ceiling = Money.Min(auth.Ceiling, limits.MaxMoneyPerSale);
        var maxVolume = Volume.Min(auth.MaxVolume, limits.MaxVolumePerSale);
        var remaining = auth.ExpiresAt - now;
        var duration = remaining < limits.MaxSaleDuration ? remaining : limits.MaxSaleDuration;

        var clamped = ceiling != auth.Ceiling || maxVolume != auth.MaxVolume;
        var note = clamped
            ? $"clamped from {auth.Ceiling}/{auth.MaxVolume} to {ceiling}/{maxVolume}"
            : "within limits";

        ledger.Append("arm.ok", $"{saleId} ceiling {ceiling} max {maxVolume} {note}");
        return (new ArmedAuthorization(saleId, ceiling, maxVolume, duration, clamped, note), "");
    }
}
