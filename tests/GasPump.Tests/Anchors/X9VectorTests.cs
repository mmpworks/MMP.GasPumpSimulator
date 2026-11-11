using System.Text.RegularExpressions;
using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;
using Xunit;

namespace GasPump.Tests.Anchors;

/// <summary>Where anchor A1 reads the X9.24-3 text, and the skip reason when it is absent.</summary>
internal static class X9Text
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "x9", "X9.24-3-2017-Test-Vectors.txt");
    public static bool Present => File.Exists(Path);
    public const string SkipReason =
        "Anchor A1 needs the ANSI X9.24-3-2017 test-vector text, which is copyright ASC X9 and stays out of git. Run: pwsh tools/fetch-x9-vectors.ps1";
}

/// <summary>A fact of anchor A1. Without the vector text it shows as skipped, with the reason and the fetch command.</summary>
public sealed class X9FactAttribute : FactAttribute
{
    public X9FactAttribute() { if (!X9Text.Present) Skip = X9Text.SkipReason; }
}

/// <summary>A theory of anchor A1. Without the vector text it shows as skipped, with the reason and the fetch command.</summary>
public sealed class X9TheoryAttribute : TheoryAttribute
{
    public X9TheoryAttribute() { if (!X9Text.Present) Skip = X9Text.SkipReason; }
}

/// <summary>
/// Anchor A1 (docs/prd-dukpt-reader.md section 5). The keys the bank derives and the PIN blocks the reader
/// makes are the values published in the ANSI X9.24-3-2017 test-vector supplement
/// (X9.24-3-2017-Test-Vectors-20180129-1.pdf, x9.org, SHA-256 ae9bb01d...c4cc).
///
/// The expected values come from the PDF text itself, never from this repo: tools/fetch-x9-vectors.ps1 downloads
/// the PDF, checks the hash, and writes the text that this class reads.
///
/// What the checks prove, and what they do not:
/// - A key passes when its four 32-bit words appear in order, as one run, somewhere in the PDF. A 128-bit value
///   cannot appear by chance, so a match proves the value is published. It does not prove which label it sits under.
/// - A PIN block passes when it sits in its own transaction's section: the first three words follow
///   "Encrypted PIN Block:", and the fourth is the last word of the next line, where pdftotext puts the second
///   column. That ties the block to its transaction. It depends on the pdftotext layout (poppler, default mode);
///   another layout fails the test rather than passing it.
/// </summary>
public class X9VectorTests
{
    // The test BDK and initial key ID of the supplement, section 2.2. They are also this simulator's training keys.
    private const string X9InitialKeyId = "1234567890123456";
    private const string X9PinRandom = "2F69ADDE2E9E7ACE";
    private const string X9Pan = "4111111111111111";
    private const string X9Pin = "1234";

    private static readonly Lazy<string> Text = new(() => File.ReadAllText(X9Text.Path));
    private static readonly Lazy<string[]> Words = new(() => Regex.Matches(Text.Value, @"\b[0-9A-F]{8}\b").Select(m => m.Value).ToArray());

    private static bool PublishedAsOneRun(byte[] value)
    {
        var run = WordsOf(value);
        var words = Words.Value;
        for (int i = 0; i + run.Length <= words.Length; i++)
            if (run.Select((w, k) => words[i + k] == w).All(x => x)) return true;
        return false;
    }

    /// <summary>The PIN block in the section "All Key Usages for Transaction {n}", placed as the class summary says.</summary>
    private static bool PinBlockPublishedFor(uint transaction, string pinBlockHex)
    {
        var w = WordsOf(Convert.FromHexString(pinBlockHex));
        var start = Text.Value.IndexOf($"All Key Usages for Transaction {transaction} (AES-128 under AES-128 BDK)", StringComparison.Ordinal);
        if (start < 0) return false;
        var end = Text.Value.IndexOf("All Key Usages for Transaction", start + 10, StringComparison.Ordinal);
        var section = Text.Value[start..(end < 0 ? Text.Value.Length : end)];
        var pattern = $@"Encrypted PIN Block: {w[0]} {w[1]} {w[2]}\s+[0-9A-F]{{8}} [0-9A-F]{{8}} {w[3]}\b";
        return Regex.IsMatch(section, pattern);
    }

