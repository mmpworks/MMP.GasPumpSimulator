namespace GasPump.Payment;

public enum NetworkMode { Up, Down, Timeout, Flapping }

/// <summary>
/// One object the UI can poke to make the wire misbehave. Latency applies in
/// every mode. Flapping fails a given fraction of calls at random.
/// </summary>
public sealed class NetworkFaultPolicy
{
    private readonly Lock _gate = new();
    private NetworkMode _mode = NetworkMode.Up;
    private TimeSpan _latency = TimeSpan.FromMilliseconds(250);
    private double _flapFailureRate = 0.5;

    public NetworkMode Mode { get { lock (_gate) return _mode; } set { lock (_gate) _mode = value; } }
    public TimeSpan Latency { get { lock (_gate) return _latency; } set { lock (_gate) _latency = value; } }
    public double FlapFailureRate { get { lock (_gate) return _flapFailureRate; } set { lock (_gate) _flapFailureRate = Math.Clamp(value, 0, 1); } }
}

public enum NetworkOutcome { Delivered, ConnectionRefused, TimedOut }

public interface INetwork
{
    /// <summary>Carries one request to the issuer. Waits the configured latency without holding a thread.</summary>
    Task<NetworkResult<T>> SendAsync<T>(Func<T> issuerCall, TimeSpan timeout, CancellationToken ct);
}

public sealed record NetworkResult<T>(NetworkOutcome Outcome, T? Value)
{
    public bool Delivered => Outcome == NetworkOutcome.Delivered;
}

/// <summary>
/// The wire between the site and the processor, with a fault policy on it.
/// Timeout mode makes the request vanish and the caller's own timeout fires.
/// Down mode refuses at once. Flapping flips a coin per call.
/// </summary>
public sealed class NetworkSimulator(NetworkFaultPolicy policy, TimeProvider clock, Random? random = null) : INetwork
{
    private readonly Random _random = random ?? Random.Shared;
    private int _inFlight;
    private int _waitingOnClock;

    /// <summary>Calls that have left and not come back.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Calls parked on a clock timer right now. Goes to zero inside the timer
    /// callback, before any continuation runs, so a test harness can tell
    /// "nothing moves until the clock does" from "a continuation is on its way".
    /// </summary>
    public int WaitingOnClock => Volatile.Read(ref _waitingOnClock);

    public async Task<NetworkResult<T>> SendAsync<T>(Func<T> issuerCall, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlight);
        try { return await SendCoreAsync(issuerCall, timeout, ct); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private async Task<NetworkResult<T>> SendCoreAsync<T>(Func<T> issuerCall, TimeSpan timeout, CancellationToken ct)
    {
        var mode = policy.Mode;
        if (mode == NetworkMode.Flapping)
            mode = _random.NextDouble() < policy.FlapFailureRate ? NetworkMode.Timeout : NetworkMode.Up;

        switch (mode)
        {
            case NetworkMode.Down:
                await WaitAsync(TimeSpan.FromMilliseconds(20), ct);
                return new NetworkResult<T>(NetworkOutcome.ConnectionRefused, default);

            case NetworkMode.Timeout:
                // The request leaves and nothing comes back. Wait out the caller's timeout.
                await WaitAsync(timeout, ct);
                return new NetworkResult<T>(NetworkOutcome.TimedOut, default);

            default:
                var latency = policy.Latency;
                if (latency >= timeout)
                {
                    await WaitAsync(timeout, ct);
                    return new NetworkResult<T>(NetworkOutcome.TimedOut, default);
                }
                await WaitAsync(latency, ct);
                return new NetworkResult<T>(NetworkOutcome.Delivered, issuerCall());
        }
    }

    /// <summary>Task.Delay on the TimeProvider, with the WaitingOnClock count maintained inside the timer callback.</summary>
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int settled = 0;
        Interlocked.Increment(ref _waitingOnClock);
        using var timer = clock.CreateTimer(_ =>
        {
            if (Interlocked.Exchange(ref settled, 1) == 0) Interlocked.Decrement(ref _waitingOnClock);
            done.TrySetResult();
        }, null, delay, Timeout.InfiniteTimeSpan);
        using var reg = ct.Register(() =>
        {
            if (Interlocked.Exchange(ref settled, 1) == 0) Interlocked.Decrement(ref _waitingOnClock);
            done.TrySetCanceled(ct);
        });
        await done.Task;
    }
}
