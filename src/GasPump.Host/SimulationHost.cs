using GasPump.Dispatch;

namespace GasPump.Host;

/// <summary>
/// Owns the current Simulation and can replace it. PowerLoss drops the
/// current one the way a crash would, with no close-out and no final
/// snapshot, then builds a fresh one on the same journal directory.
/// EmergencyStop writes the record, saves a final snapshot, drops the
/// power, and waits: the station stays dark until a cashier resets it.
/// Subscribers (the WebSocket hub) rebind through <see cref="Swapped"/>.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    private readonly Func<Simulation> _factory;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private Simulation _current;

    public SimulationHost(Func<Simulation> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
        _current = factory();
    }

    public Simulation Current { get { lock (_gate) return _current; } }

    /// <summary>True between an E-stop and the cashier's reset. The current simulation is dark; reads still answer from its last state.</summary>
    public bool EStopActive { get; private set; }
    public DateTimeOffset? EStopAt { get; private set; }

    public event Action<Simulation>? Swapped;

    public void PowerLoss()
    {
        Simulation fresh;
        lock (_gate)
        {
            if (EStopActive) return;
            _current.SimulatePowerLoss();
            fresh = _factory();
            _current = fresh;
        }
        Swapped?.Invoke(fresh);
    }

    public void EmergencyStop(string pressedBy)
    {
        lock (_gate)
        {
            if (EStopActive) return;
            _current.EmergencyStop(pressedBy);
            EStopActive = true;
            EStopAt = _clock.GetUtcNow();
        }
    }

    /// <summary>The cashier's reset. Power returns, a new Simulation recovers from the E-stop's final snapshot.</summary>
    public void Reset()
    {
        Simulation fresh;
        lock (_gate)
        {
            if (!EStopActive) return;
            fresh = _factory();
            _current = fresh;
            EStopActive = false;
            EStopAt = null;
        }
        Swapped?.Invoke(fresh);
    }

    public void Dispose() => Current.Dispose();
}