    private static string[] WordsOf(byte[] value) => Convert.ToHexString(value).Chunk(8).Select(c => new string(c)).ToArray();

    private static string Ksn(uint counter) => X9InitialKeyId + counter.ToString("X8");

    [X9Fact]
    public void A1_the_text_holds_the_supplement_and_its_test_bdk()
    {
        Assert.True(Words.Value.Length > 1000, $"only {Words.Value.Length} hex words read; is the text the X9.24-3 supplement?");
        Assert.True(PublishedAsOneRun(Convert.FromHexString(TrainingKeys.BaseDerivationKeyHex)));
    }

    [X9Fact]
    public void A1_the_initial_key_for_the_published_bdk_and_key_id_is_the_published_value()
    {
        Assert.True(PublishedAsOneRun(BankKeys.Training().InitialKey(X9InitialKeyId)));
    }

    [X9Theory]
    [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)]
    [InlineData(5u)] [InlineData(6u)] [InlineData(7u)] [InlineData(8u)]
    [InlineData(0x845FEDu)]
    public void A1_the_bank_pin_key_and_data_key_for_a_published_counter_are_the_published_values(uint counter)
    {
        var bank = BankKeys.Training();
        Assert.True(PublishedAsOneRun(bank.PinKey(Ksn(counter))), $"PIN key for counter {counter:X} is not in the PDF");
        Assert.True(PublishedAsOneRun(bank.DataKey(Ksn(counter))), $"data key for counter {counter:X} is not in the PDF");
    }

    [X9Fact]
    public void A1_the_reader_pin_blocks_for_transactions_1_to_8_are_the_published_blocks_in_their_sections()
    {
        var bank = BankKeys.Training();
        var reader = new CardReader(bank.InitialKey(X9InitialKeyId), X9InitialKeyId, _ => Convert.FromHexString(X9PinRandom));
        var card = new CardData(X9Pan, 12, 2030, CardKind.Debit);

        for (uint counter = 1; counter <= 8; counter++)
        {
            var read = reader.Read(card, Tender.Debit, X9Pin);
            Assert.Equal(Ksn(counter), read.Ksn);
            Assert.True(PinBlockPublishedFor(counter, read.PinBlock!), $"PIN block {read.PinBlock} is not the block of transaction {counter}");
        }
    }

    [X9Fact]
    public void A1_the_pin_block_check_refuses_a_block_from_another_transaction()
    {
        var bank = BankKeys.Training();
        var reader = new CardReader(bank.InitialKey(X9InitialKeyId), X9InitialKeyId, _ => Convert.FromHexString(X9PinRandom));
        var first = reader.Read(new CardData(X9Pan, 12, 2030, CardKind.Debit), Tender.Debit, X9Pin);
        Assert.True(PinBlockPublishedFor(1, first.PinBlock!));
        Assert.False(PinBlockPublishedFor(2, first.PinBlock!));
    }

    [X9Theory]
    [InlineData(1u)] [InlineData(5u)] [InlineData(8u)] [InlineData(0x845FEDu)]
    public void A1_the_bcl_reference_of_anchor_A2_derives_the_published_values(uint counter)
    {
        var bdk = Convert.FromHexString(ReferenceDukpt.X9TestBdkHex);
        var ksn = Convert.FromHexString(Ksn(counter));
        Assert.True(PublishedAsOneRun(ReferenceDukpt.WorkingKey(bdk, ksn, ReferenceDukpt.PinEncryption)));
        Assert.True(PublishedAsOneRun(ReferenceDukpt.WorkingKey(bdk, ksn, ReferenceDukpt.DataEncrypt)));
    }
}
