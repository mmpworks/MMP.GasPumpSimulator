using GasPump.Core;

namespace GasPump.Payment;

/// <summary>
/// The cashier's till, for prepaid cash sales. No bank, no network, no hold:
/// the cash taken is the ceiling. Settle returns the change owed when the
/// driver pumped less than they paid. Refund returns the whole amount when
/// nothing was pumped. Both are idempotent per sale, like the issuer.
/// </summary>
public sealed class CashDrawer
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Money> _open = new();
    private readonly Dictionary<string, Money> _changeDue = new();

    /// <summary>Cash sales taken and not yet settled or refunded, by sale id.</summary>
    public IReadOnlyDictionary<string, Money> Open { get { lock (_gate) return new Dictionary<string, Money>(_open); } }

    /// <summary>Change the cashier still owes, by sale id. Cleared when the driver collects it.</summary>
    public IReadOnlyDictionary<string, Money> ChangeDue { get { lock (_gate) return new Dictionary<string, Money>(_changeDue); } }

    public void Take(string saleId, Money amount)
    {
        lock (_gate) _open[saleId] = amount;
    }

    /// <summary>Close the sale for the fuel delivered. Returns the change due.</summary>
    public Money Settle(string saleId, Money final)
    {
        lock (_gate)
        {
            if (!_open.Remove(saleId, out var paid)) return _changeDue.GetValueOrDefault(saleId);
            var change = paid - Money.Min(final, paid);
            if (change > Money.Zero) _changeDue[saleId] = change;
            return change;
        }
    }

    /// <summary>Nothing was pumped. The whole amount goes back. Returns the refund.</summary>
    public Money Refund(string saleId)
    {
        lock (_gate)
        {
            if (!_open.Remove(saleId, out var paid)) return Money.Zero;
            _changeDue[saleId] = paid;
            return paid;
        }
    }

    public void ChangeCollected(string saleId)
    {
        lock (_gate) _changeDue.Remove(saleId);
    }
}
