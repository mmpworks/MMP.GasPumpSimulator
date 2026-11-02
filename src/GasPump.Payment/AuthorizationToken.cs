using System.Security.Cryptography;
using System.Text;
using GasPump.Core;

namespace GasPump.Payment;

/// <summary>
/// What the payment side hands the pump: "this sale, this pump, this much,
/// this long, once". The pump checks the signature and ignores who sent it. In the
/// real world the key would be per device and the counter would live in the
/// pump; here one shared key shows the shape.
/// </summary>
public sealed record AuthorizationToken(
    string SaleId,
    string PumpId,
    long CeilingCents,
    long MaxMilliGallons,
    long IssuedAtUnixMs,
    long ExpiresAtUnixMs,
    string Nonce,
    string Signature)
{
    public Money Ceiling => new(CeilingCents);
    public Volume MaxVolume => new(MaxMilliGallons);

    /// <summary>The bytes that are signed. Field order is the contract. Change it and every pump rejects every token.</summary>
    public string Payload() => $"{SaleId}|{PumpId}|{CeilingCents}|{MaxMilliGallons}|{IssuedAtUnixMs}|{ExpiresAtUnixMs}|{Nonce}";

    public string Serialize() => $"{Payload()}|{Signature}";

    public static AuthorizationToken Parse(string wire)
    {
        var parts = wire.Split('|');
        if (parts.Length != 8) throw new FormatException("token has wrong field count");
        return new AuthorizationToken(
            parts[0], parts[1],
            long.Parse(parts[2]), long.Parse(parts[3]), long.Parse(parts[4]), long.Parse(parts[5]),
            parts[6], parts[7]);
    }
}

public interface IAuthorizationSigner
{
    AuthorizationToken Issue(string saleId, string pumpId, Money ceiling, Volume maxVolume, DateTimeOffset now, TimeSpan lifetime);
}

/// <summary>HMAC-SHA256 over the payload. The verifier in GasPump.Security holds the same key.</summary>
public sealed class HmacAuthorizationSigner(byte[] key) : IAuthorizationSigner
{
    public AuthorizationToken Issue(string saleId, string pumpId, Money ceiling, Volume maxVolume, DateTimeOffset now, TimeSpan lifetime)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var unsigned = new AuthorizationToken(saleId, pumpId, ceiling.Cents, maxVolume.MilliGallons,
            now.ToUnixTimeMilliseconds(), now.Add(lifetime).ToUnixTimeMilliseconds(), nonce, "");
        return unsigned with { Signature = Sign(key, unsigned.Payload()) };
    }

    public static string Sign(byte[] key, string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
}
