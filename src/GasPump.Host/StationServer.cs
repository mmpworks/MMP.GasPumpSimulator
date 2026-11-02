using GasPump.Dispatch;
using MMP.Herald.Compliance.Audit;

namespace GasPump.Host;

/// <summary>
/// The station's copies of the dispensers' chains, one copy per pump. Each
/// subscribes to every custody line its dispenser publishes and appends it,
/// unchanged, to a file per dispenser run. Because the bytes are the same,
/// TesseraSeal's verifier accepts the copy with the same key, and the station
/// can tell when a dispenser's chain and its own copy disagree. In-process
/// the lines come off a tail of the shared file; over the socket they come off
/// the wire, which is what a real station server receives. /ws/custody hands
/// every pump's lines on. A copy drops any line it already holds, so a replay
/// after a restart or a reconnect never doubles the file.
/// </summary>
public sealed class StationServer : IDisposable
{
    private readonly string _directory;
    private readonly byte[] _ikm;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, CustodyCopy> _copies = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Action<string>> _subscribers = [];

    public StationServer(string directory, byte[] ikm)
    {
        _directory = directory;
        _ikm = ikm;
    }

    /// <summary>Pump 1's copy, for the one-pump contract.</summary>
    public string? CopyPath => Copy(1).Path;
    public long LinesReceived => Copy(1).Lines;
    /// <summary>Replayed lines pump 1's copy already held and dropped.</summary>
    public long Duplicates => Copy(1).Duplicates;
    /// <summary>The dispenser run pump 1's current copy belongs to.</summary>
    public string? RunId => Copy(1).RunId;

    /// <summary>The copy for pump <paramref name="number"/>, made on first use.</summary>
    public CustodyCopy Copy(int number)
    {
        lock (_gate)
        {
            if (!_copies.TryGetValue(number, out var copy)) _copies[number] = copy = new CustodyCopy(_directory, $"P{number}");
            return copy;
        }
    }

    /// <summary>Follow every dispenser of this station. Called on every station swap; a dispenser restart shows up as a new run id on the lines.</summary>
    public void Bind(Simulation sim)
    {
        lock (_gate)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
            foreach (var point in sim.Points)
            {
                // In-process every pump writes the one station chain, so one copy of it is the whole truth; a copy per pump would double it.
                if (point.DispenserIsLocal && point.Number != 1) continue;
                var copy = Copy(point.Number);
                _subscriptions.Add(point.SubscribeDispenserCustody((runId, line) => OnLine(copy, runId, line)));
            }
        }
    }

    private void OnLine(CustodyCopy copy, string runId, string line)
    {
        if (!copy.Append(runId, line)) return;
        Action<string>[] subs;
        lock (_gate) subs = _subscribers.ToArray();
        foreach (var s in subs) s(line);
    }

    /// <summary>Outside listeners: the WebSocket topic hands each line on, from every pump.</summary>
    public IDisposable Subscribe(Action<string> onLine)
    {
        lock (_gate) _subscribers.Add(onLine);
        return new Unsubscriber(this, onLine);
    }

    /// <summary>Run TesseraSeal's verifier over pump <paramref name="number"/>'s copy. The copy is closed between appends, so it reads directly.</summary>
    public (bool Ok, long? FailureAtSeq, string? Reason, int Lines) VerifyCopy(int number = 1)
    {
        var path = Copy(number).Path;
        if (path is null || !File.Exists(path)) return (false, null, $"no station copy for pump {number} yet", 0);
        var ok = ImmutableAuditFileSink.VerifyChain(path, _ikm, out var seq, out var reason);
        return (ok, seq, reason, File.ReadLines(path).Count());
    }

    public void Dispose()
    {
        lock (_gate) { foreach (var s in _subscriptions) s.Dispose(); _subscriptions.Clear(); }
    }

    private sealed class Unsubscriber(StationServer owner, Action<string> onLine) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._subscribers.Remove(onLine); }
    }
}
