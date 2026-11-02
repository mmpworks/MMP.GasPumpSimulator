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
                lastSeqSent = link.LastSeqSent,
                lastSeqReceived = link.LastSeqReceived,
                gaps = link.Gaps,
                duplicates = link.Duplicates,
                heartbeatAt = link.LastHeartbeatAt,
                peerStatus = link.PeerStatus,
                peerAlive = link.PeerAliveNow,
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
            // What the pump bills by, as the pump face shows it. The station's own board stands in until a dispenser has answered.
            prices = Display(sim.View.Prices is { } inForce ? new Pricing(inForce) : sim.Prices),
            // The station's posted board in mills, for the admin panel's fields.
            priceBoard = sim.Prices.TenthCentsPerGallon.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            // False while the island still bills by an older board: a write refused, unanswered, or waiting for a delivery to end.
            pricesInSync = sim.View.Prices is null || sim.Prices.SameAs(new Pricing(sim.View.Prices)),
            totals = sim.LastTotals is { } t ? Totals(t) : null,
            ledgerOk = sim.Ledger.Verify() == 0,
        };
    }

    private static Dictionary<string, string> Display(Pricing board) =>
        Enum.GetValues<Grade>().Where(board.TenthCentsPerGallon.ContainsKey).ToDictionary(g => g.ToString(), board.Display);

    /// <summary>The island's counters as last read: the sealed totalizer live, the per-grade rows by finished delivery.</summary>
    public static object Totals(PumpTotals t) => new
    {
        totalizerGallons = t.Totalizer.Gallons,
        lastTxSeq = t.LastTxSeq,
        readAt = t.ReadAt,
        byGrade = t.ByGrade.Select(g => new { grade = g.Grade.ToString(), gallons = g.Volume.Gallons, cents = g.Money.Cents, g.Deliveries }),
    };

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
        spoutInserted = v.SpoutInserted,
        leverHeld = v.LeverHeld,
        selectedGrade = v.SelectedGrade?.ToString(),
        poured = v.Poured,
        armed = v.Armed is { } a ? new { a.SaleId, ceilingCents = a.Ceiling.Cents, maxGallons = a.MaxVolume.Gallons, a.Clamped, a.ClampNote } : null,
        fault = v.Fault,
        totalizerGallons = new Volume(v.TotalizerMilliGallons).Gallons,
        ledgerIntact = v.LedgerIntact,
        runId = v.RunId,
        pid = v.Pid,
        openTransactions = (v.OpenTransactions ?? []).Select(t => new { txSeq = t.TxSeq, t.SaleId, grade = t.Grade.ToString(), gallons = t.Volume.Gallons, cents = t.Money.Cents, state = t.State.ToString(), t.LockedBy, t.EndReason }),
        prices = v.Prices?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
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
        s.SpoutInserted,
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
