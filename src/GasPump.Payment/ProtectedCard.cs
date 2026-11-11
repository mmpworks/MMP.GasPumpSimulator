using System.Globalization;
using System.Text;

namespace GasPump.Payment;

/// <summary>
/// What the card reader hands the terminal, and all the terminal ever sees of the card. The card number is
/// sealed for the bank, the PIN is a PIN block for the bank, and the clear fields are the ones a receipt or a
/// decline may show: the first 6 and last 4 digits, the kind, the expiry, the billing ZIP.
///
/// Ksn is the key serial number in hex: the reader's initial key ID (8 bytes) and its counter (4 bytes).
/// The bank derives both keys of the sale from it. SealedPan is AES-128-GCM ciphertext and tag under the
/// sale's data key. The tag also covers <see cref="AssociatedData"/>, so a changed clear field, KSN or PIN
/// block fails at the bank. PinBlock is ISO 9564 format 4 under the sale's PIN key, debit only.
/// ReaderCheck is the reader's own Luhn and format check, made where the digits are.
/// </summary>
public sealed record ProtectedCard(
    string Ksn, string SealedPan, string? PinBlock, string MaskedPan,
    CardKind Kind, int ExpiryMonth, int ExpiryYear, string? Zip, CardCheck ReaderCheck)
{
    /// <summary>The digits a receipt may print.</summary>
    public string Last4 => MaskedPan[^4..];

    /// <summary>The reader's initial key ID: the key set ID (4 bytes) and the device ID (4 bytes), in hex.</summary>
    public string InitialKeyId => Ksn[..16];

    /// <summary>The reader's transaction counter for this sale. A new sale always has a new counter.</summary>
    public uint Counter => uint.Parse(Ksn[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// The bytes the GCM tag covers beside the card number: every field but the ciphertext, in one fixed text
    /// form. The reader and the bank both call this, so the two sides cannot disagree on the order.
    /// </summary>
    public byte[] AssociatedData() => Encoding.ASCII.GetBytes(string.Join('|',
        "GASPUMP-CARD-1", Ksn, PinBlock ?? "", MaskedPan, Kind, $"{ExpiryMonth:00}{ExpiryYear:0000}", Zip ?? "", ReaderCheck));

    /// <summary>The GCM nonce: 12 zero bytes. Each data key comes from one counter and seals one message only, so a fixed nonce never repeats under a key.</summary>
    public static byte[] SealNonce() => new byte[12];

    /// <summary>The first 6 and the last 4 digits, with a star for each digit between.</summary>
    public static string Mask(string pan) =>
        pan.Length <= 10 ? new string('*', pan.Length) : pan[..6] + new string('*', pan.Length - 10) + pan[^4..];
}
