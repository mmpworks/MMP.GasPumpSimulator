using GasPump.Dispatch;

namespace GasPump.Host;

/// <summary>
/// Owns the current Simulation and can replace it. PowerLoss drops the
/// current one the way a crash would, with no close-out and no final
/// snapshot, then builds a fresh one on the same journal directory. The
/// fresh one reads the last periodic snapshot and recovers. Subscribers
/// (the WebSocket hub) rebind through <see cref="Swapped"/>.
/// </summary>
public sealed class SimulationHost : IDisposable
{
    private readonly Func<Simulation> _factory;
    private readonly Lock _gate = new();
    private Simulation _current;

    public SimulationHost(Func<Simulation> factory)
    {
        _factory = factory;
        _current = factory();
    }

    public Simulation Current { get { lock (_gate) return _current; } }

    public event Action<Simulation>? Swapped;

    public void PowerLoss()
    {
        Simulation old, fresh;
        lock (_gate)
        {
            old = _current;
            old.SimulatePowerLoss();
            fresh = _factory();
            _current = fresh;
        }
        Swapped?.Invoke(fresh);
    }

    public void Dispose() => Current.Dispose();
}
