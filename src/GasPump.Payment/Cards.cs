namespace GasPump.Payment;

public enum CardKind { Credit, Debit }

/// <summary>
/// The card in clear: what the magnetic stripe holds, and what the bank keeps on file. Only the card reader
/// (GasPump.Reader) and the bank (GasPump.Bank) hold one. The terminal gets a <see cref="ProtectedCard"/>.
/// The numbers are made up. Pin and Zip are what the driver typed at the keypad for this sale: the PIN for
/// a debit card, the billing ZIP for a credit card. The bank checks them.
/// </summary>
public sealed record CardData(string Pan, int ExpiryMonth, int ExpiryYear, CardKind Kind, string? Pin = null, string? Zip = null)
{
    public string Last4 => Pan.Length >= 4 ? Pan[^4..] : Pan;
}

public enum CardCheck { Ok, BadLuhn, Expired, BadFormat }

/// <summary>
/// Luhn and expiry, the two checks a terminal can make before it spends a network call. The card number
/// check runs in the reader, where the digits are. The expiry check runs in the terminal on the clear expiry.
/// </summary>
public static class CardValidator
{
    public static CardCheck Check(CardData card, DateTimeOffset now)
    {
        var number = CheckNumber(card.Pan);
        return number != CardCheck.Ok ? number : CheckExpiry(card.ExpiryMonth, card.ExpiryYear, now);
    }

    /// <summary>The length, the digits and the Luhn digit.</summary>
    public static CardCheck CheckNumber(string pan)
    {
        if (pan.Length is < 12 or > 19 || !pan.All(char.IsDigit)) return CardCheck.BadFormat;
        return Luhn(pan) ? CardCheck.Ok : CardCheck.BadLuhn;
    }

    public static CardCheck CheckExpiry(int month, int year, DateTimeOffset now)
    {
        if (month is < 1 or > 12) return CardCheck.BadFormat;
        // A card is good through the last day of its expiry month.
        var lastValid = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        if (now >= lastValid) return CardCheck.Expired;
        return CardCheck.Ok;
    }

    public static bool Luhn(string digits)
    {
        int sum = 0;
        bool doubleIt = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubleIt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }
}

/// <summary>
/// The named test cards. The name says what the card will do, so a scenario
/// reads like a sentence: "present the InsufficientFunds card".
/// Every PAN passes Luhn except BadLuhn, which fails on purpose.
/// None of them sits in a real, usable card range.
/// Every credit account has billing ZIP 12345 on file. DebitGoodPin's account
/// has PIN 1234 on file. DebitBadPin's account has no PIN on file, so no PIN
/// the driver types can match it.
/// </summary>
public static class TestCards
{
    /// <summary>The billing ZIP every credit test card expects at the keypad.</summary>
    public const string Zip = "12345";
    /// <summary>The PIN the DebitGoodPin account expects at the keypad.</summary>
    public const string Pin = "1234";

    public static readonly IReadOnlyDictionary<string, CardData> ByName = new Dictionary<string, CardData>(StringComparer.OrdinalIgnoreCase)
    {
        ["Good"]              = new("4111111111111111", 12, 2030, CardKind.Credit),
        ["LowBalance"]        = new("4012888888881881", 12, 2030, CardKind.Credit),      // approves $18.50 of a $75 hold (partial)
        ["InsufficientFunds"] = new("4222222222222",    12, 2030, CardKind.Credit),      // $0 available
        ["Expired"]           = new("5555555555554444", 1,  2020, CardKind.Credit),
        ["Stolen"]            = new("5105105105105100", 12, 2030, CardKind.Credit),
        ["Frozen"]            = new("378282246310005",  12, 2030, CardKind.Credit),
        ["Velocity"]          = new("371449635398431",  12, 2030, CardKind.Credit),      // third use in the window declines
        ["DebitGoodPin"]      = new("6011111111111117", 12, 2030, CardKind.Debit),      // PIN 1234 on file at the bank
        ["DebitBadPin"]       = new("6011000990139424", 12, 2030, CardKind.Debit),      // no PIN on file, so every PIN is wrong
        ["BadLuhn"]           = new("4111111111111112", 12, 2030, CardKind.Credit),
    };

    public static CardData Get(string name) =>
        ByName.TryGetValue(name, out var c) ? c : throw new KeyNotFoundException($"no test card named '{name}'");
}
