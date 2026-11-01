using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Security;

namespace GasPump.Host;

/// <summary>The JSON the UI sees. One shape for the REST snapshot and the socket push.</summary>
public static class Contracts
{
    public static object State(Simulation sim, SimulationHost? host = null, StationServer? station = null)
    {
        var s = sim.Snapshot();
        return new
        {
            type = "state",
            strategy = sim.StrategyName,
            pumpId = sim.Options.PumpId,
            station = new
            {
                estopActive = host?.EStopActive ?? false,
                estopAt = host?.EStopAt,
                custodyLinesPublished = sim.Custody?.LinesPublished ?? 0,
                custodyLinesReceived = station?.LinesReceived ?? 0,
                custodyCopyPath = station?.CopyPath,
            },
            sale = Sale(s),
            pump = new
            {
                phase = sim.Pump.Phase.ToString(),
                faultCode = sim.Pump.LastFaultCode,
                supplyAvailable = sim.Pump.SupplyAvailable,
                breakawayOpen = sim.Pump.BreakawayOpen,
                pulserDead = sim.Meter.PulserDead,
                nozzleLifted = sim.Pump.NozzleLifted,
                nozzleSeated = sim.Pump.NozzleSeated,
                triggerHeld = sim.Pump.TriggerHeld,
                selectedGrade = sim.Pump.SelectedGrade?.ToString(),
                poured = sim.Pump.Poured,
                armed = sim.Pump.Armed is { } a ? new { a.SaleId, ceilingCents = a.Ceiling.Cents, maxGallons = a.MaxVolume.Gallons, a.Clamped, a.ClampNote } : null,
                fault = sim.Pump.LastFault,
                totalizerGallons = sim.Totalizer.Total.Gallons,
            },
            network = new { mode = sim.Network.Mode.ToString(), latencyMs = sim.Network.Latency.TotalMilliseconds, lateResponseMs = sim.Network.LateResponse.TotalMilliseconds, flapFailureRate = sim.Network.FlapFailureRate },
            pendingCompletions = sim.Gateway.PendingCompletions.Select(p => new { p.SaleId, cents = p.Final.Cents }),
            pending = sim.Gateway.Pending.Select(p => new { p.SaleId, kind = p.Kind.ToString(), cents = p.Final.Cents }),
            openHolds = sim.Issuer.OpenHolds,
            site = new
            {
                creditHoldCents = sim.Site.CreditHold.Cents,
                debitHoldCents = sim.Site.DebitHold.Cents,
                failedPins = sim.Site.FailedPins,
                lockedUntil = sim.Site.LockedUntil,
                maxPinFailures = SiteLimits.MaxPinFailures,
            },
            cash = new
            {
                open = sim.Drawer.Open.Select(kv => new { saleId = kv.Key, cents = kv.Value.Cents }),
                changeDue = sim.Drawer.ChangeDue.Select(kv => new { saleId = kv.Key, cents = kv.Value.Cents }),
            },
            now = sim.Clock.GetUtcNow(),
            runId = sim.RunId,
            recovery = Recovery(sim.LastRecovery),
            custody = new { path = sim.CustodyPath, journalPath = sim.JournalPath },
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
        hold = s.Hold.ToString(),
        end = s.End.ToString(),
        notice = new { s.Notice.Headline, s.Notice.Detail, action = s.Notice.Action.ToString() },
        s.GradeSelected,
        s.NozzleOut,
        s.NozzleSeated,
        tender = s.Tender.ToString(),
        s.FaultCode,
        faultPart = s.FaultCode is { } fc ? DiagnosticCodes.Find(fc)?.Part : null,
        cashPrepaidCents = s.CashPrepaid.Cents,
        changeDueCents = s.ChangeDue.Cents,
        s.DeadlineAt,
    };

    public static object Recovery(RecoveryOutcome r) => new
    {
        kind = r.Kind.ToString(),
        previousRunId = r.PreviousRunId,
        r.SaleId,
        tender = r.Tender.ToString(),
        grade = r.Grade.ToString(),
        gallons = r.Volume.Gallons,
        cents = r.Money.Cents,
        unrecordedUpToGallons = r.UnrecordedUpTo.Gallons,
        snapshotAgeSeconds = r.SnapshotAge.TotalSeconds,
        r.UnderRecorded,
        r.Note,
    };

    public static object Log(LogLine l) => new
    {
        type = "log",
        at = l.At,
        l.Source,
        l.Message,
        severity = l.Severity.ToString(),
        props = l.Props.ToDictionary(p => p.Key, p => p.Value?.ToString()),
    };

    public static object Ledger(LedgerEntry e) => new { type = "ledger", e.Seq, e.At, e.Kind, e.Detail, hash = e.Hash[..12], prev = e.PrevHash[..12] };
}
