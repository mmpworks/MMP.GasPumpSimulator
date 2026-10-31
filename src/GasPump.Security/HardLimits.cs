using GasPump.Core;

namespace GasPump.Security;

/// <summary>
/// The numbers the pump will never exceed, whatever anyone upstream says.
/// Set once at construction, from the pump's own configuration, never from a
/// message. This is the 600-gallon defense: a token signed with the right key
/// for 600 gallons gets clamped to MaxVolumePerSale before the valve opens.
/// </summary>
public sealed record HardLimits(
    Money MaxMoneyPerSale,
    Volume MaxVolumePerSale,
    TimeSpan MaxSaleDuration,
    FlowRate MaxFlow,
    long MaxPulsesPerTick)
{
    public static HardLimits Default => new(
        MaxMoneyPerSale: Money.FromDollars(500),
        MaxVolumePerSale: Volume.FromGallons(50),
        MaxSaleDuration: TimeSpan.FromMinutes(10),
        MaxFlow: FlowRate.GallonsPerMinute(12),
        // A tick is 100 ms. 12 gal/min is 20 mGal per tick. Anything far above that is a spoofed pulser.
        MaxPulsesPerTick: 60);
}
