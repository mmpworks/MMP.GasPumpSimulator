namespace GasPump.Core;

/// <summary>Where a delivered transaction stands in the dispenser's buffer.</summary>
public enum TransactionState
{
    /// <summary>Fuel moved and the delivery ended. The dispenser holds the record until the station clears it.</summary>
    Payable,
    /// <summary>A controller has claimed it and is settling it. No other controller may touch it.</summary>
    Locked,
    /// <summary>Settled or queued by the controller. The dispenser may drop it.</summary>
    Cleared,
}

/// <summary>
/// A finished delivery as the dispenser remembers it: the forecourt
/// standard's transaction buffer entry. The dispenser is the device that
/// counted the fuel, so this is the record of truth for the money, and it
/// stays on the island until the station says it has been dealt with. A
/// station that restarts asks for these and settles from them.
/// </summary>
public sealed record PumpTransaction(
    long TxSeq,
    string SaleId,
    Grade Grade,
    Volume Volume,
    Money Money,
    IReadOnlyList<ChargeLine> Lines,
    DateTimeOffset EndedAt,
    string EndReason,
    TransactionState State,
    /// <summary>Who locked it, when it is locked.</summary>
    string? LockedBy = null);

/// <summary>
/// Lifetime totals for one grade, the way the forecourt standard keeps them
/// per nozzle: volume, money, and how many deliveries. Counted when a
/// delivery ends, so a read during a pour shows the deliveries before it.
/// </summary>
public sealed record GradeTotal(Grade Grade, Volume Volume, Money Money, long Deliveries);

/// <summary>
/// What a read of totals returns: the sealed counter, which counts every
/// slice as it moves; the per-grade totals, which count finished deliveries;
/// the number of the last transaction written; and the island's clock when
/// it answered. An inspector's close-out and the station's restart both read
/// this instead of trusting their own last copy.
/// </summary>
public sealed record PumpTotals(Volume Totalizer, IReadOnlyList<GradeTotal> ByGrade, long LastTxSeq, DateTimeOffset ReadAt);
