namespace GasPump.Core;

/// <summary>Whole cents. Never a double. Rule 4 from the research note.</summary>
public readonly record struct Money(long Cents) : IComparable<Money>
{
    public static readonly Money Zero = new(0);
    public static Money FromDollars(decimal dollars) => new((long)Math.Round(dollars * 100m, MidpointRounding.AwayFromZero));
    public static Money operator +(Money a, Money b) => new(a.Cents + b.Cents);
    public static Money operator -(Money a, Money b) => new(a.Cents - b.Cents);
    public static bool operator <(Money a, Money b) => a.Cents < b.Cents;
    public static bool operator >(Money a, Money b) => a.Cents > b.Cents;
    public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;
    public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;
    public static Money Min(Money a, Money b) => a <= b ? a : b;
    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);
    public override string ToString() => $"${Cents / 100}.{Cents % 100:00}";
}

/// <summary>Thousandths of a gallon. A pulser click is one of these.</summary>
public readonly record struct Volume(long MilliGallons) : IComparable<Volume>
{
    public static readonly Volume Zero = new(0);
    public static Volume FromGallons(decimal gallons) => new((long)Math.Round(gallons * 1000m, MidpointRounding.AwayFromZero));
    public decimal Gallons => MilliGallons / 1000m;
    public static Volume operator +(Volume a, Volume b) => new(a.MilliGallons + b.MilliGallons);
    public static Volume operator -(Volume a, Volume b) => new(a.MilliGallons - b.MilliGallons);
    public static bool operator <(Volume a, Volume b) => a.MilliGallons < b.MilliGallons;
    public static bool operator >(Volume a, Volume b) => a.MilliGallons > b.MilliGallons;
    public static bool operator <=(Volume a, Volume b) => a.MilliGallons <= b.MilliGallons;
    public static bool operator >=(Volume a, Volume b) => a.MilliGallons >= b.MilliGallons;
    public static Volume Min(Volume a, Volume b) => a <= b ? a : b;
    public int CompareTo(Volume other) => MilliGallons.CompareTo(other.MilliGallons);
    public override string ToString() => $"{Gallons:0.000} gal";
}

/// <summary>Milli-gallons per second. 10 gal/min is about 167 of these.</summary>
public readonly record struct FlowRate(long MilliGallonsPerSecond)
{
    public static readonly FlowRate Zero = new(0);
    public static FlowRate GallonsPerMinute(decimal gpm) => new((long)Math.Round(gpm * 1000m / 60m, MidpointRounding.AwayFromZero));
    public decimal GallonsPerMinuteValue => MilliGallonsPerSecond * 60m / 1000m;
    public Volume Over(TimeSpan dt) => new((long)Math.Round(MilliGallonsPerSecond * dt.TotalSeconds));
    public FlowRate Scale(double fraction) => new((long)Math.Round(MilliGallonsPerSecond * fraction));
}

public enum Grade { Regular = 87, Plus = 89, Premium = 93 }

/// <summary>
/// What the pump asks when it prices a slice: how much is this volume of this
/// grade, and how much volume fits under this money. The posted price board is
/// one answer. A schedule that wraps it (an additive surcharge, a loyalty
/// discount) is another, and the controller cannot tell them apart. Both
/// methods must agree with each other: MaxVolumeFor(g, m) is the largest v with
/// CentsFor(g, v) at or under m.
/// </summary>
public interface IPriceSchedule
{
    Money CentsFor(Grade grade, Volume volume);
    Volume MaxVolumeFor(Grade grade, Money money);
    string Display(Grade grade);

    /// <summary>
    /// The same money as <see cref="CentsFor"/>, broken into the lines a receipt
    /// and a settlement carry. The sum of the lines is the money, always. The
    /// board answers with one fuel line; a schedule that wraps it adds its own.
    /// </summary>
    IReadOnlyList<ChargeLine> Lines(Grade grade, Volume volume) =>
        [new ChargeLine(ChargeLine.Fuel, $"{grade} {volume} at {Display(grade)}/gal", CentsFor(grade, volume))];
}

/// <summary>
/// One line of a sale's charges: fuel, a fee, a discount. Code is stable and
/// machine-readable; Description is for the receipt. A discount is a negative
/// amount. The bank receives the list with the completion and the driver can
/// print it, so the two always agree with each other and with the total.
/// </summary>
public sealed record ChargeLine(string Code, string Description, Money Amount)
{
    public const string Fuel = "FUEL";

    public static Money Total(IEnumerable<ChargeLine> lines)
    {
        var total = Money.Zero;
        foreach (var l in lines) total += l.Amount;
        return total;
    }
}

/// <summary>
/// Price in tenths of a cent per gallon, so $3.499 is 3499. Converts volume to
/// money with integer math and rounds half up, the same way everywhere, so the
/// ceiling check and the display agree.
/// </summary>
public sealed record Pricing(IReadOnlyDictionary<Grade, long> TenthCentsPerGallon) : IPriceSchedule
{
    public static Pricing Default => new(new Dictionary<Grade, long>
    {
        [Grade.Regular] = 3499,
        [Grade.Plus] = 3799,
        [Grade.Premium] = 4099,
    });

    public long PriceOf(Grade grade) => TenthCentsPerGallon[grade];

    public Money CentsFor(Grade grade, Volume volume)
    {
        // cents = mGal * tenthCents / gal / 10 / 1000, rounded half up.
        long numerator = volume.MilliGallons * PriceOf(grade);
        return new Money((numerator + 5_000) / 10_000);
    }

    /// <summary>The largest volume whose price does not exceed the money. Used for the last tick.</summary>
    public Volume MaxVolumeFor(Grade grade, Money money)
    {
        long price = PriceOf(grade);
        // Largest v with (v*price + 5000) / 10000 <= cents, so v*price + 5000 < (cents+1)*10000.
        long v = ((money.Cents + 1) * 10_000 - 5_000 - 1) / price;
        if (v < 0) v = 0;
        // Guard rounding at the edge.
        while (v > 0 && CentsFor(grade, new Volume(v)) > money) v--;
        return new Volume(v);
    }

    public string Display(Grade grade) => $"${PriceOf(grade) / 1000}.{PriceOf(grade) % 1000:000}";
}
