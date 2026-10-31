using GasPump.Core;

namespace GasPump.Pump;

/// <summary>
/// How far the valve opens as the sale closes in on its ceiling. Full flow
/// until the last 20 percent of the money is left, then the opening falls in a
/// straight line with the remaining money, down to a floor so the last few
/// cents still move. The controller does the final stop on the cent.
///
/// With a $30 ceiling: full to $24, tapering from $24 to $30, creeping at 8
/// percent for the last few cents. Change the two numbers to change the feel.
/// </summary>
public sealed record TaperPolicy(double TaperStartFraction, double MinimumOpening)
{
    public static TaperPolicy Default => new(TaperStartFraction: 0.20, MinimumOpening: 0.08);

    /// <summary>Returns the valve opening in [0, 1] for the money still available under the ceiling.</summary>
    public double Opening(Money remaining, Money ceiling)
    {
        if (remaining <= Money.Zero || ceiling <= Money.Zero) return 0;
        var taperWindow = (long)Math.Ceiling(ceiling.Cents * TaperStartFraction);
        if (taperWindow <= 0) return 1;
        if (remaining.Cents >= taperWindow) return 1;
        var fraction = (double)remaining.Cents / taperWindow;
        return Math.Max(MinimumOpening, fraction);
    }

    public string PhaseFor(Money remaining, Money ceiling)
    {
        var opening = Opening(remaining, ceiling);
        if (opening >= 1) return "full";
        if (opening <= MinimumOpening) return "creep";
        return "taper";
    }
}
