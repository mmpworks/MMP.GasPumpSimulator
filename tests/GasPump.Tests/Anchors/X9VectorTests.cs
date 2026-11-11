using System.Text.RegularExpressions;
using GasPump.Bank;
using GasPump.Core;
using GasPump.Payment;
using GasPump.Reader;
using Xunit;

namespace GasPump.Tests.Anchors;

/// <summary>Where anchor A1 reads the fetched X9.24-3 text, and the skip reason when it is absent.</summary>
internal static class X9Text
{
    /// <summary>Set to 1 to run as a fresh clone does: the fetched text counts as absent.</summary>
    public const string AbsentSwitch = "GASPUMP_X9_TEXT_ABSENT";

    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "x9", "X9.24-3-2017-Test-Vectors.txt");
    public static bool Present => File.Exists(Path) && Environment.GetEnvironmentVariable(AbsentSwitch) != "1";
    public const string SkipReason =
        "This check compares the committed vector file with the ANSI X9.24-3-2017 test-vector text, which is copyright ASC X9 and stays out of git. Run: pwsh tools/fetch-x9-vectors.ps1";
}

/// <summary>A fact that needs the fetched X9.24-3 text. Without it the fact shows as skipped, with the fetch command.</summary>
public sealed class X9TextFactAttribute : FactAttribute
{
    public X9TextFactAttribute() { if (!X9Text.Present) Skip = X9Text.SkipReason; }
}

/// <summary>
/// The committed vector file, Anchors/x9-24-3-aes-dukpt.vectors. tools/extract-x9-vectors.py writes it from the
/// fetched text. Groups are "[name]" lines; each value is a "name = value" line; "#" and "source:" lines are notes.
/// </summary>
internal static class X9VectorFile
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "Anchors", "x9-24-3-aes-dukpt.vectors");

    public static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Groups = new(Read);

    public static IReadOnlyDictionary<string, string> Group(string name) => Groups.Value[name];

    /// <summary>The transaction groups, by counter, in counter order.</summary>
    public static IEnumerable<(uint Counter, IReadOnlyDictionary<string, string> Values)> Transactions() =>
        Groups.Value
            .Where(g => g.Key.StartsWith("transaction ", StringComparison.Ordinal))
            .Select(g => (uint.Parse(g.Key["transaction ".Length..]), g.Value))
            .OrderBy(t => t.Item1);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Read()
    {
        var groups = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadLines(Path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("source:", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = [];
                groups.Add(line[1..^1], current);
                continue;
            }
            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (current is null || eq < 0) throw new FormatException($"{Path}: unreadable line '{line}'");
            current.Add(line[..eq], line[(eq + 3)..]);
        }
        return groups;
    }
}

/// <summary>
/// Anchor A1 (docs/prd-dukpt-reader.md section 5). The keys the bank derives and the PIN blocks the reader makes
/// are the values published in the ANSI X9.24-3-2017 test-vector supplement
/// (X9.24-3-2017-Test-Vectors-20180129-1.pdf, x9.org, SHA-256 ae9bb01d...c4cc).
///
/// Two tests:
/// - The first always runs. The reader, the bank and the BCL reference of anchor A2 derive each key and PIN block,
///   and each one equals the committed vector file.
/// - The second runs when tools/fetch-x9-vectors.ps1 has fetched the PDF text. Every value in the committed file
///   equals the fetched text in its place. That proves the file is a true copy. Without the text it shows as
///   skipped, with the fetch command. GASPUMP_X9_TEXT_ABSENT=1 skips it on a machine that has the text.
///
/// What the place checks prove, and what they do not:
/// - A key passes when its four 32-bit words appear in order, as one run, inside its own transaction's section.
///   A 128-bit value cannot appear by chance, so a match proves the value is published for that transaction.
///   It does not prove which label in the section it sits under.
/// - A PIN block passes when the first three words follow "Encrypted PIN Block:" in its own transaction's section,
///   and the fourth is the last word of the next line, where pdftotext puts the second column. It depends on the
///   pdftotext layout (poppler, default mode); another layout fails the test rather than passing it.
/// </summary>
public class X9VectorTests
{
    private static readonly Lazy<string> Text = new(() => File.ReadAllText(X9Text.Path));
    private static readonly Regex HexWord = new(@"\b[0-9A-F]{8}\b");

