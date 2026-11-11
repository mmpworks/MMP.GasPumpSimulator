using System.Text;
using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;
using Xunit;

namespace GasPump.Tests.Anchors;

/// <summary>
/// Anchor A2 (docs/prd-dukpt-reader.md section 5). What the reader makes opens with a second implementation
/// that shares no code with MMP.NetworkSecurity: the .NET BCL AES and AES-GCM, and the format 4 steps written
/// out in <see cref="ReferenceDukpt"/>. The last test runs the other way: the bank opens what the BCL made.
/// </summary>
public class ReferenceAnchorTests
{
    private static readonly byte[] Bdk = Convert.FromHexString(TrainingKeys.BaseDerivationKeyHex);
    private static readonly CardData Debit = TestCards.Get("DebitGoodPin");
    private static readonly CardData Credit = TestCards.Get("Good");

    private static CardReader NewReader(string initialKeyId = "1234567800010001") =>
        new(BankKeys.Training().InitialKey(initialKeyId), initialKeyId);

    [Fact]
    public void A2_a_pin_block_the_reader_made_opens_with_the_bcl_reference_and_gives_the_typed_pin()
    {
        var reader = NewReader();
        var card = reader.Read(Debit, Tender.Debit, TestCards.Pin);

        var pinKey = ReferenceDukpt.WorkingKey(Bdk, Convert.FromHexString(card.Ksn), ReferenceDukpt.PinEncryption);
        var pin = ReferenceDukpt.OpenPinBlock(pinKey, Convert.FromHexString(card.PinBlock!), Debit.Pan);

        Assert.Equal(TestCards.Pin, pin);
    }

    [Fact]
    public void A2_the_card_number_the_reader_sealed_opens_with_the_bcl_aes_gcm_under_the_data_key()
    {
        var reader = NewReader();
        var card = reader.Read(Credit, Tender.Credit, TestCards.Zip);

        var dataKey = ReferenceDukpt.WorkingKey(Bdk, Convert.FromHexString(card.Ksn), ReferenceDukpt.DataEncrypt);
        var plain = ReferenceDukpt.OpenGcm(dataKey, ProtectedCard.SealNonce(), card.AssociatedData(), Convert.FromHexString(card.SealedPan));

        Assert.NotNull(plain);
        Assert.Equal(Credit.Pan, Encoding.ASCII.GetString(plain));
    }

    [Fact]
    public void A2_each_sale_uses_a_new_counter_and_the_bcl_reference_opens_every_one()
    {
        var reader = NewReader();
        for (uint expected = 1; expected <= 5; expected++)
        {
            var card = reader.Read(Debit, Tender.Debit, TestCards.Pin);
            Assert.Equal(expected, card.Counter);
            var pinKey = ReferenceDukpt.WorkingKey(Bdk, Convert.FromHexString(card.Ksn), ReferenceDukpt.PinEncryption);
            Assert.Equal(TestCards.Pin, ReferenceDukpt.OpenPinBlock(pinKey, Convert.FromHexString(card.PinBlock!), Debit.Pan));
        }
    }

    [Fact]
    public void A2_the_bank_opens_a_message_the_bcl_reference_built_with_no_library_code()
    {
        const string ksnHex = "123456780001000100000007";
        var ksn = Convert.FromHexString(ksnHex);
        var pinKey = ReferenceDukpt.WorkingKey(Bdk, ksn, ReferenceDukpt.PinEncryption);
        var dataKey = ReferenceDukpt.WorkingKey(Bdk, ksn, ReferenceDukpt.DataEncrypt);
        var pinBlock = ReferenceDukpt.SealPinBlock(pinKey, "8642", Debit.Pan, Convert.FromHexString("0011223344556677"));

        var unsealed = new ProtectedCard(ksnHex, "", Convert.ToHexString(pinBlock), ProtectedCard.Mask(Debit.Pan),
            CardKind.Debit, Debit.ExpiryMonth, Debit.ExpiryYear, null, CardCheck.Ok);
        var sealedPan = ReferenceDukpt.SealGcm(dataKey, ProtectedCard.SealNonce(), unsealed.AssociatedData(), Encoding.ASCII.GetBytes(Debit.Pan));
        var card = unsealed with { SealedPan = Convert.ToHexString(sealedPan) };

        var opened = BankKeys.Training().Open(card);

        Assert.Equal(DeclineReason.None, opened.Refusal);
        Assert.Equal(Debit.Pan, opened.Pan);
        Assert.Equal("8642", opened.Pin);
    }
}
