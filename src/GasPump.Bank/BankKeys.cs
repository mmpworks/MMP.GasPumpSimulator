using System.Security.Cryptography;
using System.Text;
using GasPump.Payment;
using NetSec.Crypto;
using NetSec.Dukpt;

namespace GasPump.Bank;

/// <summary>
/// TRAINING KEYS. Do not use them for anything but this simulator.
/// The base derivation key is the test BDK of the ANSI X9.24-3-2017 test-vector supplement (section 2.2),
/// so anyone can derive every key this simulator uses and check it against the published values.
/// The key set ID is the first 4 bytes of the supplement's initial key ID.
/// </summary>
public static class TrainingKeys
{
    public const string BaseDerivationKeyHex = "FEDCBA9876543210F1F1F1F1F1F1F1F1";
    public const string KeySetId = "12345678";
}

/// <summary>What the bank opened, or why it refused. Pan and Pin are null on a refusal; Pin is null for credit.</summary>
public sealed record CardOpening(DeclineReason Refusal, string? Pan, string? Pin)
{
    public bool Opened => Refusal == DeclineReason.None;

    /// <summary>Masked, so a log line or a debugger never shows the opened digits.</summary>
    public override string ToString() =>
        $"CardOpening {{ Refusal = {Refusal}, Pan = {(Pan is null ? "null" : ProtectedCard.Mask(Pan))}, Pin = {Secret.Mark(Pin)} }}";
    public static CardOpening Refuse(DeclineReason reason) => new(reason, null, null);
}

/// <summary>
/// The bank's side of AES DUKPT: the base derivation key, and the key serial numbers it has accepted.
/// It keeps no key per reader. It derives the keys of each sale again from the KSN (MMP.NetworkSecurity
/// DukptBank). The key injection facility holds the same base key, so this class also gives a reader
/// its initial key, the one "inject the initial key" step of the PRD.
///
/// Open checks in a fixed order:
/// 1. The GCM tag over the card number and every clear field. A message that fails it is refused as
///    <c>CardDataUnreadable</c> whatever its KSN, so the answer never tells a caller which KSNs the bank has seen.
/// 2. The counter against the device's high-water mark: a counter at or below the highest one accepted for
///    that initial key ID is <c>KeyReplayed</c>, seen or not. A gap ahead is accepted (a sale that never reached
///    the bank leaves one). The library's <c>KsnLedger</c> also refuses an exact repeat.
/// 3. The PIN block, for debit.
/// A message is recorded only when it opens, so a forged message cannot burn the counter of the real one.
/// The high-water mark sits here, on top of the library: library backlog item, "per-device counter floor in DukptBank".
///
/// Injection refuses an initial key ID this bank has issued before. Each data key seals one message, which is
/// what makes the fixed GCM nonce safe; a second reader with the same ID would repeat (key, nonce) pairs.
/// </summary>
public sealed class BankKeys
{
    private readonly byte[] _bdk;
    private readonly KsnLedger _ledger = new();
    private readonly List<string> _seen = [];
    private readonly Dictionary<string, uint> _highest = new();
    private readonly HashSet<string> _issued = new();
    private readonly Lock _gate = new();

    public BankKeys(byte[] baseDerivationKey) => _bdk = (byte[])baseDerivationKey.Clone();

    /// <summary>A bank with the training base key.</summary>
    public static BankKeys Training() => new(Convert.FromHexString(TrainingKeys.BaseDerivationKeyHex));

    /// <summary>A new initial key ID for a reader: the key set ID, then the pump number (1 byte) and 3 random bytes.</summary>
    public static string NewInitialKeyId(int pumpNumber) =>
        TrainingKeys.KeySetId + ((byte)pumpNumber).ToString("X2") + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    /// <summary>
    /// The initial key a reader with this key ID gets at injection. The caller hands it to the reader, which erases it.
    /// An ID this bank issued before is refused with <see cref="DuplicateKeyIdCode"/>: one ID, one reader, one set of counters.
    /// </summary>
    public byte[] InitialKey(string initialKeyId)
    {
        var id = initialKeyId.ToUpperInvariant();
        lock (_gate)
            if (!_issued.Add(id))
                throw new InvalidOperationException($"[{DuplicateKeyIdCode}] initial key id {id} was already injected by this bank; a second reader with it would repeat every key and nonce");
        return DukptBank.DeriveInitialKey(_bdk, Convert.FromHexString(id));
    }

