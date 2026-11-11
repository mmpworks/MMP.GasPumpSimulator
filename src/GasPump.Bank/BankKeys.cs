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
    public static CardOpening Refuse(DeclineReason reason) => new(reason, null, null);
}

/// <summary>
/// The bank's side of AES DUKPT: the base derivation key, and the key serial numbers it has accepted.
/// It keeps no key per reader. It derives the keys of each sale again from the KSN (MMP.NetworkSecurity
/// DukptBank). The key injection facility holds the same base key, so this class also gives a reader
/// its initial key, the one "inject the initial key" step of the PRD.
///
/// Open checks in a fixed order: the KSN ledger (a replay is refused before any key work), the GCM tag
/// over the card number and every clear field, then the PIN block for debit. A KSN goes in the ledger
/// only when the message opens, so a forged message cannot burn the counter of the real one.
/// </summary>
public sealed class BankKeys
{
    private readonly byte[] _bdk;
    private readonly KsnLedger _ledger = new();
    private readonly List<string> _seen = [];
    private readonly Lock _gate = new();

    public BankKeys(byte[] baseDerivationKey) => _bdk = (byte[])baseDerivationKey.Clone();

    /// <summary>A bank with the training base key.</summary>
    public static BankKeys Training() => new(Convert.FromHexString(TrainingKeys.BaseDerivationKeyHex));

    /// <summary>A new initial key ID for a reader: the key set ID, then the pump number (1 byte) and 3 random bytes.</summary>
    public static string NewInitialKeyId(int pumpNumber) =>
        TrainingKeys.KeySetId + ((byte)pumpNumber).ToString("X2") + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    /// <summary>The initial key a reader with this key ID gets at injection. The caller hands it to the reader, which erases it.</summary>
    public byte[] InitialKey(string initialKeyId) => DukptBank.DeriveInitialKey(_bdk, Convert.FromHexString(initialKeyId));

    public CardOpening Open(ProtectedCard card)
    {
        lock (_gate)
        {
            // Bytes that are not even hex of the right size never reach the key work.
            if (!TryHex(card.Ksn, out var ksn) || ksn.Length != 12 || !TryHex(card.SealedPan, out var sealedPan)
                || !TryHex(card.PinBlock ?? "", out var pinBlock))
                return CardOpening.Refuse(DeclineReason.CardDataUnreadable);
            if (_ledger.Contains(ksn)) return CardOpening.Refuse(DeclineReason.KeyReplayed);

            var dataKey = DukptBank.DeriveWorkingKey(_bdk, ksn, DukptKeyUsage.DataEncrypt, DukptKeyType.Aes128);
            if (!Gcm.TryOpen(new AesKey(dataKey), ProtectedCard.SealNonce(), card.AssociatedData(), sealedPan, out var plain))
                return CardOpening.Refuse(DeclineReason.CardDataUnreadable);
            var pan = Encoding.ASCII.GetString(plain);

            if (card.Kind == CardKind.Debit && card.PinBlock is not null)
            {
                // The tag covered the PIN block, so a shape failure here means a reader with a broken PIN pad. It is still the driver's PIN that failed.
                var sale = DukptBank.OpenPinSale(_bdk, _ledger, ksn, pinBlock, pan, DukptKeyType.Aes128);
                if (sale.Code != null) return CardOpening.Refuse(DeclineReason.InvalidPin);
                _seen.Add(card.Ksn);
                return new CardOpening(DeclineReason.None, pan, sale.Pin);
            }

            _ledger.Record(ksn);
            _seen.Add(card.Ksn);
            return new CardOpening(DeclineReason.None, pan, null);
        }
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

    /// <summary>Bring back the accepted KSNs after a restart, so a message recorded before the restart is still a replay.</summary>
    public void Restore(IEnumerable<string> seen)
    {
        lock (_gate)
            foreach (var ksn in seen)
            {
                _ledger.Record(Convert.FromHexString(ksn));
                _seen.Add(ksn);
            }
    }

    /// <summary>The sale's PIN key. Internal: anchor test A1 compares it with the published value.</summary>
    internal byte[] PinKey(string ksn) => DukptBank.DeriveWorkingKey(_bdk, Convert.FromHexString(ksn), DukptKeyUsage.PinEncryption, DukptKeyType.Aes128);

    /// <summary>The sale's data key. Internal: anchor test A1 compares it with the published value.</summary>
    internal byte[] DataKey(string ksn) => DukptBank.DeriveWorkingKey(_bdk, Convert.FromHexString(ksn), DukptKeyUsage.DataEncrypt, DukptKeyType.Aes128);
}
