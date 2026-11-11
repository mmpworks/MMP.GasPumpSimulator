using System.Security.Cryptography;
using System.Text;

namespace GasPump.Tests.Anchors;

/// <summary>
/// A second implementation of AES DUKPT and PIN block format 4, for anchor test A2. It uses the .NET BCL
/// (System.Security.Cryptography.Aes and AesGcm) and shares no code with MMP.NetworkSecurity.
///
/// The steps follow ANSI X9.24-3-2017 section 6 (derivation data, initial key, intermediate keys, working keys)
/// and ISO 9564-1 format 4 (PIN field, PAN field, encrypt, XOR, encrypt). They are written from the standards
/// for AES-128 only. When the X9.24-3 PDF text is present, X9VectorTests checks this class against the
/// published values. Without the PDF, this class is a second implementation, not an outside source.
/// </summary>
internal static class ReferenceDukpt
{
    private const ushort KeyDerivation = 0x8000;
    private const ushort InitialKeyUsage = 0x8001;
    public const ushort PinEncryption = 0x1000;
    public const ushort DataEncrypt = 0x3000;

    public static byte[] InitialKey(byte[] bdk, byte[] initialKeyId)
    {
        // Bytes 8 to 15 of the derivation data hold the whole initial key ID.
        var data = Header(InitialKeyUsage);
        initialKeyId.CopyTo(data, 8);
        return Ecb(bdk, data);
    }

    /// <summary>The working key for one counter: walk the counter's 1 bits from the top, then derive the usage key.</summary>
    public static byte[] WorkingKey(byte[] bdk, byte[] ksn, ushort usage)
    {
        var initialKeyId = ksn[..8];
        uint counter = (uint)(ksn[8] << 24 | ksn[9] << 16 | ksn[10] << 8 | ksn[11]);
        var key = InitialKey(bdk, initialKeyId);
        uint reached = 0;
        for (int bit = 31; bit >= 0; bit--)
        {
            uint mask = 1u << bit;
            if ((counter & mask) == 0) continue;
            reached |= mask;
            key = Ecb(key, CounterData(KeyDerivation, initialKeyId, reached));
        }
        return Ecb(key, CounterData(usage, initialKeyId, counter));
    }

    /// <summary>ISO 9564-1 format 4, decrypt side: AES-decrypt, XOR the PAN field, AES-decrypt, read the PIN field.</summary>
    public static string? OpenPinBlock(byte[] pinKey, byte[] block, string pan)
    {
        var intermediate = EcbDecrypt(pinKey, block);
        var panField = PanField(pan);
        for (int i = 0; i < 16; i++) intermediate[i] ^= panField[i];
        var pinField = EcbDecrypt(pinKey, intermediate);
        int control = pinField[0] >> 4, length = pinField[0] & 0x0F;
        if (control != 4 || length is < 4 or > 12) return null;
        var pin = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            int nibble = Nibble(pinField, i + 2);
            if (nibble > 9) return null;
            pin.Append((char)('0' + nibble));
        }
        // Nibbles past the PIN up to nibble 15 are the fill value A.
        for (int i = length + 2; i < 16; i++) if (Nibble(pinField, i) != 0xA) return null;
        return pin.ToString();
    }

    /// <summary>ISO 9564-1 format 4, encrypt side, with a caller-chosen 8-byte random fill.</summary>
    public static byte[] SealPinBlock(byte[] pinKey, string pin, string pan, byte[] random8)
    {
        var pinField = new byte[16];
        pinField[0] = (byte)(0x40 | pin.Length);
        for (int i = 2; i < 16; i++) SetNibble(pinField, i, i - 2 < pin.Length ? pin[i - 2] - '0' : 0xA);
        random8.CopyTo(pinField, 8);
        var intermediate = Ecb(pinKey, pinField);
        var panField = PanField(pan);
        for (int i = 0; i < 16; i++) intermediate[i] ^= panField[i];
        return Ecb(pinKey, intermediate);
    }

    /// <summary>The PAN field: nibble 0 is the count of PAN digits past 12, then the PAN, left-padded to 12 digits, then zeros.</summary>
    private static byte[] PanField(string pan)
    {
        var digits = pan.PadLeft(12, '0');
        var field = new byte[16];
        SetNibble(field, 0, digits.Length - 12);
        for (int i = 0; i < digits.Length; i++) SetNibble(field, i + 1, digits[i] - '0');
        return field;
    }

    /// <summary>The BCL's own AES-GCM. Sealed bytes are the ciphertext followed by the 16-byte tag.</summary>
    public static byte[]? OpenGcm(byte[] key, byte[] nonce, byte[] aad, byte[] sealedBytes)
    {
        if (sealedBytes.Length < 16) return null;
        var cipher = sealedBytes[..^16];
        var tag = sealedBytes[^16..];
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        try { gcm.Decrypt(nonce, cipher, tag, plain, aad); }
        catch (AuthenticationTagMismatchException) { return null; }
        return plain;
    }

    public static byte[] SealGcm(byte[] key, byte[] nonce, byte[] aad, byte[] plain)
    {
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag, aad);
        return [.. cipher, .. tag];
    }

    /// <summary>Bytes 0 to 7: version 1, block counter 1, usage, algorithm 2 (AES-128), key length 128 bits.</summary>
    private static byte[] Header(ushort usage)
    {
        var data = new byte[16];
        data[0] = 1;
        data[1] = 1;
        data[2] = (byte)(usage >> 8);
        data[3] = (byte)usage;
        data[5] = 2;
        data[7] = 0x80;
        return data;
    }

    /// <summary>Bytes 8 to 11 hold the last 4 bytes of the initial key ID, bytes 12 to 15 the counter.</summary>
    private static byte[] CounterData(ushort usage, byte[] initialKeyId, uint counter)
    {
        var data = Header(usage);
        Array.Copy(initialKeyId, 4, data, 8, 4);
        data[12] = (byte)(counter >> 24);
        data[13] = (byte)(counter >> 16);
        data[14] = (byte)(counter >> 8);
        data[15] = (byte)counter;
        return data;
    }

    private static byte[] Ecb(byte[] key, byte[] block)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    private static byte[] EcbDecrypt(byte[] key, byte[] block)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(block, PaddingMode.None);
    }

    private static int Nibble(byte[] field, int index) => index % 2 == 0 ? field[index / 2] >> 4 : field[index / 2] & 0x0F;

    private static void SetNibble(byte[] field, int index, int value) =>
        field[index / 2] |= (byte)(index % 2 == 0 ? value << 4 : value);
}
