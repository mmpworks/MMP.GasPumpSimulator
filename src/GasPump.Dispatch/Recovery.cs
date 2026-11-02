using GasPump.Core;

namespace GasPump.Dispatch;

public enum RecoveryKind
{
    /// <summary>No journal, or the last run ended with nothing open.</summary>
    CleanStart,
    /// <summary>Fuel had moved. The sale closes for the money recorded, and the record says delivery may be under-counted.</summary>
    ClosedOut,
    /// <summary>A hold or cash was taken and no fuel moved. The hold is reversed, the cash refunded.</summary>
    Reversed,
    /// <summary>Fueling had ended and settlement was in flight. The completion is sent again; the bank treats it as the same one.</summary>
    CompletionResent,
}

/// <summary>What the restart decided about the sale it found open, in words a driver and an auditor can both read.</summary>
public sealed record RecoveryOutcome(
    RecoveryKind Kind,
    string PreviousRunId,
    string? SaleId,
    Tender Tender,
    Grade Grade,
    Volume Volume,
    Money Money,
    /// <summary>How much more fuel may have left the hose after the last snapshot: one snapshot period at the last flow rate. An estimate, never a count.</summary>
    Volume UnrecordedUpTo,
    TimeSpan SnapshotAge,
    string Note,
    /// <summary>The itemized charges a resent completion carries, from the journal. Null when the journal had none.</summary>
    IReadOnlyList<ChargeLine>? Lines = null)
{
    public bool UnderRecorded => Kind == RecoveryKind.ClosedOut && UnrecordedUpTo > Volume.Zero;
}

/// <summary>
/// A pure decision: given the last snapshot and the clock now, what does the
/// restart do about the sale that was open. No I/O, no workers, so the whole
/// table is testable with records. The simulation carries the decision out.
/// </summary>
public static class RecoveryPlan
{
    public static RecoveryOutcome Build(JournalSnapshot? snapshot, DateTimeOffset now, TimeSpan snapshotPeriod)
    {
        if (snapshot is null)
            return new(RecoveryKind.CleanStart, "", null, Tender.Credit, Grade.Regular, Volume.Zero, Money.Zero, Volume.Zero, TimeSpan.Zero, "no journal found, first run");

        var s = snapshot.Sale;
        var state = Enum.Parse<SaleState>(s.State);
        var tender = Enum.Parse<Tender>(s.Tender);
        var grade = Enum.Parse<Grade>(s.Grade);
        var hold = Enum.TryParse<HoldStatus>(s.Hold, out var h) ? h : HoldStatus.None;
        var volume = new Volume(s.VolumeMilliGallons);
        var money = new Money(s.MoneyCents);
        var age = now - snapshot.SavedAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var flow = new FlowRate(s.FlowMilliGallonsPerSecond);
        // An E-stop wrote its own final snapshot on the way down, so nothing flowed after it. A crash did not.
        var estop = snapshot.StopReason == "ESTOP";
        var unrecorded = estop ? Volume.Zero : flow.Over(snapshotPeriod);
        var how = estop ? $"restarted after the station E-stop ({DiagnosticCodes.StationEStop.Code})" : $"pump restarted {age.TotalSeconds:0.0} s after the last record";
        var lines = JournalLine.To(s.Lines);

        switch (state)
        {
            case SaleState.Fueling when volume > Volume.Zero:
                return new(RecoveryKind.ClosedOut, snapshot.RunId, s.SaleId, tender, grade, volume, money, unrecorded, age,
                    estop
                        ? $"{how} while fueling; closed for {volume} at {money}; the E-stop cut power with the valve open, so the record is exact to the last tick"
                        : $"{how} while fueling; closed for {volume} at {money}; delivery likely under-recorded by up to {unrecorded}",
                    lines);
            case SaleState.Fueling:
            case SaleState.Authorizing:
            case SaleState.ReadyToPump:
                return new(RecoveryKind.Reversed, snapshot.RunId, s.SaleId, tender, grade, Volume.Zero, Money.Zero, Volume.Zero, age,
                    $"{how} in {state} with no fuel delivered; {(tender == Tender.Cash ? "cash refunded" : "hold reversed")}");
            case SaleState.Completing:
                return new(RecoveryKind.CompletionResent, snapshot.RunId, s.SaleId, tender, grade, volume, money, Volume.Zero, age,
                    $"{how} while settling {money}; completion sent again", lines);
            // The sale had ended but the bank still held the money: the compensation never left. Finish it.
            case SaleState.Faulted or SaleState.Cancelled or SaleState.Declined when hold is HoldStatus.Requested or HoldStatus.Held && volume > Volume.Zero:
                return new(RecoveryKind.CompletionResent, snapshot.RunId, s.SaleId, tender, grade, volume, money, Volume.Zero, age,
                    $"{how} after the sale ended {state} with {money} still held for {volume} delivered; completion sent", lines);
            case SaleState.Faulted or SaleState.Cancelled or SaleState.Declined when hold is HoldStatus.Requested or HoldStatus.Held:
                return new(RecoveryKind.Reversed, snapshot.RunId, s.SaleId, tender, grade, Volume.Zero, Money.Zero, Volume.Zero, age,
                    $"{how} after the sale ended {state} with the hold still at the bank and no fuel delivered; {(tender == Tender.Cash ? "cash refunded" : "hold reversed")}");
            case SaleState.Faulted or SaleState.Cancelled or SaleState.Declined or SaleState.StoreAndForward when hold is HoldStatus.SettlePending or HoldStatus.ReleasePending:
                return new(RecoveryKind.CleanStart, snapshot.RunId, null, tender, grade, Volume.Zero, Money.Zero, Volume.Zero, age,
                    $"previous run ended in {state} with advice queued for the bank; the queue drains on this run");
            default:
                return new(RecoveryKind.CleanStart, snapshot.RunId, null, tender, grade, Volume.Zero, Money.Zero, Volume.Zero, age,
                    $"previous run ended in {state}; nothing to close");
        }
    }
}
