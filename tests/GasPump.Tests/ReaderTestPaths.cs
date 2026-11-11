// IssuerSimulator moved from GasPump.Payment to GasPump.Bank in 0.2.0. One global using keeps the older test files as they were.
global using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;

namespace GasPump.Tests;

/// <summary>
/// The tests written before 0.2.0 hand the gateway and the bank a card in clear, with the keypad entry beside it.
/// From 0.2.0 the gateway and the bank take only what a card reader sealed. These two extensions keep those
/// tests' calls and assertions as they were, and route each card through a real <see cref="CardReader"/> on the way.
/// Each call injects a new reader with a new initial key ID, so two reads never share a KSN.
/// The reader is injected from the bank's own keys, as a key injection facility would.
/// </summary>
internal static class ReaderTestPaths
{
    public static CardReader NewReader(BankKeys keys)
    {
        var initialKeyId = BankKeys.NewInitialKeyId(0x7E);
        return new CardReader(keys.InitialKey(initialKeyId), initialKeyId);
    }

    /// <summary>The pre-0.2.0 call: the card and the keypad entry, through a reader injected for the bank behind <paramref name="gateway"/>.</summary>
    public static Task<PreAuthResult> PreAuthorizeAsync(this PaymentGateway gateway, string saleId, string pumpId, CardData card, Tender tender, string? keypad, CancellationToken ct) =>
        gateway.PreAuthorizeAsync(saleId, pumpId, NewReader(gateway.BankKeysForTests()).Read(card, tender, keypad), tender, ct);

    /// <summary>The pre-0.2.0 call: the card with its PIN or ZIP on it, read by a reader injected for this bank.</summary>
    public static IssuerDecision Authorize(this IssuerSimulator issuer, string saleId, CardData card, Money hold, DateTimeOffset now) =>
        issuer.Authorize(saleId, NewReader(issuer.Keys).Read(card, card.Kind == CardKind.Debit ? Tender.Debit : Tender.Credit, card.Kind == CardKind.Debit ? card.Pin : card.Zip), hold, now);

    private static BankKeys BankKeysForTests(this PaymentGateway gateway) =>
        gateway.Issuer is IssuerSimulator bank ? bank.Keys : throw new InvalidOperationException("the gateway's issuer is not the simulated bank");
}
