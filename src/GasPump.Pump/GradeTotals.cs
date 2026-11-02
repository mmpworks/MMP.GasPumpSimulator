using GasPump.Core;

namespace GasPump.Pump;

/// <summary>
/// Lifetime totals per grade, on the island. Each finished delivery adds its
/// volume and money to its grade's row. The sealed <see cref="Security.Totalizer"/>
/// counts live, slice by slice, and is the number an inspector seals; these
/// rows break the same fuel down by product, as the forecourt standard
/// reports per nozzle. Both ride the dispenser's state file.
/// </summary>
public sealed class GradeTotals
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Grade, GradeTotal> _rows = new();

    /// <summary>A delivery ended with fuel moved. Add it to its grade.</summary>
    public void Add(Grade grade, Volume volume, Money money)
    {
        lock (_gate)
        {
            var row = _rows.TryGetValue(grade, out var have) ? have : new GradeTotal(grade, Volume.Zero, Money.Zero, 0);
            _rows[grade] = row with { Volume = row.Volume + volume, Money = row.Money + money, Deliveries = row.Deliveries + 1 };
        }
    }

    /// <summary>Every grade that has delivered, in grade order. Empty before the first delivery.</summary>
    public IReadOnlyList<GradeTotal> Snapshot() { lock (_gate) return _rows.Values.OrderBy(r => r.Grade).ToList(); }

    /// <summary>From the state file at start, before any delivery. Once only: a counter that can be set again cannot be used to check the books.</summary>
    public void Restore(IEnumerable<GradeTotal> rows)
    {
        lock (_gate)
        {
            if (_rows.Count > 0) throw new InvalidOperationException("grade totals are restored once, before the first delivery");
            foreach (var r in rows) _rows[r.Grade] = r;
        }
    }
}
