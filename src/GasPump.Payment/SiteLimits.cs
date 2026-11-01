using GasPump.Core;

namespace GasPump.Payment;

/// <summary>
/// The site's own ceilings on how much it asks the bank to hold, by tender.
/// A station manager sets these from the back office: debit holds are kept
/// low because a debit hold is real money gone from a checking account until
/// settlement, and credit holds can run higher. Cash has no entry here: the
/// cash amount is the ceiling.
///
/// These are business limits, upstream of the pump. The pump's HardLimits
/// are the safety limits and clamp whatever arrives here, so an attacker who
/// owns this panel still cannot exceed $500 or 50 gallons.
///
/// Changes need the admin PIN. Three wrong PINs lock the panel for a while.
/// Every attempt is reported through <see cref="Changed"/> so the ledger
/// has the whole story.
/// </summary>
public sealed class SiteLimits
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly string _adminPin;
    private Money _creditHold;
    private Money _debitHold;
    private int _failedPins;
    private DateTimeOffset? _lockedUntil;

    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);
    public const int MaxPinFailures = 3;

    public SiteLimits(TimeProvider clock, Money creditHold, Money debitHold, string adminPin = "1234")
    {
        _clock = clock;
        _creditHold = creditHold;
        _debitHold = debitHold;
        _adminPin = adminPin;
    }

    public Money CreditHold { get { lock (_gate) return _creditHold; } }
    public Money DebitHold { get { lock (_gate) return _debitHold; } }
    public int FailedPins { get { lock (_gate) return _failedPins; } }
    public DateTimeOffset? LockedUntil { get { lock (_gate) return IsLocked() ? _lockedUntil : null; } }

    /// <summary>Kind: "admin.limits" on success, "admin.refused" on a bad PIN, "admin.locked" when the panel locks. Detail is human text.</summary>
    public event Action<string, string>? Changed;

    /// <summary>How much to ask the bank to hold for this tender. Cash never asks the bank.</summary>
    public Money HoldFor(Tender tender) => tender switch
    {
        Tender.Debit => DebitHold,
        Tender.Credit => CreditHold,
        _ => throw new ArgumentOutOfRangeException(nameof(tender), tender, "cash has no bank hold"),
    };

    public (bool Ok, string Reason) TrySet(string pin, Tender tender, Money amount)
    {
        string kind, detail;
        bool ok;
        lock (_gate)
        {
            if (IsLocked())
            {
                (ok, kind, detail) = (false, "admin.refused", $"panel locked until {_lockedUntil:HH:mm:ss}");
            }
            else if (pin != _adminPin)
            {
                _failedPins++;
                if (_failedPins >= MaxPinFailures)
                {
                    _lockedUntil = _clock.GetUtcNow() + Lockout;
                    (ok, kind, detail) = (false, "admin.locked", $"{_failedPins} wrong PINs, panel locked for {Lockout.TotalMinutes:0} min");
                }
                else (ok, kind, detail) = (false, "admin.refused", $"wrong PIN ({_failedPins} of {MaxPinFailures})");
            }
            else if (tender == Tender.Cash)
            {
                (ok, kind, detail) = (false, "admin.refused", "cash has no hold ceiling; the cash amount is the ceiling");
            }
            else if (amount <= Money.Zero)
            {
                (ok, kind, detail) = (false, "admin.refused", $"hold must be above zero, got {amount}");
            }
            else
            {
                _failedPins = 0;
                var before = tender == Tender.Debit ? _debitHold : _creditHold;
                if (tender == Tender.Debit) _debitHold = amount; else _creditHold = amount;
                (ok, kind, detail) = (true, "admin.limits", $"{tender} hold {before} -> {amount}");
            }
        }
        Changed?.Invoke(kind, detail);
        return (ok, detail);
    }

    /// <summary>For the journal. A reboot must not reset a lockout, or three wrong PINs and a power cycle would be the attack.</summary>
    public void Restore(Money creditHold, Money debitHold, int failedPins, DateTimeOffset? lockedUntil)
    {
        lock (_gate)
        {
            _creditHold = creditHold;
            _debitHold = debitHold;
            _failedPins = failedPins;
            _lockedUntil = lockedUntil;
        }
    }

    private bool IsLocked()
    {
        if (_lockedUntil is { } until && _clock.GetUtcNow() < until) return true;
        if (_lockedUntil is not null) { _lockedUntil = null; _failedPins = 0; }
        return false;
    }
}
