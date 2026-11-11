using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Reader;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The card reader attacks of docs/prd-dukpt-reader.md acceptance item 3, one control each. Every test here
/// fails when its control is removed; the revert checks are in docs/threat-model.md (attacks 47 to 53).
/// </summary>
public class ReaderAttackTests
{
    private static readonly DateTimeOffset Now = SimHarness.Start;
    private static readonly CardData Debit = TestCards.Get("DebitGoodPin");
    private static readonly CardData Credit = TestCards.Get("Good");

    private static (IssuerSimulator Bank, CardReader Reader) Rig()
    {
        var bank = IssuerSimulator.WithTestCards();
        return (bank, ReaderTestPaths.NewReader(bank.Keys));
    }

    /// <summary>One hex digit of a field changed, so the field stays valid hex of the same length.</summary>
    private static string FlipOneByte(string hex, int byteIndex)
    {
        var bytes = Convert.FromHexString(hex);
        bytes[byteIndex] ^= 0x01;
        return Convert.ToHexString(bytes);
    }

    // ----- (a) replay

    [Fact]
    public void Attack_a_a_recorded_pin_block_sent_again_with_its_ksn_is_refused_as_a_replay()
    {
        var (bank, reader) = Rig();
        var recorded = reader.Read(Debit, Tender.Debit, TestCards.Pin);
        Assert.True(bank.Authorize("S1", recorded, Money.FromDollars(50), Now).Approved);

        var replay = bank.Authorize("S2", recorded, Money.FromDollars(50), Now);

        Assert.False(replay.Approved);
        Assert.Equal(DeclineReason.KeyReplayed, replay.Reason);
        Assert.Single(bank.OpenHolds);
    }

    [Fact]
    public void Attack_a_a_replay_after_the_bank_restarts_from_its_books_is_still_refused()
    {
        var (bank, reader) = Rig();
        var recorded = reader.Read(Credit, Tender.Credit, TestCards.Zip);
        Assert.True(bank.Authorize("S1", recorded, Money.FromDollars(50), Now).Approved);

        var restarted = new IssuerSimulator(GasPump.Bank.BankKeys.Training());
        restarted.Import(bank.Export());

        Assert.Equal(DeclineReason.KeyReplayed, restarted.Authorize("S2", recorded, Money.FromDollars(50), Now).Reason);
    }

    [Fact]
    public void Attack_a_a_forged_message_under_a_seen_ksn_fails_the_tag_first_so_the_answer_reveals_nothing_about_the_ledger()
    {
        var (bank, reader) = Rig();
        var recorded = reader.Read(Credit, Tender.Credit, TestCards.Zip);
        Assert.True(bank.Authorize("S1", recorded, Money.FromDollars(50), Now).Approved);

        var forged = recorded with { SealedPan = FlipOneByte(recorded.SealedPan, 0) };

        Assert.Equal(DeclineReason.CardDataUnreadable, bank.Authorize("S2", forged, Money.FromDollars(50), Now).Reason);
    }

    [Fact]
    public void Attack_a_an_older_counter_the_bank_never_saw_is_refused_below_the_high_water_mark()
    {
        var (bank, reader) = Rig();
        var older = reader.Read(Credit, Tender.Credit, TestCards.Zip);
        var newer = reader.Read(Credit, Tender.Credit, TestCards.Zip);
        Assert.True(bank.Authorize("S2", newer, Money.FromDollars(10), Now).Approved);

        var late = bank.Authorize("S1", older, Money.FromDollars(10), Now);

        Assert.Equal(DeclineReason.KeyReplayed, late.Reason);
    }

    [Fact]
    public void A_gap_ahead_in_the_counter_is_accepted_because_a_sale_that_never_reached_the_bank_leaves_one()
    {
        var (bank, reader) = Rig();
        var first = reader.Read(Credit, Tender.Credit, TestCards.Zip);
        reader.Read(Credit, Tender.Credit, TestCards.Zip);   // read, never sent
        var third = reader.Read(Credit, Tender.Credit, TestCards.Zip);

        Assert.True(bank.Authorize("S1", first, Money.FromDollars(10), Now).Approved);
        Assert.True(bank.Authorize("S3", third, Money.FromDollars(10), Now).Approved);
    }

    [Fact]
    public void The_bank_refuses_to_inject_one_initial_key_id_twice_even_after_a_restart()
    {
        var bank = IssuerSimulator.WithTestCards();
        var id = GasPump.Bank.BankKeys.NewInitialKeyId(1);
        bank.Keys.InitialKey(id);

        var again = Assert.Throws<InvalidOperationException>(() => bank.Keys.InitialKey(id));
        Assert.Contains(GasPump.Bank.BankKeys.DuplicateKeyIdCode, again.Message);

        var restarted = new IssuerSimulator(GasPump.Bank.BankKeys.Training());
        restarted.Import(bank.Export());
        Assert.Throws<InvalidOperationException>(() => restarted.Keys.InitialKey(id));
    }

    // ----- (b) a changed PIN block

