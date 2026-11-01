using GasPump.Dispatch;
using MMP.Herald.Compliance.Audit;

namespace GasPump.Host;

/// <summary>
/// The station's side of the custody stream, in the same process for the
/// demonstration. It subscribes to every custody line the dispenser
/// publishes and appends it, unchanged, to its own file per run. Because the
/// bytes are the same, TesseraSeal's verifier accepts the server's copy with
/// the same key, and the server can tell when the dispenser's chain and its
/// own copy disagree. A real station server would receive these over the
/// wire; the WebSocket at /ws/custody is that wire for any outside listener.
/// </summary>
public sealed class StationServer : IDisposable
{
    private readonly string _directory;
    private readonly byte[] _ikm;
    private readonly Lock _gate = new();
    private readonly List<Action<string>> _subscribers = [];
    private Simulation? _bound;
    private Action<string>? _handler;

    public StationServer(string directory, byte[] ikm)
    {
        _directory = directory;
        _ikm = ikm;
    }

    public string? CopyPath { get; private set; }
    public long LinesReceived { get; private set; }
    public string? RunId { get; private set; }

    /// <summary>Follow this simulation's custody stream. Called on every swap.</summary>
    public void Bind(Simulation sim)
    {
        lock (_gate)
        {
            if (_bound?.Custody is { } old && _handler is not null) old.Line -= _handler;
            _bound = sim;
            RunId = sim.RunId;
            LinesReceived = 0;
            CopyPath = Path.Combine(_directory, $"station-copy-{sim.RunId}.ndjson");
            if (sim.Custody is null) return;
            _handler = line =>
            {
                lock (_gate)
                {
                    File.AppendAllText(CopyPath!, line + "\n");
                    LinesReceived++;
                }
                Action<string>[] subs;
                lock (_gate) subs = _subscribers.ToArray();
                foreach (var s in subs) s(line);
            };
            // Replay first: the header and the recovery entries were written before the server bound.
            sim.Custody.SubscribeWithReplay(_handler);
        }
    }

    /// <summary>Outside listeners: the WebSocket topic hands each line on.</summary>
    public IDisposable Subscribe(Action<string> onLine)
    {
        lock (_gate) _subscribers.Add(onLine);
        return new Unsubscriber(this, onLine);
    }

    /// <summary>Run TesseraSeal's verifier over the server's own copy. The copy is closed between appends, so it reads directly.</summary>
    public (bool Ok, long? FailureAtSeq, string? Reason, int Lines) VerifyCopy()
    {
        lock (_gate)
        {
            if (CopyPath is null || !File.Exists(CopyPath)) return (false, null, "no station copy yet", 0);
            var ok = ImmutableAuditFileSink.VerifyChain(CopyPath, _ikm, out var seq, out var reason);
            return (ok, seq, reason, File.ReadLines(CopyPath).Count());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_bound?.Custody is { } c && _handler is not null) c.Line -= _handler;
        }
    }

    private sealed class Unsubscriber(StationServer owner, Action<string> onLine) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._subscribers.Remove(onLine); }
    }
}
