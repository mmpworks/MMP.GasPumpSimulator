using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Security;

namespace GasPump.Host;

/// <summary>The JSON the UI sees. One shape for the REST snapshot and the socket push.</summary>
public static class Contracts
{
    public static object State(Simulation sim, SimulationHost? host = null, StationServer? station = null, DispenserProcess? process = null)
    {
        var s = sim.Snapshot();
        var link = sim.Link;
        return new
        {
            type = "state",
            strategy = sim.StrategyName,
            pumpId = sim.Options.PumpId,
            station = new
            {
                pid = Environment.ProcessId,
                port = 5177,
                runId = sim.RunId,
                custodyPath = sim.CustodyPath,
                estopActive = host?.EStopActive ?? false,
                estopAt = host?.EStopAt,
                custodyLinesPublished = sim.DispenserCustodyLines,
                custodyLinesReceived = station?.LinesReceived ?? 0,
                custodyCopyPath = station?.CopyPath,
                custodyCopyRunId = station?.RunId,
            },
            dispenser = new
            {
                link = link.Kind,
                connected = link.Connected,
                pid = link.RemotePid,
                runId = link.RemoteRunId,
                custodyPath = link.RemoteCustodyPath,
                framesSent = link.FramesSent,
                framesReceived = link.FramesReceived,
                custodyLines = link.CustodyLines,
                port = process?.Port ?? sim.Options.Dispenser?.Port,
                processRunning = process?.Running ?? sim.DispenserIsLocal,
                external = process?.External ?? false,
                lastExit = process?.LastExit,
            },
            sale = Sale(s),
            // While the E-stop holds, the forecourt has no power: the pump is out of service whatever its last view said.
            pump = Pump(host?.EStopActive == true ? sim.View with { Service = EStopStatus } : sim.View),
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

    private static readonly ServiceStatus EStopStatus = new([DiagnosticCodes.StationEStop.Code]);

    /// <summary>The pump as the station knows it. Same shape whether the dispenser is a mailbox away or a socket away.</summary>
    public static object Pump(DispenserView v) => new
    {
        type = "pump",
        phase = v.Phase,
        faultCode = v.FaultCode,
        supplyAvailable = v.SupplyAvailable,
        breakawayOpen = v.BreakawayOpen,
        pulserDead = v.PulserDead,
        inService = v.Service.InService,
        serviceCodes = v.Service.Codes,
        serviceParts = v.Service.Codes.Select(c => DiagnosticCodes.Find(c)?.Part ?? c),
        nozzleLifted = v.NozzleLifted,
        nozzleSeated = v.NozzleSeated,
        triggerHeld = v.TriggerHeld,
        selectedGrade = v.SelectedGrade?.ToString(),
        poured = v.Poured,
        armed = v.Armed is { } a ? new { a.SaleId, ceilingCents = a.Ceiling.Cents, maxGallons = a.MaxVolume.Gallons, a.Clamped, a.ClampNote } : null,
        fault = v.Fault,
        totalizerGallons = new Volume(v.TotalizerMilliGallons).Gallons,
        ledgerIntact = v.LedgerIntact,
        runId = v.RunId,
        pid = v.Pid,
    };

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
        source = s.Source.ToString(),
        s.FaultCode,
        faultPart = s.FaultCode is { } fc ? DiagnosticCodes.Find(fc)?.Part : null,
        cashPrepaidCents = s.CashPrepaid.Cents,
        changeDueCents = s.ChangeDue.Cents,
        s.DeadlineAt,
        lines = s.Charges.Select(l => new { l.Code, l.Description, cents = l.Amount.Cents }).ToArray(),
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

    public static object Log(LogLine l, string origin) => new
    {
        type = "log",
        origin,
        at = l.At,
        l.Source,
        l.Message,
        severity = l.Severity.ToString(),
        props = l.Props.Where(p => p.Key != "Origin").ToDictionary(p => p.Key, p => p.Value?.ToString()),
    };

    public static object Ledger(LedgerEntry e, string origin) => new { type = "ledger", origin, e.Seq, e.At, e.Kind, e.Detail, hash = e.Hash[..12], prev = e.PrevHash[..12] };
}
