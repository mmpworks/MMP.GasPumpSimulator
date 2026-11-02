using GasPump.Core;
using GasPump.Security;

namespace GasPump.Pump;

/// <summary>
/// The forecourt standard's transaction buffer, on the island. When a
/// delivery ends with fuel moved, the pump writes the transaction here and
/// keeps it until the station locks it and clears it. While one is open the
/// pump refuses to arm, so a sale can never be paid twice or lost between the
/// hang-up and the settlement. The dispenser is the device that counted the
/// fuel, so a station that restarts settles from this record, not from its
/// own last snapshot.
/// </summary>
public sealed class TransactionBuffer(EventLedger ledger)
{
    private readonly Lock _gate = new();
    private readonly List<PumpTransaction> _items = [];
    private long _seq;

    /// <summary>Every transaction not yet cleared, oldest first.</summary>
    public IReadOnlyList<PumpTransaction> Open { get { lock (_gate) return _items.Where(t => t.State != TransactionState.Cleared).ToList(); } }

    /// <summary>The last transaction recorded, cleared or not. Null before the first delivery.</summary>
    public PumpTransaction? Last { get { lock (_gate) return _items.Count == 0 ? null : _items[^1]; } }

    public bool HasOpen { get { lock (_gate) return _items.Any(t => t.State != TransactionState.Cleared); } }

    /// <summary>A delivery ended with fuel moved. Write it down as Payable.</summary>
    public PumpTransaction Record(string saleId, Grade grade, Volume volume, Money money, IReadOnlyList<ChargeLine> lines, DateTimeOffset endedAt, string endReason)
    {
        PumpTransaction t;
        lock (_gate)
        {
            t = new PumpTransaction(++_seq, saleId, grade, volume, money, lines, endedAt, endReason, TransactionState.Payable);
            _items.Add(t);
        }
        ledger.Append("tx.payable", $"#{t.TxSeq} {saleId} {grade} {volume} {money} {endReason}");
        return t;
    }

    /// <summary>A controller claims the transaction before settling it. A second controller is told who holds it.</summary>
    public (bool Ok, string Reason) Lock(long txSeq, string by)
    {
        lock (_gate)
        {
            var i = _items.FindIndex(t => t.TxSeq == txSeq);
            if (i < 0) return (false, $"no transaction #{txSeq}");
            var t = _items[i];
            if (t.State == TransactionState.Cleared) return (false, $"#{txSeq} already cleared");
            if (t.State == TransactionState.Locked && t.LockedBy != by) return (false, $"#{txSeq} locked by {t.LockedBy}");
            _items[i] = t with { State = TransactionState.Locked, LockedBy = by };
        }
        ledger.Append("tx.locked", $"#{txSeq} by {by}");
        return (true, "");
    }

    /// <summary>The controller has settled or queued the money. The pump may forget the transaction and arm again.</summary>
    public (bool Ok, string Reason) Clear(long txSeq, string by)
    {
        lock (_gate)
        {
            var i = _items.FindIndex(t => t.TxSeq == txSeq);
            if (i < 0) return (false, $"no transaction #{txSeq}");
            var t = _items[i];
            if (t.State == TransactionState.Cleared) return (true, "already cleared");
            if (t.State == TransactionState.Locked && t.LockedBy != by) return (false, $"#{txSeq} locked by {t.LockedBy}");
            _items[i] = t with { State = TransactionState.Cleared, LockedBy = by };
            // Keep a short tail of cleared transactions for the view and the restart; the ledger holds the rest.
            while (_items.Count > 32 && _items[0].State == TransactionState.Cleared) _items.RemoveAt(0);
        }
        ledger.Append("tx.cleared", $"#{txSeq} by {by}");
        return (true, "");
    }

    /// <summary>For the state file: every transaction kept, cleared ones included, plus the sequence counter.</summary>
    public (long Seq, IReadOnlyList<PumpTransaction> Items) Export() { lock (_gate) return (_seq, _items.ToList()); }

    /// <summary>From the state file, before any delivery. A restart of the island must not forget an unpaid delivery or reuse a number.</summary>
    public void Restore(long seq, IEnumerable<PumpTransaction> items)
    {
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(items);
            _seq = Math.Max(seq, _items.Count == 0 ? 0 : _items.Max(t => t.TxSeq));
        }
    }
}
