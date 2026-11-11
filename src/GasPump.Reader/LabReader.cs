using GasPump.Core;
using GasPump.Payment;

namespace GasPump.Reader;

/// <summary>
/// The card side of the app's "reader with another base key" attack. It lives in the reader project, so the
/// test card's number and PIN stay inside the reader boundary: the lab outside gets only the sealed card.
/// </summary>
public static class LabReader
{
    /// <summary>A reader loaded with <paramref name="initialKey"/> (which it erases) reads the DebitGoodPin test card with its PIN.</summary>
    public static ProtectedCard ReadDebitTestCard(byte[] initialKey, string initialKeyId)
    {
        var reader = new CardReader(initialKey, initialKeyId);
        reader.Keypad("LAB", TestCards.Pin);
        return reader.Read("LAB", "DebitGoodPin", Tender.Debit);
    }
}
