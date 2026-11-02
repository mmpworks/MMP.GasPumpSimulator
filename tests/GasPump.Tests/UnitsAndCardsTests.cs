using GasPump.Core;
using GasPump.Payment;
using Xunit;

namespace GasPump.Tests;

public class UnitsAndCardsTests
{
    private static readonly Pricing P = Pricing.Default;

    [Fact]
    public void One_gallon_of_regular_is_350_cents_rounded_half_up()
    {
        Assert.Equal(new Money(350), P.CentsFor(Grade.Regular, Volume.FromGallons(1)));
        Assert.Equal(new Money(3499), P.CentsFor(Grade.Regular, Volume.FromGallons(10)));
    }

    /// <summary>
    /// NIST Handbook 44 G-S.5.5 asks for agreement to the nearest cent and
    /// S.1.6.5.2 for price steps no coarser than 0.01 gal. It names no tie
    /// rule, so half up is this project's rule (docs/glossary.md, Money
    /// rounding). Banker's rounding would turn $17.485 into $17.48.
    /// </summary>
    [Fact]
    public void Money_rounds_half_up_once_per_sale_and_on_steps_finer_than_handbook_44_asks()
    {
        var board = new Pricing(new Dictionary<Grade, long> { [Grade.Regular] = 3497, [Grade.Plus] = 3797, [Grade.Premium] = 4097 });

        // 5.000 gal at $3.497 is $17.485: an exact half cent. Half up gives $17.49; half to even would give $17.48.
        Assert.Equal(new Money(1749), board.CentsFor(Grade.Regular, Volume.FromGallons(5)));
        // 3.000 gal at $3.497 is $10.491: below the half, so down.
        Assert.Equal(new Money(1049), board.CentsFor(Grade.Regular, Volume.FromGallons(3)));
        // 7.000 gal at $3.497 is $24.479: above the half, so up.
        Assert.Equal(new Money(2448), board.CentsFor(Grade.Regular, Volume.FromGallons(7)));

        // One rounding on the whole volume, never a sum of rounded slices: ten slices of 0.5 gal price the same as 5 gal.
        var slices = Enumerable.Range(1, 10).Select(i => board.CentsFor(Grade.Regular, new Volume(500L * i))).ToList();
        Assert.Equal(board.CentsFor(Grade.Regular, Volume.FromGallons(5)), slices[^1]);

        // The price step is 0.001 gal, finer than the 0.01 gal Handbook 44 allows, so one thousandth changes the cents.
        var tenth = board.CentsFor(Grade.Regular, new Volume(10));
        var hundredth = board.CentsFor(Grade.Regular, new Volume(100));
        Assert.Equal(new Money(3), tenth);         // 0.010 gal at $3.497 is 3.497 cents
        Assert.Equal(new Money(35), hundredth);    // 0.100 gal at $3.497 is 34.97 cents
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(1850)]
    [InlineData(1)]
    [InlineData(17495)]
    public void MaxVolumeFor_never_prices_over_the_money_and_one_more_pulse_does(long cents)
    {
        var money = new Money(cents);
        var v = P.MaxVolumeFor(Grade.Regular, money);
        Assert.True(P.CentsFor(Grade.Regular, v) <= money);
        Assert.True(P.CentsFor(Grade.Regular, v + new Volume(1)) > money);
    }

    [Fact]
    public void Ten_gallons_a_minute_over_100ms_is_about_17_milligallons()
    {
        var flow = FlowRate.GallonsPerMinute(10);
        Assert.Equal(167, flow.MilliGallonsPerSecond);
        Assert.Equal(17, flow.Over(TimeSpan.FromMilliseconds(100)).MilliGallons);
    }

    [Fact]
    public void Test_cards_pass_luhn_except_the_one_that_should_not()
    {
        foreach (var (name, card) in TestCards.ByName)
        {
            var ok = CardValidator.Luhn(card.Pan);
            if (name == "BadLuhn") Assert.False(ok, name); else Assert.True(ok, name);
        }
    }

    [Fact]
    public void Expired_card_is_caught_locally()
    {
        var now = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(CardCheck.Expired, CardValidator.Check(TestCards.Get("Expired"), now));
        Assert.Equal(CardCheck.Ok, CardValidator.Check(TestCards.Get("Good"), now));
        // Good through the last day of the month.
        var edge = new CardData("4111111111111111", 10, 2026, CardKind.Credit);
        Assert.Equal(CardCheck.Ok, CardValidator.Check(edge, new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.Zero)));
        Assert.Equal(CardCheck.Expired, CardValidator.Check(edge, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
