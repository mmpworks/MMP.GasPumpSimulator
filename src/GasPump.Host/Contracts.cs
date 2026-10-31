using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Security;

namespace GasPump.Host;

/// <summary>The JSON the UI sees. One shape for the REST snapshot and the socket push.</summary>
public static class Contracts
{
    public static object State(Simulation sim)
    {
        var s = sim.Snapshot();
        return new
        {
            type = "state",
            strategy = sim.StrategyName,
            pumpId = sim.Options.PumpId,
            sale = Sale(s),
            pump = new
            {
                phase = sim.Pump.Phase.ToString(),
                armed = sim.Pump.Armed is { } a ? new { a.SaleId, ceilingCents = a.Ceiling.Cents, maxGallons = a.MaxVolume.Gallons, a.Clamped, a.ClampNote } : null,
                fault = sim.Pump.LastFault,
                totalizerGallons = sim.Totalizer.Total.Gallons,
            },
            network = new { mode = sim.Network.Mode.ToString(), latencyMs = sim.Network.Latency.TotalMilliseconds, flapFailureRate = sim.Network.FlapFailureRate },
            pendingCompletions = sim.Gateway.PendingCompletions.Select(p => new { p.SaleId, cents = p.Final.Cents }),
            limits = new
            {
                maxCents = sim.Options.HardLimits.MaxMoneyPerSale.Cents,
                maxGallons = sim.Options.HardLimits.MaxVolumePerSale.Gallons,
                maxMinutes = sim.Options.HardLimits.MaxSaleDuration.TotalMinutes,
                maxGpm = sim.Options.HardLimits.MaxFlow.GallonsPerMinuteValue,
            },
            prices = Enum.GetValues<Grade>().ToDictionary(g => g.ToString(), g => sim.Options.Pricing.Display(g)),
            ledgerOk = sim.Ledger.Verify() == 0,
        };
    }

    public static object Sale(SaleSnapshot s) => new
    {
        type = "sale",
        s.SaleId,
        state = s.State.ToString(),
        s.CardName,
        grade = s.Grade.ToString(),
        ceilingCents = s.Ceiling.Cents,
        cents = s.Money.Cents,
        gallons = s.Volume.Gallons,
        gpm = s.Flow.GallonsPerMinuteValue,
        s.Phase,
        s.LastMessage,
    };

    public static object Log(LogLine l) => new { type = "log", at = l.At, l.Source, l.Message };

    public static object Ledger(LedgerEntry e) => new { type = "ledger", e.Seq, e.At, e.Kind, e.Detail, hash = e.Hash[..12], prev = e.PrevHash[..12] };
}
