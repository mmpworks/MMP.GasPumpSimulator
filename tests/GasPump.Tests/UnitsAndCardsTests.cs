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
