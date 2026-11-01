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
    string Note)
{
    public bool UnderRecorded => Kind == RecoveryKind.ClosedOut;
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
        var volume = new Volume(s.VolumeMilliGallons);
        var money = new Money(s.MoneyCents);
        var age = now - snapshot.SavedAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var flow = new FlowRate(s.FlowMilliGallonsPerSecond);
        var unrecorded = flow.Over(snapshotPeriod);

        switch (state)
        {
            case SaleState.Fueling when volume > Volume.Zero:
                return new(RecoveryKind.ClosedOut, snapshot.RunId, s.SaleId, tender, grade, volume, money, unrecorded, age,
                    $"pump restarted {age.TotalSeconds:0.0} s after the last record while fueling; closed for {volume} at {money}; delivery likely under-recorded by up to {unrecorded}");
            case SaleState.Fueling:
            case SaleState.Authorizing:
            case SaleState.ReadyToPump:
                return new(RecoveryKind.Reversed, snapshot.RunId, s.SaleId, tender, grade, Volume.Zero, Money.Zero, Volume.Zero, age,
                    $"pump restarted {age.TotalSeconds:0.0} s after the last record in {state} with no fuel delivered; {(tender == Tender.Cash ? "cash refunded" : "hold reversed")}");
            case SaleState.Completing:
                return new(RecoveryKind.CompletionResent, snapshot.RunId, s.SaleId, tender, grade, volume, money, Volume.Zero, age,
                    $"pump restarted {age.TotalSeconds:0.0} s after the last record while settling {money}; completion sent again");
            default:
                return new(RecoveryKind.CleanStart, snapshot.RunId, null, tender, grade, Volume.Zero, Money.Zero, Volume.Zero, age,
                    $"previous run ended in {state}; nothing to close");
        }
    }
}