    [Fact]
    public void Attack_b_one_changed_byte_in_the_pin_block_is_refused_at_the_bank()
    {
        var (bank, reader) = Rig();
        var card = reader.Read(Debit, Tender.Debit, TestCards.Pin);
        for (int i = 0; i < 16; i++)
        {
            var changed = card with { PinBlock = FlipOneByte(card.PinBlock!, i) };
            var decision = bank.Authorize($"S{i}", changed, Money.FromDollars(50), Now);
            Assert.False(decision.Approved);
            Assert.Equal(DeclineReason.CardDataUnreadable, decision.Reason);
        }
        // The refusals burned nothing: the real message still opens.
        Assert.True(bank.Authorize("REAL", card, Money.FromDollars(50), Now).Approved);
    }

    // ----- (c) a changed card number, or a changed clear field

    [Fact]
    public void Attack_c_one_changed_byte_in_the_sealed_card_number_is_refused_at_the_bank()
    {
        var (bank, reader) = Rig();
        var card = reader.Read(Credit, Tender.Credit, TestCards.Zip);
        var length = card.SealedPan.Length / 2;
        for (int i = 0; i < length; i++)
        {
            var decision = bank.Authorize($"S{i}", card with { SealedPan = FlipOneByte(card.SealedPan, i) }, Money.FromDollars(50), Now);
            Assert.Equal(DeclineReason.CardDataUnreadable, decision.Reason);
        }
        Assert.True(bank.Authorize("REAL", card, Money.FromDollars(50), Now).Approved);
    }

    [Fact]
    public void Attack_c_a_changed_clear_field_is_refused_because_the_tag_covers_it()
    {
        var (bank, reader) = Rig();
        var card = reader.Read(Credit, Tender.Credit, "99999");   // a wrong ZIP, which the attacker "fixes" in transit
        var fixedZip = bank.Authorize("S1", card with { Zip = TestCards.Zip }, Money.FromDollars(50), Now);
        Assert.Equal(DeclineReason.CardDataUnreadable, fixedZip.Reason);
        var fixedExpiry = bank.Authorize("S2", card with { ExpiryYear = 2099 }, Money.FromDollars(50), Now);
        Assert.Equal(DeclineReason.CardDataUnreadable, fixedExpiry.Reason);
    }

    // ----- (d) a reader the bank never injected

    [Fact]
    public void Attack_d_a_reader_injected_from_another_base_key_is_refused_at_the_bank()
    {
        var bank = IssuerSimulator.WithTestCards();
        var rogueFacility = new GasPump.Bank.BankKeys(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
        var rogue = ReaderTestPaths.NewReader(rogueFacility);   // same key set ID, other base key

        var decision = bank.Authorize("S1", rogue.Read(Debit, Tender.Debit, TestCards.Pin), Money.FromDollars(50), Now);

        Assert.False(decision.Approved);
        Assert.Equal(DeclineReason.CardDataUnreadable, decision.Reason);
        Assert.Empty(bank.OpenHolds);
    }

    // ----- (e) the reader's memory after a sale

    [Fact]
    public void Attack_e_the_keys_left_in_the_reader_after_a_sale_cannot_give_that_sale_key()
    {
        var bank = IssuerSimulator.WithTestCards();
        var id = GasPump.Bank.BankKeys.NewInitialKeyId(3);
        var injected = bank.Keys.InitialKey(id);
        var reader = new CardReader(injected, id);
        Assert.All(injected, b => Assert.Equal(0, b));   // the reader erased the injected key it was handed
        reader.Read(Debit, Tender.Debit, TestCards.Pin);
        var sale = reader.Read(Debit, Tender.Debit, TestCards.Pin);

        var memory = reader.ReadMemory(sale.Counter);

        Assert.True(memory.HeldKeyRegisters > 0);
        Assert.False(memory.SaleKeyDerivable);
        Assert.True(memory.NextKeyDerivable);       // the probe works: the registers do give a later key
    }

    // ----- (f) scrape the terminal

    private const string ScrapePin = "8642";

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void Attack_f_after_a_credit_sale_and_a_debit_sale_no_terminal_artifact_holds_a_card_number_or_the_pin(string strategy)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gaspump-scrape-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var bank = IssuerSimulator.WithTestCards();
            bank.Seed(Debit.Pan, Money.FromDollars(900), pin: ScrapePin);
            var events = new List<string>();
            var views = new List<string>();
            using (var h = new SimHarness(strategy, SimulationOptions.Default with
                   {
                       HoldAmount = Money.FromDollars(10),
                       DebitHold = Money.FromDollars(10),
                       Journal = new JournalOptions(dir, "PUMP-07", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), "RUN-SCRAPE"),
                   }, bank))
            {
                h.Sim.Capabilities.Register(new EventRecorder(events));

                h.Present("Good");
                RunSale(h, views);
                h.Present("DebitGoodPin", tender: Tender.Debit, code: ScrapePin);
                RunSale(h, views);
                Assert.Equal(2, bank.Settlements.Count);

                // Every reader attack the app can run, and every refusal message and exception text it produced.
                var attackResults = new List<string>();
                foreach (var attack in ReaderLab.Attacks)
                {
                    var result = ReaderLab.Run(attack, h.Point(1), bank, Money.FromDollars(10), h.Clock.GetUtcNow());
                    Assert.True(result.Defended, $"{attack}: {result.Code} {result.Message}");
                    attackResults.Add(JsonSerializer.Serialize(result));
                }
                var duplicate = Assert.Throws<InvalidOperationException>(() => bank.Keys.InitialKey(h.Point(1).Reader.InitialKeyId));
                attackResults.Add(duplicate.ToString());

                h.Sim.SaveJournal();
                var artifacts = new Dictionary<string, string>
                {
                    ["log"] = JsonSerializer.Serialize(h.Sim.Log.Snapshot()),
                    ["ledger"] = JsonSerializer.Serialize(h.Sim.Ledger.Snapshot()),
                    ["sale events seen by capabilities"] = string.Join("\n", events),
                    ["pump screens and receipts"] = JsonSerializer.Serialize(h.Snapshots),
                    ["terminal view and reader status"] = string.Join("\n", views),
                    ["bank view on the reader panel"] = JsonSerializer.Serialize(bank.LastOpened),
                    ["reader attack results and exception text"] = string.Join('\n', attackResults),
                };
                foreach (var file in Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != "bank.json"))
                    artifacts[Path.GetFileName(file)] = ReadShared(file);

