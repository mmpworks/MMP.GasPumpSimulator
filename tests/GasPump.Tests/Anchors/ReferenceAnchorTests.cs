using System.Text;
using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;
using Xunit;

namespace GasPump.Tests.Anchors;

/// <summary>
/// Anchor A2 (docs/prd-dukpt-reader.md section 5). What the reader makes opens with a second implementation
/// that shares no code with MMP.NetworkSecurity or with the product's wire layout: the .NET BCL AES and
/// AES-GCM, the format 4 steps written out in <see cref="ReferenceDukpt"/>, and the associated data, the nonce,
/// the mask and the BDK typed in that class. The last test runs the other way: the bank opens what the BCL made.
/// The products' types are used only as the containers the reader hands over.
/// </summary>
public class ReferenceAnchorTests
{
    private static readonly byte[] Bdk = Convert.FromHexString(ReferenceDukpt.X9TestBdkHex);
    private const string DebitPan = "6011111111111117";
    private const string CreditPan = "4111111111111111";
    private const string Pin = "1234";
    private const string Zip = "12345";

    private static CardReader NewReader(string initialKeyId = "1234567800010001") =>
        new(BankKeys.Training().InitialKey(initialKeyId), initialKeyId);

    private static byte[] Aad(ProtectedCard c) =>
        ReferenceDukpt.AssociatedData(c.Ksn, c.PinBlock, c.MaskedPan, c.Kind.ToString(), c.ExpiryMonth, c.ExpiryYear, c.Zip, c.ReaderCheck.ToString());

    [Fact]
    public void A2_a_pin_block_the_reader_made_opens_with_the_bcl_reference_and_gives_the_typed_pin()
    {
        var card = NewReader().Read(new CardData(DebitPan, 12, 2030, CardKind.Debit), Tender.Debit, Pin);

        var pinKey = ReferenceDukpt.WorkingKey(Bdk, Convert.FromHexString(card.Ksn), ReferenceDukpt.PinEncryption);

        Assert.Equal(Pin, ReferenceDukpt.OpenPinBlock(pinKey, Convert.FromHexString(card.PinBlock!), DebitPan));
    }

    [Fact]
    public void A2_the_card_number_the_reader_sealed_opens_with_the_bcl_aes_gcm_and_the_hand_written_layout()
    {
        var card = NewReader().Read(new CardData(CreditPan, 12, 2030, CardKind.Credit), Tender.Credit, Zip);

        Assert.Equal(ReferenceDukpt.Mask(CreditPan), card.MaskedPan);
        var dataKey = ReferenceDukpt.WorkingKey(Bdk, Convert.FromHexString(card.Ksn), ReferenceDukpt.DataEncrypt);
        var plain = ReferenceDukpt.OpenGcm(dataKey, ReferenceDukpt.Nonce(), Aad(card), Convert.FromHexString(card.SealedPan));

        Assert.NotNull(plain);
        Assert.Equal(CreditPan, Encoding.ASCII.GetString(plain));
    }

    [Fact]
    public void A2_each_sale_uses_a_new_counter_and_the_bcl_reference_opens_every_one()
    {
        var reader = NewReader();
        for (uint expected = 1; expected <= 5; expected++)
        {
            var card = reader.Read(new CardData(DebitPan, 12, 2030, CardKind.Debit), Tender.Debit, Pin);
            Assert.Equal(expected, card.Counter);
            var pinKey = ReferenceDukpt.WorkingKey(Bdk, Convert.FromHexString(card.Ksn), ReferenceDukpt.PinEncryption);
            Assert.Equal(Pin, ReferenceDukpt.OpenPinBlock(pinKey, Convert.FromHexString(card.PinBlock!), DebitPan));
        }
    }

    [Fact]
    public void A2_the_bank_opens_a_message_the_bcl_reference_built_with_no_library_or_layout_code()
    {
        const string ksnHex = "123456780001000100000007";
        var ksn = Convert.FromHexString(ksnHex);
        var pinKey = ReferenceDukpt.WorkingKey(Bdk, ksn, ReferenceDukpt.PinEncryption);
        var dataKey = ReferenceDukpt.WorkingKey(Bdk, ksn, ReferenceDukpt.DataEncrypt);
        var pinBlock = Convert.ToHexString(ReferenceDukpt.SealPinBlock(pinKey, "8642", DebitPan, Convert.FromHexString("0011223344556677")));
        var masked = ReferenceDukpt.Mask(DebitPan);
        var aad = ReferenceDukpt.AssociatedData(ksnHex, pinBlock, masked, "Debit", 12, 2030, null, "Ok");
        var sealedPan = ReferenceDukpt.SealGcm(dataKey, ReferenceDukpt.Nonce(), aad, Encoding.ASCII.GetBytes(DebitPan));
        var card = new ProtectedCard(ksnHex, Convert.ToHexString(sealedPan), pinBlock, masked, CardKind.Debit, 12, 2030, null, CardCheck.Ok);

        var opened = BankKeys.Training().Open(card);

        Assert.Equal(DeclineReason.None, opened.Refusal);
        Assert.Equal(DebitPan, opened.Pan);
        Assert.Equal("8642", opened.Pin);
    }
}
