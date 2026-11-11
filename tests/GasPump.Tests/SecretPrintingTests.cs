using GasPump.Core;
using GasPump.Payment;
using Xunit;

namespace GasPump.Tests;

/// <summary>A record's own ToString prints every member. Each record that holds a card number, a PIN or key bytes prints masked values instead.</summary>
public class SecretPrintingTests
{
    private const string Pan = "6011111111111117";
    private const string Pin = "8642";

    private static void AssertMasked(object value)
    {
        var text = value.ToString()!;
        Assert.DoesNotContain(Pan, text);
        Assert.DoesNotContain(Pin, text);
    }

    [Fact]
    public void Every_record_that_holds_a_card_number_or_a_pin_prints_them_masked()
    {
        var card = new CardData(Pan, 12, 2030, CardKind.Debit, Pin, "12345");
        AssertMasked(card);
        Assert.Contains("601111******1117", card.ToString());

        AssertMasked(new CardOpening(DeclineReason.None, Pan, Pin));
        AssertMasked(new IssuerAccountState(Pan, 100, "Active", Pin, 3, [], "12345"));
        AssertMasked(new IssuerHoldState("S1", Pan, 100));

        var bank = IssuerSimulator.WithTestCards();
        bank.Seed(Pan, Money.FromDollars(900), pin: Pin);
        var reader = ReaderTestPaths.NewReader(bank.Keys);
        var sealedCard = reader.Read(card, Tender.Debit, Pin);
        AssertMasked(sealedCard);
        AssertMasked(reader.Status);
        AssertMasked(reader.ReadMemory(sealedCard.Counter));
        Assert.True(bank.Authorize("S1", sealedCard, Money.FromDollars(10), SimHarness.Start).Approved);
        AssertMasked(bank.LastOpened!);
        AssertMasked(bank.Export());
    }
}