                Assert.Contains("state.json", artifacts.Keys);
                foreach (var (name, text) in artifacts)
                {
                    Assert.False(text.Contains(Credit.Pan), $"{name} holds the credit card number");
                    Assert.False(text.Contains(Debit.Pan), $"{name} holds the debit card number");
                    Assert.False(HoldsPin(text), $"{name} holds the PIN");
                }
            }

            // The scan can see a card number when one is there: the bank's own books hold both.
            var books = File.ReadAllText(Path.Combine(dir, "bank.json"));
            Assert.Contains(Credit.Pan, books);
            Assert.Contains(Debit.Pan, books);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    /// <summary>The PIN as a token of its own: not part of a longer run of hex digits, such as a KSN or a ciphertext.</summary>
    private static bool HoldsPin(string text) => Regex.IsMatch(text, $"(?<![0-9A-Fa-f]){ScrapePin}(?![0-9A-Fa-f])");

    private static void RunSale(SimHarness h, List<string> views)
    {
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        views.Add(JsonSerializer.Serialize(h.Point(1).LastPresented));
        views.Add(JsonSerializer.Serialize(h.Point(1).Reader.Status));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromMinutes(3));
        h.Settle();
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    /// <summary>A telemetry plug-in: every sale event, payload and all, as JSON.</summary>
    private sealed class EventRecorder(List<string> events) : IStationCapability
    {
        public string Name => "scrape";
        public void OnSaleEvent(SaleEvent e) { lock (events) events.Add(JsonSerializer.Serialize(e, e.GetType())); }
    }

    // ----- the boundary itself

    [Fact]
    public void Only_the_reader_and_the_bank_reference_the_security_library()
    {
        string[] terminal = ["GasPump.Core", "GasPump.Payment", "GasPump.Security", "GasPump.Pump", "GasPump.Custody", "GasPump.Dispatch"];
        foreach (var name in terminal)
        {
            var refs = Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name);
            Assert.DoesNotContain("NetSec.Core", refs);
        }
        Assert.Contains("NetSec.Core", typeof(CardReader).Assembly.GetReferencedAssemblies().Select(a => a.Name));
        Assert.Contains("NetSec.Core", typeof(GasPump.Bank.BankKeys).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }

    [Theory]
    [MemberData(nameof(SimHarness.Strategies), MemberType = typeof(SimHarness))]
    public void A_sale_that_ends_before_the_card_is_read_leaves_no_keypad_digits_in_the_reader(string strategy)
    {
        using var h = new SimHarness(strategy);
        h.Present("DebitGoodPin", tender: Tender.Debit, code: "12");   // the wrong shape: the machine waits, the reader holds the digits
        h.Settle();
        Assert.Equal(SaleState.CardRead, h.State.State);
        Assert.Equal(1, h.Point(1).Reader.KeypadEntriesHeld);

        h.AdvanceUntilState(SaleState.Cancelled, TimeSpan.FromMinutes(5));   // the keypad entry times out

        Assert.Equal(0, h.Point(1).Reader.KeypadEntriesHeld);
    }

    [Fact]
    public void A_keypad_entry_for_a_new_sale_drops_the_one_left_from_the_last()
    {
        var (_, reader) = Rig();
        reader.Keypad("S1", TestCards.Pin);
        reader.Keypad("S2", TestCards.Pin);
        Assert.Equal(1, reader.KeypadEntriesHeld);
        reader.Forget("S2");
        Assert.Equal(0, reader.KeypadEntriesHeld);
    }

    [Fact]
    public void The_keypad_entry_reaches_the_sale_machine_as_one_star_per_digit()
    {
        var (_, reader) = Rig();
        Assert.Equal("****", reader.Keypad("S1", TestCards.Pin));
        Assert.Equal("**a*", reader.Keypad("S2", "12a4"));
    }
}
