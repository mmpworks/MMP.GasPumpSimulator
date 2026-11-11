using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;

namespace GasPump.Dispatch;

/// <summary>The outcome of one reader attack from the app. Code is the bank's decline code or the memory probe's answer.</summary>
public sealed record ReaderAttackResult(string Attack, bool Defended, string Code, string Message, ProtectedCard? Sent);

/// <summary>
/// The card reader attacks the app can run, against the real bank of this station and the real reader of one pump.
/// Each one starts from the last card the terminal saw on that pump, as an attacker on the terminal would. The
/// attack's sale id is LAB-..., and the bank gets no other message. If a defence ever failed and the bank approved,
/// the hold is reversed at once and the result says so.
/// </summary>
public static class ReaderLab
{
    public static IReadOnlyList<string> Attacks { get; } = ["replay", "tamper-pin", "tamper-card", "rogue-reader", "read-memory"];

    public static ReaderAttackResult Run(string attack, FuellingPoint point, IssuerSimulator bank, Money hold, DateTimeOffset now) => attack switch
    {
        "replay" => WithLast(attack, point, card => Send(attack, bank, card, hold, now, "the recorded message, sent again with its KSN")),
        "tamper-pin" => WithLast(attack, point, card => card.PinBlock is null
            ? Unrun(attack, "the last card was credit; run a debit sale first", card)
            : Send(attack, bank, card with { PinBlock = Flip(card.PinBlock) }, hold, now, "one byte of the PIN block changed")),
        "tamper-card" => WithLast(attack, point, card => Send(attack, bank, card with { SealedPan = Flip(card.SealedPan) }, hold, now, "one byte of the sealed card number changed")),
        "rogue-reader" => RogueReader(bank, hold, now),
        "read-memory" => WithLast(attack, point, card => ReadMemory(point.Reader, card)),
        _ => Unrun(attack, $"no reader attack named '{attack}'; the attacks are {string.Join(", ", Attacks)}", null),
    };

    private static ReaderAttackResult WithLast(string attack, FuellingPoint point, Func<ProtectedCard, ReaderAttackResult> run) =>
        point.LastPresented is { } card ? run(card) : Unrun(attack, "no card sale on this pump yet; run a sale first", null);

    private static ReaderAttackResult Send(string attack, IssuerSimulator bank, ProtectedCard card, Money hold, DateTimeOffset now, string what)
    {
        var saleId = $"LAB-{attack}-{now:HHmmssfff}";
        var decision = bank.Authorize(saleId, card, hold, now);
        if (decision.Approved) bank.Reverse(saleId);
        var verdict = decision.Approved ? "the bank approved it, and the lab reversed the hold" : $"the bank refused it: {decision.Message}";
        return new(attack, !decision.Approved, decision.Approved ? "Approved" : decision.Reason.ToString(), $"{what}; {verdict}", card);
    }

    /// <summary>A reader with the right key set ID, injected from a base key this bank never held.</summary>
    private static ReaderAttackResult RogueReader(IssuerSimulator bank, Money hold, DateTimeOffset now)
    {
        var rogueKeys = new BankKeys(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var initialKeyId = BankKeys.NewInitialKeyId(0xEE);
        // The card is read inside GasPump.Reader: the lab never holds the test card's number or PIN.
        var card = LabReader.ReadDebitTestCard(rogueKeys.InitialKey(initialKeyId), initialKeyId);
        return Send("rogue-reader", bank, card, hold, now, "a reader with a base key this bank never injected read the DebitGoodPin card");
    }

    private static ReaderAttackResult ReadMemory(CardReader reader, ProtectedCard card)
    {
        var m = reader.ReadMemory(card.Counter);
        var defended = !m.SaleKeyDerivable;
        var message = $"{m.HeldKeyRegisters} key registers read. The PIN key of counter {m.SaleCounter} "
            + (m.SaleKeyDerivable ? "CAN be rebuilt from them" : "cannot be rebuilt from them")
            + $"; the next counter's key {(m.NextKeyDerivable ? "can" : "cannot")} be, so the probe works.";
        return new("read-memory", defended, defended ? "SaleKeyGone" : "SaleKeyDerivable", message, card);
    }

    private static ReaderAttackResult Unrun(string attack, string why, ProtectedCard? card) => new(attack, false, "NotRun", why, card);

    private static string Flip(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        bytes[bytes.Length / 2] ^= 0x01;
        return Convert.ToHexString(bytes);
    }
}
