using GasPump.Dispatch;
using MMP.Herald.Compliance.Audit;

namespace GasPump.Host;

/// <summary>
/// The station's copy of the dispenser's chain. It subscribes to every
/// custody line the dispenser publishes and appends it, unchanged, to a file
/// per dispenser run. Because the bytes are the same, TesseraSeal's verifier
/// accepts the copy with the same key, and the station can tell when the
/// dispenser's chain and its own copy disagree. In-process the lines come off
/// a tail of the shared file; over the socket they come off the wire, which
/// is what a real station server receives. /ws/custody hands them on.
/// The copy itself drops any line it already holds, so a replay after a
/// restart or a reconnect never doubles the file.
/// </summary>
public sealed class StationServer : IDisposable
{
    private readonly CustodyCopy _copy;
    private readonly byte[] _ikm;
    private readonly Lock _gate = new();
    private readonly List<Action<string>> _subscribers = [];
    private IDisposable? _subscription;

    public StationServer(string directory, byte[] ikm)
    {
        _copy = new CustodyCopy(directory);
        _ikm = ikm;
    }

    public string? CopyPath => _copy.Path;
    public long LinesReceived => _copy.Lines;
    /// <summary>Replayed lines the copy already held and dropped.</summary>
    public long Duplicates => _copy.Duplicates;
    /// <summary>The dispenser run the current copy belongs to.</summary>
    public string? RunId => _copy.RunId;

    /// <summary>Follow this station's dispenser. Called on every station swap; a dispenser restart shows up as a new run id on the lines.</summary>
    public void Bind(Simulation sim)
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = sim.SubscribeDispenserCustody(OnLine);
        }
    }

    private void OnLine(string runId, string line)
    {
        if (!_copy.Append(runId, line)) return;
        Action<string>[] subs;
        lock (_gate) subs = _subscribers.ToArray();
        foreach (var s in subs) s(line);
    }

    /// <summary>Outside listeners: the WebSocket topic hands each line on.</summary>
    public IDisposable Subscribe(Action<string> onLine)
    {
        lock (_gate) _subscribers.Add(onLine);
        return new Unsubscriber(this, onLine);
    }

    /// <summary>Run TesseraSeal's verifier over the station's copy. The copy is closed between appends, so it reads directly.</summary>
    public (bool Ok, long? FailureAtSeq, string? Reason, int Lines) VerifyCopy()
    {
        var path = _copy.Path;
        if (path is null || !File.Exists(path)) return (false, null, "no station copy yet", 0);
        var ok = ImmutableAuditFileSink.VerifyChain(path, _ikm, out var seq, out var reason);
        return (ok, seq, reason, File.ReadLines(path).Count());
    }

    public void Dispose()
    {
        lock (_gate) { _subscription?.Dispose(); _subscription = null; }
    }

    private sealed class Unsubscriber(StationServer owner, Action<string> onLine) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._subscribers.Remove(onLine); }
    }
}
