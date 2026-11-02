using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Payment;
using GasPump.Security;

namespace GasPump.Host;

/// <summary>The JSON the UI sees. One shape for the REST snapshot and the socket push. Site-wide fields at the top, one entry per pump under <c>pumps</c>.</summary>
public static class Contracts
{
    public static object State(Simulation sim, SimulationHost? host = null, StationServer? station = null, IReadOnlyList<DispenserProcess>? processes = null, StationIdentity? identity = null)
    {
        var who = identity ?? StationIdentity.Default;
        return new
        {
            type = "state",
            strategy = sim.StrategyName,
            pumpCount = sim.Points.Count,
            station = new
            {
                name = who.Name,
                theme = who.Theme,
                pid = Environment.ProcessId,
                port = who.Port,
                runId = sim.RunId,
                custodyPath = sim.CustodyPath,
                estopActive = host?.EStopActive ?? false,
                estopAt = host?.EStopAt,
            },
            pumps = sim.Points.Select(p => PumpEntry(sim, p, host, station, processes?.FirstOrDefault(x => x.Number == p.Number))).ToArray(),
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
            custody = new { path = sim.CustodyPath, journalPath = sim.JournalPath },
            limits = new
            {
                maxCents = sim.Options.HardLimits.MaxMoneyPerSale.Cents,
                maxGallons = sim.Options.HardLimits.MaxVolumePerSale.Gallons,
                maxMinutes = sim.Options.HardLimits.MaxSaleDuration.TotalMinutes,
                maxGpm = sim.Options.HardLimits.MaxFlow.GallonsPerMinuteValue,
            },
            // The station's posted board in mills, for the admin panel's fields.
            priceBoard = sim.Prices.TenthCentsPerGallon.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            ledgerOk = sim.Ledger.Verify() == 0,
        };
    }

    /// <summary>One pump as the station knows it: its sale, its island's view, its link and process, what the last restart did about it, its totals, and the prices it bills by.</summary>
    private static object PumpEntry(Simulation sim, FuellingPoint p, SimulationHost? host, StationServer? station, DispenserProcess? process)
    {
        var link = p.Link;
        var copy = station?.Copy(p.Number);
        return new
        {
            number = p.Number,
            pumpId = p.PumpId,
            node = PumpNodes.Of(p.PumpId).ToString(),
            sale = Sale(p.Snapshot()),
            // While the E-stop holds, the forecourt has no power: the pump is out of service whatever its last view said.
            pump = Pump(host?.EStopActive == true ? p.View with { Service = EStopStatus } : p.View),
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
                port = process?.Port ?? sim.Options.DispenserFor(p.Number)?.Port,
                processRunning = process?.Running ?? p.DispenserIsLocal,
                external = process?.External ?? false,
                lastExit = process?.LastExit,
            },
            // The station's copy of this pump's chain.
            copy = new { path = copy?.Path, linesReceived = copy?.Lines ?? 0, runId = copy?.RunId, linesPublished = p.DispenserCustodyLines },
            recovery = Recovery(p.LastRecovery),
            totals = p.LastTotals is { } t ? Totals(t) : null,
            // What the pump bills by, as the pump face shows it. The station's own board stands in until a dispenser has answered.
            prices = Display(p.View.Prices is { } inForce ? new Pricing(inForce) : sim.Prices),
            // False while the island still bills by an older board: a write refused, unanswered, or waiting for a delivery to end.
            pricesInSync = p.View.Prices is null || sim.Prices.SameAs(new Pricing(p.View.Prices)),
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

    /// <summary>The pump as the station knows it. Same shape whether the dispenser is a mailbox away or a socket away. <paramref name="pump"/> is set on a push, so the UI knows which tab it is for.</summary>
    public static object Pump(DispenserView v, int? pump = null) => new
    {
        type = "pump",
        pump,
        pumpId = v.PumpId,
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

    /// <summary>The sale on one pump's screen. The sale id names the pump; <c>pump</c> repeats it as a number for the UI.</summary>
    public static object Sale(SaleSnapshot s) => new
    {
        type = "sale",
        pump = SaleIds.PumpOf(s.SaleId),
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

    /// <summary><paramref name="pump"/> is the pump the line is about, or null for a line about the site. The Origin and Pump properties are the record's own and are not repeated in <c>props</c>.</summary>
    public static object Log(LogLine l, string origin, int? pump) => new
    {
        type = "log",
        origin,
        pump,
        at = l.At,
        l.Source,
        l.Message,
        severity = l.Severity.ToString(),
        props = l.Props.Where(p => p.Key is not ("Origin" or "Pump")).ToDictionary(p => p.Key, p => p.Value?.ToString()),
    };

    public static object Ledger(LedgerEntry e, string origin, int? pump) => new { type = "ledger", origin, pump, e.Seq, e.At, e.Kind, e.Detail, hash = e.Hash[..12], prev = e.PrevHash[..12] };
}
