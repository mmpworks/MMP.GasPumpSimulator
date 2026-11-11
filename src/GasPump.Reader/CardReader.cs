using System.Security.Cryptography;
using System.Text;
using GasPump.Core;
using GasPump.Payment;
using NetSec.Crypto;
using NetSec.Dukpt;
using NetSec.Errors;

namespace GasPump.Reader;

/// <summary>What the reader shows about itself. No key: the key serial number, the counter, and how many key registers it holds.</summary>
public sealed record ReaderStatus(string InitialKeyId, string? LastKsn, uint? LastCounter, ulong NextCounter, int HeldKeyRegisters);

/// <summary>
/// The result of reading the reader's memory after a sale (attack e). HeldKeyRegisters is what an attacker
/// finds. SaleKeyDerivable says whether those keys give the PIN key of the named sale; NextKeyDerivable
/// says whether they give the next sale's key, which shows the probe can derive at all.
/// InjectionKeyErased says the injected initial key was wiped once the registers were loaded.
/// </summary>
public sealed record MemoryReadout(uint SaleCounter, int HeldKeyRegisters, bool SaleKeyDerivable, bool NextKeyDerivable, bool InjectionKeyErased);

/// <summary>A reader failure, with its diagnostic code (docs/diagnostics.md).</summary>
public sealed class ReaderException(DiagnosticCode code, string message) : Exception(code.Format(message))
{
    public DiagnosticCode Code { get; } = code;
}

/// <summary>
/// The secure card reader at the pump: the card slot, the PIN pad, and the AES DUKPT key registers
/// (MMP.NetworkSecurity <c>DukptPinPad</c>). The keypad digits and the card number enter here and leave
/// only as a <see cref="ProtectedCard"/>: the card number sealed with AES-128-GCM under the sale's data key,
/// the PIN as an ISO 9564 format 4 PIN block under the sale's PIN key, and the first 6 and last 4 digits.
/// Both keys come from one counter, and the register they came from is erased before the method returns.
///
/// This is a process boundary in a simulator, not a tamper-resistant device. The keys sit on the managed
/// heap, and the library's notes say what erasing them can and cannot prove.
/// </summary>
public sealed class CardReader
{
    private static readonly DukptKeyUsage[] SaleKeys = [DukptKeyUsage.PinEncryption, DukptKeyUsage.DataEncrypt];

    private readonly DukptPinPad _pad;
    private readonly Func<int, byte[]> _random;
    private readonly byte[] _injected;
    private readonly Dictionary<string, string> _keypad = new();
    private readonly Lock _gate = new();
    private ProtectedCard? _last;

    /// <summary>
    /// Loads the injected initial key into the registers, then erases <paramref name="initialKey"/>. The reader keeps no copy.
    /// <paramref name="random"/> gives the 8 random bytes of each PIN field; tests pass a fixed value to match published vectors.
    /// </summary>
    public CardReader(byte[] initialKey, string initialKeyId, Func<int, byte[]>? random = null)
    {
        _pad = new DukptPinPad(initialKey, Convert.FromHexString(initialKeyId));
        // The pad says the caller erases its own array. This is the reader's half of attack (e): no key from injection survives.
        CryptographicOperations.ZeroMemory(initialKey);
        _injected = initialKey;
        InitialKeyId = initialKeyId.ToUpperInvariant();
        _random = random ?? RandomNumberGenerator.GetBytes;
    }

    public string InitialKeyId { get; }

    /// <summary>
    /// The driver typed at the PIN pad. The digits stay in the reader for this sale. The pump gets a star for each digit,
    /// and any other key as typed, so the sale machine can still check the shape of the entry.
    /// </summary>
    public string Keypad(string saleId, string typed)
    {
        lock (_gate) _keypad[saleId] = typed;
        return new string(typed.Select(c => char.IsAsciiDigit(c) ? KeypadEntry.Mask : c).ToArray());
    }