    [Fact]
    public void A1_the_reader_and_the_bank_derive_the_committed_keys_and_pin_blocks()
    {
        var setup = X9VectorFile.Group("setup");
        var inputs = X9VectorFile.Group("pin-block-inputs");
        var keyId = setup["initial-key-id"];
        var bdk = Convert.FromHexString(setup["bdk"]);

        Assert.True(setup["bdk"] == TrainingKeys.BaseDerivationKeyHex, "the bank's training BDK");
        Assert.True(setup["bdk"] == ReferenceDukpt.X9TestBdkHex, "the A2 reference BDK");
        var bank = BankKeys.Training();
        var initialKey = bank.InitialKey(keyId); // a bank injects one key ID once
        Assert.Equal(setup["initial-key"], Convert.ToHexString(initialKey));

        var transactions = X9VectorFile.Transactions().ToList();
        Assert.NotEmpty(transactions);
        foreach (var (counter, values) in transactions)
        {
            var ksn = values["ksn"];
            Assert.Equal(keyId + counter.ToString("X8"), ksn);
            Assert.True(values["pin-key"] == Convert.ToHexString(bank.PinKey(ksn)), $"bank PIN key for counter {counter:X}");
            Assert.True(values["data-key"] == Convert.ToHexString(bank.DataKey(ksn)), $"bank data key for counter {counter:X}");
            var ksnBytes = Convert.FromHexString(ksn);
            Assert.True(values["pin-key"] == Convert.ToHexString(ReferenceDukpt.WorkingKey(bdk, ksnBytes, ReferenceDukpt.PinEncryption)), $"A2 reference PIN key for counter {counter:X}");
            Assert.True(values["data-key"] == Convert.ToHexString(ReferenceDukpt.WorkingKey(bdk, ksnBytes, ReferenceDukpt.DataEncrypt)), $"A2 reference data key for counter {counter:X}");
        }

        // The reader's counter starts at 1, so the PIN blocks are its first reads, in order.
        var withBlocks = transactions.Where(t => t.Values.ContainsKey("pin-block")).ToList();
        Assert.Equal(Enumerable.Range(1, withBlocks.Count).Select(i => (uint)i), withBlocks.Select(t => t.Counter));
        var reader = new CardReader(initialKey, keyId, _ => Convert.FromHexString(inputs["random"]));
        var card = new CardData(inputs["pan"], 12, 2030, CardKind.Debit);
        foreach (var (counter, values) in withBlocks)
        {
            var read = reader.Read(card, Tender.Debit, inputs["pin"]);
            Assert.Equal(values["ksn"], read.Ksn);
            Assert.True(values["pin-block"] == read.PinBlock, $"reader PIN block {read.PinBlock} for counter {counter}");
        }
    }

    [X9TextFact]
    public void A1_every_committed_value_equals_the_fetched_pdf_text_in_its_place()
    {
        Assert.True(HexWord.Matches(Text.Value).Count > 1000, "too few hex words read; is the text the X9.24-3 supplement?");

        // The position check must refuse a block from another transaction before it is trusted.
        var first = X9VectorFile.Group("transaction 1")["pin-block"];
        Assert.True(PinBlockInPlace(Section(1), first));
        Assert.False(PinBlockInPlace(Section(2), first));

        var setup = X9VectorFile.Group("setup");
        Assert.True(Spaced(Text.Value).Contains("BDK-128: " + HexWords(setup["bdk"])), "bdk");
        Assert.True(Spaced(Text.Value).Contains("InitialKeyID: " + HexWords(setup["initial-key-id"])), "initial-key-id");
        Assert.Equal(["bdk", "initial-key", "initial-key-id"], setup.Keys.Order(StringComparer.Ordinal));
        Assert.All(X9VectorFile.Groups.Value.Keys, g => Assert.True(g is "setup" or "pin-block-inputs" || g.StartsWith("transaction ", StringComparison.Ordinal), $"unknown group [{g}]"));

        var inputs = X9VectorFile.Group("pin-block-inputs");
        Assert.Equal(["pan", "pin", "random"], inputs.Keys.Order(StringComparer.Ordinal));
        var inputLine = $"PAN = {inputs["pan"]} PIN = {inputs["pin"]} Random Number = {inputs["random"]}";

        foreach (var (counter, values) in X9VectorFile.Transactions())
        {
            var section = Section(counter);
            Assert.True(OneRun(section, setup["initial-key"]), $"initial-key in transaction {counter}");
            foreach (var (name, value) in values)
            {
                var inPlace = name switch
                {
                    "ksn" => KsnInPlace(section, counter, value, setup["initial-key-id"]),
                    "pin-key" or "data-key" => OneRun(section, value),
                    "pin-block" => PinBlockInPlace(section, value) && Spaced(section).Contains(inputLine),
                    _ => false,
                };
                Assert.True(inPlace, $"{name} = {value} of transaction {counter} is not in its place in the PDF text");
            }
        }
    }

    /// <summary>The text of "All Key Usages for Transaction {counter}", up to the next one or to section 2.3.</summary>
    private static string Section(uint counter)
    {
        var start = Regex.Match(Text.Value, $@"All Key Usages for Transaction {counter}\b");
        Assert.True(start.Success, $"no section for transaction {counter}");
        var next = Regex.Match(Text.Value[(start.Index + start.Length)..], @"All Key Usages for Transaction|2\.3 Full internal state");
        return next.Success ? Text.Value.Substring(start.Index, start.Length + next.Index) : Text.Value[start.Index..];
    }

    private static string Spaced(string s) => Regex.Replace(s, @"\s+", " ");

    private static string HexWords(string hex) => string.Join(' ', WordsOf(hex));

    private static string[] WordsOf(string hex) => hex.Chunk(8).Select(c => new string(c)).ToArray();

    private static bool OneRun(string section, string hex)
    {
        var run = WordsOf(hex);
        var words = HexWord.Matches(section).Select(m => m.Value).ToArray();
        for (int i = 0; i + run.Length <= words.Length; i++)
            if (run.Select((w, k) => words[i + k] == w).All(x => x)) return true;
        return false;
    }

    /// <summary>The KSN is the published InitialKeyID and the counter that the section prints, in decimal and hex.</summary>
    private static bool KsnInPlace(string section, uint counter, string ksn, string keyId) =>
        ksn == keyId + counter.ToString("X8")
        && section.Contains($"Counter: {counter}")
        && section.Contains($"( 0x{counter:x} )")
        && Spaced(section).Contains($"{keyId[8..]} {counter:X8}");

    private static bool PinBlockInPlace(string section, string pinBlockHex)
    {
        var w = WordsOf(pinBlockHex);
        return Regex.IsMatch(section, $@"Encrypted PIN Block: {w[0]} {w[1]} {w[2]}\s+[0-9A-F]{{8}} [0-9A-F]{{8}} {w[3]}\b");
    }
}
