#if X9_VECTORS
using System.Text.RegularExpressions;
using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;
using Xunit;

namespace GasPump.Tests.Anchors;

/// <summary>
/// Anchor A1 (docs/prd-dukpt-reader.md section 5). The keys the bank derives and the PIN blocks the reader
/// makes are the values published in the ANSI X9.24-3-2017 test-vector supplement
/// (X9.24-3-2017-Test-Vectors-20180129-1.pdf, x9.org, SHA-256 ae9bb01d...c4cc).
///
/// The expected values come from the PDF text itself, never from this repo: tools/fetch-x9-vectors.ps1 downloads
/// the PDF, checks the hash, and writes the text that this class reads. The class compiles only when that text
/// is present, because the document is copyright ASC X9 and stays out of git.
///
/// The check: every 32-bit word of a derived value appears in the PDF, and for a key the four words appear in
/// order as one run. A 128-bit value cannot appear in the PDF by chance, so a match means the value is published.
/// The PDF splits PIN blocks across two columns, so a PIN block is checked word by word.
/// </summary>
public class X9VectorTests
{
    // The test BDK and initial key ID of the supplement, section 2.2. They are also this simulator's training keys.
    private const string X9InitialKeyId = "1234567890123456";
    private const string X9PinRandom = "2F69ADDE2E9E7ACE";
    private const string X9Pan = "4111111111111111";
    private const string X9Pin = "1234";

    private static readonly string[] Words = Regex.Matches(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "x9", "X9.24-3-2017-Test-Vectors.txt")),
            @"\b[0-9A-F]{8}\b")
        .Select(m => m.Value).ToArray();

    private static bool PublishedAsOneRun(byte[] value)
    {
        var run = WordsOf(value);
        for (int i = 0; i + run.Length <= Words.Length; i++)
            if (run.Select((w, k) => Words[i + k] == w).All(x => x)) return true;
        return false;
    }

    private static bool EveryWordPublished(byte[] value) => WordsOf(value).All(Words.Contains);

    private static string[] WordsOf(byte[] value) => Convert.ToHexString(value).Chunk(8).Select(c => new string(c)).ToArray();

    private static string Ksn(uint counter) => X9InitialKeyId + counter.ToString("X8");

    [Fact]
    public void A1_the_text_holds_the_supplement_and_its_test_bdk()
    {
        Assert.True(Words.Length > 1000, $"only {Words.Length} hex words read; is the text the X9.24-3 supplement?");
        Assert.True(PublishedAsOneRun(Convert.FromHexString(TrainingKeys.BaseDerivationKeyHex)));
    }

    [Fact]
    public void A1_the_initial_key_for_the_published_bdk_and_key_id_is_the_published_value()
    {
        Assert.True(PublishedAsOneRun(BankKeys.Training().InitialKey(X9InitialKeyId)));
    }

    [Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)]
    [InlineData(5u)] [InlineData(6u)] [InlineData(7u)] [InlineData(8u)]
    [InlineData(0x845FEDu)]
    public void A1_the_bank_pin_key_and_data_key_for_a_published_counter_are_the_published_values(uint counter)
    {
        var bank = BankKeys.Training();
        Assert.True(PublishedAsOneRun(bank.PinKey(Ksn(counter))), $"PIN key for counter {counter:X} is not in the PDF");
        Assert.True(PublishedAsOneRun(bank.DataKey(Ksn(counter))), $"data key for counter {counter:X} is not in the PDF");
    }

    [Fact]
    public void A1_the_reader_pin_blocks_for_transactions_1_to_8_are_the_published_blocks()
    {
        var bank = BankKeys.Training();
        var reader = new CardReader(bank.InitialKey(X9InitialKeyId), X9InitialKeyId, _ => Convert.FromHexString(X9PinRandom));
        var card = new CardData(X9Pan, 12, 2030, CardKind.Debit);

        for (uint counter = 1; counter <= 8; counter++)
        {
            var read = reader.Read(card, Tender.Debit, X9Pin);
            Assert.Equal(Ksn(counter), read.Ksn);
            Assert.True(EveryWordPublished(Convert.FromHexString(read.PinBlock!)), $"PIN block {read.PinBlock} for transaction {counter} is not in the PDF");
        }
    }

    [Theory]
    [InlineData(1u)] [InlineData(5u)] [InlineData(8u)] [InlineData(0x845FEDu)]
    public void A1_the_bcl_reference_of_anchor_A2_derives_the_published_values(uint counter)
    {
        var bdk = Convert.FromHexString(TrainingKeys.BaseDerivationKeyHex);
        var ksn = Convert.FromHexString(Ksn(counter));
        Assert.True(PublishedAsOneRun(ReferenceDukpt.WorkingKey(bdk, ksn, ReferenceDukpt.PinEncryption)));
        Assert.True(PublishedAsOneRun(ReferenceDukpt.WorkingKey(bdk, ksn, ReferenceDukpt.DataEncrypt)));
    }
}
#endif