    /// <summary>Reads the named test card for this sale with what was typed at the keypad. An unknown name reads as a card no bank knows.</summary>
    public ProtectedCard Read(string saleId, string cardName, Tender tender)
    {
        var card = TestCards.ByName.TryGetValue(cardName, out var known) ? known : new CardData("0000000000000000", 1, 2000, CardKind.Credit);
        string? typed;
        lock (_gate) { _keypad.Remove(saleId, out typed); }
        return Read(card, tender, typed);
    }

    /// <summary>
    /// Reads the card in the slot. The tender decides what the keypad entry is: the PIN for debit, sealed into a PIN block;
    /// the billing ZIP for credit, sent in clear. A debit sale with no PIN sends no PIN block, and the bank declines it.
    /// </summary>
    public ProtectedCard Read(CardData card, Tender tender, string? keypad)
    {
        // A debit tender always runs as debit. A credit tender keeps the card's own kind, so a debit card run as credit
        // still gets the bank's PIN check, with no PIN block, and declines on PIN (as before 0.2.0).
        bool debitTender = tender == Tender.Debit;
        var kind = debitTender ? CardKind.Debit : card.Kind;
        var check = CardValidator.CheckNumber(card.Pan);
        lock (_gate)
        {
            var keys = NextSaleKeys();
            var ksn = Convert.ToHexString(keys[0].Ksn);
            string? pinBlock = null;
            if (debitTender && keypad is { Length: >= 4 and <= 12 } pin && pin.All(char.IsAsciiDigit) && card.Pan.All(char.IsAsciiDigit))
                pinBlock = Convert.ToHexString(PinBlock.EncryptFormat4(keys[0].Key, pin, card.Pan, _random(8)));

            var unsealed = new ProtectedCard(ksn, "", pinBlock, ProtectedCard.Mask(card.Pan), kind, card.ExpiryMonth, card.ExpiryYear,
                debitTender ? null : keypad, check);
            var sealedPan = Gcm.Seal(new AesKey(keys[1].Key), ProtectedCard.SealNonce(), unsealed.AssociatedData(), Encoding.ASCII.GetBytes(card.Pan));
            Array.Clear(keys[0].Key);
            Array.Clear(keys[1].Key);
            _last = unsealed with { SealedPan = Convert.ToHexString(sealedPan) };
            return _last;
        }
    }

    public ReaderStatus Status
    {
        get { lock (_gate) return new(InitialKeyId, _last?.Ksn, _last?.Counter, _pad.NextCounter, _pad.HeldCount); }
    }

    /// <summary>
    /// Attack (e): read every key register the reader holds and try to rebuild the PIN key of a past sale from them.
    /// It also tries the next counter, which must succeed, so a "not derivable" answer is a real answer.
    /// </summary>
    public MemoryReadout ReadMemory(uint saleCounter)
    {
        lock (_gate)
        {
            bool sale = _pad.TryDeriveFromHeldKeys(saleCounter, DukptKeyUsage.PinEncryption, DukptKeyType.Aes128, out var k1);
            bool next = _pad.NextCounter <= uint.MaxValue
                && _pad.TryDeriveFromHeldKeys((uint)_pad.NextCounter, DukptKeyUsage.PinEncryption, DukptKeyType.Aes128, out var k2) && k2 is not null;
            if (k1 is not null) Array.Clear(k1);
            return new MemoryReadout(saleCounter, _pad.HeldCount, sale, next, _injected.All(b => b == 0));
        }
    }

    private DukptUse[] NextSaleKeys()
    {
        try { return _pad.NextTogether(SaleKeys, DukptKeyType.Aes128); }
        catch (NetSecException e) when (e.Error.Code == ErrorCodes.DukptCounterExhausted)
        {
            throw new ReaderException(DiagnosticCodes.ReaderKeyExhausted, "the card reader has used every key; it needs a new initial key");
        }
    }
}