    /// <summary>The stable code of a refused second injection.</summary>
    public const string DuplicateKeyIdCode = "BANK.KEY.DUPLICATEID";

    /// <summary>Every initial key ID this bank injected, for the bank's books.</summary>
    public IReadOnlyList<string> IssuedKeyIds { get { lock (_gate) return _issued.Order().ToArray(); } }

    public CardOpening Open(ProtectedCard card)
    {
        lock (_gate)
        {
            // Bytes that are not even hex of the right size never reach the key work.
            if (!TryHex(card.Ksn, out var ksn) || ksn.Length != 12 || !TryHex(card.SealedPan, out var sealedPan)
                || !TryHex(card.PinBlock ?? "", out var pinBlock))
                return CardOpening.Refuse(DeclineReason.CardDataUnreadable);

            var dataKey = DukptBank.DeriveWorkingKey(_bdk, ksn, DukptKeyUsage.DataEncrypt, DukptKeyType.Aes128);
            if (!Gcm.TryOpen(new AesKey(dataKey), ProtectedCard.SealNonce(), card.AssociatedData(), sealedPan, out var plain))
                return CardOpening.Refuse(DeclineReason.CardDataUnreadable);
            var pan = Encoding.ASCII.GetString(plain);

            if (AtOrBelowHighWater(card) || _ledger.Contains(ksn)) return CardOpening.Refuse(DeclineReason.KeyReplayed);

            if (card.Kind == CardKind.Debit && card.PinBlock is not null)
            {
                // The tag covered the PIN block, so a shape failure here means a reader with a broken PIN pad. It is still the driver's PIN that failed.
                var sale = DukptBank.OpenPinSale(_bdk, _ledger, ksn, pinBlock, pan, DukptKeyType.Aes128);
                if (sale.Code != null) return CardOpening.Refuse(DeclineReason.InvalidPin);
                Accept(card.Ksn);
                return new CardOpening(DeclineReason.None, pan, sale.Pin);
            }

            _ledger.Record(ksn);
            Accept(card.Ksn);
            return new CardOpening(DeclineReason.None, pan, null);
        }
    }

    private bool AtOrBelowHighWater(ProtectedCard card) =>
        _highest.TryGetValue(card.InitialKeyId.ToUpperInvariant(), out var top) && card.Counter <= top;

    /// <summary>Records an accepted KSN and raises its device's high-water mark. The caller holds the gate.</summary>
    private void Accept(string ksnHex)
    {
        var ksn = ksnHex.ToUpperInvariant();
        _seen.Add(ksn);
        var id = ksn[..16];
        var counter = uint.Parse(ksn[16..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        if (!_highest.TryGetValue(id, out var top) || counter > top) _highest[id] = counter;
    }

    private static bool TryHex(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 2 != 0 || !text.All(Uri.IsHexDigit)) return false;
        bytes = Convert.FromHexString(text);
        return true;
    }

    /// <summary>Every KSN the bank accepted, oldest first, for the bank's books.</summary>
    public IReadOnlyList<string> SeenKsns { get { lock (_gate) return _seen.ToArray(); } }

    /// <summary>
    /// Bring back the accepted KSNs and the issued key IDs after a restart, so a message recorded before the restart
    /// is still a replay, the high-water marks stand, and no ID is injected twice.
    /// </summary>
    public void Restore(IEnumerable<string> seen, IEnumerable<string>? issued = null)
    {
        lock (_gate)
        {
            foreach (var ksn in seen)
            {
                _ledger.Record(Convert.FromHexString(ksn));
                Accept(ksn);
            }
            foreach (var id in issued ?? []) _issued.Add(id.ToUpperInvariant());
        }
    }

    /// <summary>The sale's PIN key. Internal: anchor test A1 compares it with the published value.</summary>
    internal byte[] PinKey(string ksn) => DukptBank.DeriveWorkingKey(_bdk, Convert.FromHexString(ksn), DukptKeyUsage.PinEncryption, DukptKeyType.Aes128);

    /// <summary>The sale's data key. Internal: anchor test A1 compares it with the published value.</summary>
    internal byte[] DataKey(string ksn) => DukptBank.DeriveWorkingKey(_bdk, Convert.FromHexString(ksn), DukptKeyUsage.DataEncrypt, DukptKeyType.Aes128);
}
