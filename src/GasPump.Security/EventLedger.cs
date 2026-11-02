using System.Security.Cryptography;
using System.Text;
using GasPump.Core;

namespace GasPump.Security;

public sealed record LedgerEntry(long Seq, DateTimeOffset At, string Kind, string Detail, string PrevHash, string Hash)
{
    public static string Compute(long seq, DateTimeOffset at, string kind, string detail, string prevHash)
    {
        var bytes = Encoding.UTF8.GetBytes($"{seq}|{at.ToUnixTimeMilliseconds()}|{kind}|{detail}|{prevHash}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

/// <summary>
/// Append-only, hash-chained. Every entry carries the hash of the one before
/// it, so editing entry 7 breaks entries 8 onward. Verify walks the whole
/// chain. The totalizer is recorded here on every sale, which is how stolen
/// volume shows up in the books even if the sale record was erased.
/// </summary>
public sealed class EventLedger(TimeProvider clock)
{
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly Lock _gate = new();
    private readonly List<LedgerEntry> _entries = [];

    public event Action<LedgerEntry>? Appended;

    public LedgerEntry Append(string kind, string detail)
    {
        LedgerEntry entry;
        lock (_gate)
        {
            var prev = _entries.Count == 0 ? Genesis : _entries[^1].Hash;
            var seq = _entries.Count + 1;
            var at = clock.GetUtcNow();
            entry = new LedgerEntry(seq, at, kind, detail, prev, LedgerEntry.Compute(seq, at, kind, detail, prev));
            _entries.Add(entry);
        }
        Appended?.Invoke(entry);
        return entry;
    }

    public IReadOnlyList<LedgerEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>Returns the first broken sequence number, or 0 when the chain is intact.</summary>
    public long Verify() => Verify(Snapshot());

    public static long Verify(IReadOnlyList<LedgerEntry> entries)
    {
        var prev = Genesis;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Seq != i + 1) return e.Seq;
            if (e.PrevHash != prev) return e.Seq;
            if (LedgerEntry.Compute(e.Seq, e.At, e.Kind, e.Detail, e.PrevHash) != e.Hash) return e.Seq;
            prev = e.Hash;
        }
        return 0;
    }
}

/// <summary>
/// The sealed counter on the side of the pump. It only goes up. Handbook 44
/// requires one on every dispenser so the books can be checked against the
/// meter. There is no Reset method on purpose.
/// </summary>
public sealed class Totalizer
{
    private long _milliGallons;
    public Volume Total => new(Interlocked.Read(ref _milliGallons));
    public void Add(Volume delivered)
    {
        if (delivered.MilliGallons < 0) throw new ArgumentOutOfRangeException(nameof(delivered), "a totalizer never goes down");
        Interlocked.Add(ref _milliGallons, delivered.MilliGallons);
    }

    /// <summary>
    /// Seed the counter from the journal at start. Allowed once, and only
    /// upward from zero, because a counter that can be set lower can't be
    /// used to check the books.
    /// </summary>
    public void Restore(Volume journaled)
    {
        if (journaled.MilliGallons < 0) throw new ArgumentOutOfRangeException(nameof(journaled));
        if (Interlocked.CompareExchange(ref _milliGallons, journaled.MilliGallons, 0) != 0)
            throw new InvalidOperationException("a totalizer is restored once, before the first sale");
    }
}
