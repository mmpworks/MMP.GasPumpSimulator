using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GasPump.Core;
using GasPump.Security;

namespace GasPump.Dispatch;

/// <summary>Where the dispenser is. Null on <see cref="SimulationOptions.Dispenser"/> means in this process.</summary>
public sealed record DispenserLinkOptions(string Host, int Port, TimeSpan? Heartbeat = null)
{
    public TimeSpan HeartbeatPeriod => Heartbeat ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// The seam between the station and the pump. The station posts
/// <see cref="PumpMessage"/>s and receives <see cref="SaleEvent"/>s, a view of
/// the pump for the screen, and the dispenser's custody lines. Two
/// implementations: a mailbox in this process, and a socket to another one.
/// The station code above this interface is the same for both.
///
/// A command that matters (arm, close, stop, clear) goes through
/// <see cref="Command"/> and is answered; the hand messages go through
/// <see cref="Post"/> and are not. Over the socket every line wears a
/// <see cref="WireFrame"/>: address, sequence, kind, correlation, time. The
/// link keeps the peer's heartbeat and says when the device goes silent
/// while the socket stays open, which is a different fault from the socket
/// closing.
/// </summary>
public interface IDispenserLink : IDisposable
{
    string Kind { get; }
    bool Connected { get; }
    int? RemotePid { get; }
    string? RemoteRunId { get; }
    string? RemoteCustodyPath { get; }
    long FramesSent { get; }
    long FramesReceived { get; }
    /// <summary>Messages posted and not yet handled by the pump worker. Zero over a socket, where the backlog is the other process's.</summary>
    int Backlog { get; }
    DispenserView View { get; }
    long CustodyLines { get; }

    /// <summary>The envelope's view of the wire. Zero in-process, where nothing is numbered.</summary>
    long LastSeqSent { get; }
    long LastSeqReceived { get; }
    long Gaps { get; }
    long Duplicates { get; }
    /// <summary>When the peer's last heartbeat arrived, and the one-word status it carried. Null until the first.</summary>
    DateTimeOffset? LastHeartbeatAt { get; }
    string? PeerStatus { get; }
    /// <summary>True while heartbeats arrive on time. In-process, always.</summary>
    bool PeerAliveNow { get; }

    /// <summary>Fire and forget: the driver's hands and the test hooks.</summary>
    void Post(PumpMessage m, string saleId);
    /// <summary>Ask and wait for an answer. Returns the correlation id the <see cref="Answered"/> event will carry.</summary>
    string Command(PumpMessage m, string saleId, TimeSpan ttl);
    /// <summary>Ask the island for the deliveries it still holds. Answered through <see cref="TransactionsReported"/>.</summary>
    void RequestTransactions();
    /// <summary>The station cuts the dispenser's power. In-process this is a no-op; the process ends with the station's.</summary>
    void PowerOff();

    event Action<SaleEvent>? Event;
    event Action<DispenserView>? ViewChanged;
    event Action<bool>? ConnectionChanged;
    event Action<WireAnswer>? Answered;
    event Action<IReadOnlyList<PumpTransaction>>? TransactionsReported;
    /// <summary>The heartbeat watchdog: false when the peer missed its beats with the socket still open, true when they return.</summary>
    event Action<bool>? PeerAlive;
    /// <summary>A sale event that arrived late, filling a gap the receiver had reported. Recorded, never applied to the sale.</summary>
    event Action<SaleEvent>? LateEvent;
    /// <summary>A log line written by a dispenser in another process. Never raised in-process, where the log is shared.</summary>
    event Action<LogLine>? RemoteLog;
    /// <summary>A ledger entry written by a dispenser in another process. Never raised in-process, where the ledger is shared.</summary>
    event Action<LedgerEntry>? RemoteLedger;

    /// <summary>Every custody line the dispenser has written, from the header on, then live. Carries the dispenser's run id so a copy can be filed per chain.</summary>
    IDisposable SubscribeCustody(Action<string, string> onLine);
}

/// <summary>Pump numbers on the wire come from the pump id: PUMP-07 is node 7. Anything else is node 1.</summary>
public static class PumpNodes
{
    public static WireNode Of(string pumpId)
    {
        var digits = new string(pumpId.Where(char.IsDigit).ToArray());
        return WireNode.Pump(int.TryParse(digits, out var n) && n > 0 ? n : 1);
    }
}

/// <summary>
/// The dispenser in this process, behind a mailbox and a ticker. The pump
/// worker belongs to the dispatch strategy, so a threaded strategy gives it a
/// thread and a channel strategy gives it a task. Commands are answered the
/// moment they are handled, so the station code sees the same shape as over
/// the socket.
/// </summary>
public sealed class LocalDispenserLink : IDispenserLink
{
    private readonly Dispenser _dispenser;
    private readonly IMailbox<PumpEnvelope> _box;
    private readonly Func<string> _currentSaleId;
    private readonly CustodyStream? _custody;
    private readonly string _runId;
    private readonly string? _custodyPath;
    private readonly TimeProvider _clock;
    private long _sent, _received;

    public LocalDispenserLink(Dispenser dispenser, IDispatchStrategy dispatch, TimeSpan tickPeriod, Func<string> currentSaleId, CustodyStream? custody, string runId, string? custodyPath, TimeProvider? clock = null)
    {
        _dispenser = dispenser;
        _currentSaleId = currentSaleId;
        _custody = custody;
        _runId = runId;
        _custodyPath = custodyPath;
        _clock = clock ?? TimeProvider.System;
        _box = dispatch.CreateMailbox<PumpEnvelope>("pump", 1024);
        dispatch.StartWorker("pump", _box, Handle);
        dispatch.StartTicker("pump-tick", tickPeriod, _box, () => new PumpEnvelope(null, new PumpMessage.Tick(tickPeriod)));
    }

    public string Kind => "in-process";
    public bool Connected => true;
    public int? RemotePid => Environment.ProcessId;
    public string? RemoteRunId => _runId;
    public string? RemoteCustodyPath => _custodyPath;
    public long FramesSent => Volatile.Read(ref _sent);
    public long FramesReceived => Volatile.Read(ref _received);
    public int Backlog => _box.Count;
    public DispenserView View => _dispenser.View(_runId, Environment.ProcessId, _custodyPath);
    public long CustodyLines => _custody?.LinesPublished ?? 0;
    public long LastSeqSent => 0;
    public long LastSeqReceived => 0;
    public long Gaps => 0;
    public long Duplicates => 0;
    public DateTimeOffset? LastHeartbeatAt => _clock.GetUtcNow();
    public string? PeerStatus => _dispenser.Pump.Phase.ToString();
    public bool PeerAliveNow => true;

    public event Action<SaleEvent>? Event;
    public event Action<DispenserView>? ViewChanged;
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public event Action<WireAnswer>? Answered;
    public event Action<IReadOnlyList<PumpTransaction>>? TransactionsReported;
    public event Action<bool>? PeerAlive { add { } remove { } }
    public event Action<SaleEvent>? LateEvent { add { } remove { } }
    public event Action<LogLine>? RemoteLog { add { } remove { } }
    public event Action<LedgerEntry>? RemoteLedger { add { } remove { } }

    /// <summary>The sale id is read when the message is handled, so a replaced sale is seen.</summary>
    public void Post(PumpMessage m, string saleId) { Interlocked.Increment(ref _sent); _box.Post(new PumpEnvelope(null, m)); }

    public string Command(PumpMessage m, string saleId, TimeSpan ttl)
    {
        var corr = Guid.NewGuid().ToString("N")[..8];
        Interlocked.Increment(ref _sent);
        _box.Post(new PumpEnvelope(null, m, corr));
        return corr;
    }

    public void RequestTransactions() => TransactionsReported?.Invoke(_dispenser.Pump.Transactions.Open);

    public void PowerOff() { }

    private void Handle(PumpEnvelope env)
    {
        var events = _dispenser.Handle(env.M, env.SaleId ?? _currentSaleId());
        foreach (var e in events) { Interlocked.Increment(ref _received); Event?.Invoke(e); }
        if (env.CorrelationId is { } corr) Answered?.Invoke(AnswerFor(corr, events));
        if (env.M is not PumpMessage.Tick || events.Count > 0) ViewChanged?.Invoke(View);
    }

    /// <summary>The answer a command earned: Refused or Busy when the pump said no, Done otherwise.</summary>
    internal static WireAnswer AnswerFor(string corr, IReadOnlyList<SaleEvent> events)
    {
        var refused = events.OfType<SaleEvent.PumpRefused>().FirstOrDefault();
        if (refused is null) return new WireAnswer(corr, AnswerCode.Done);
        return new WireAnswer(corr, refused.Reason == Pump.PumpController.TransactionPayable ? AnswerCode.Busy : AnswerCode.Refused, refused.Reason);
    }

    public IDisposable SubscribeCustody(Action<string, string> onLine)
    {
        if (_custody is null) return Nothing.Instance;
        Action<string> handler = line => onLine(_runId, line);
        _custody.SubscribeWithReplay(handler);
        return new Unsubscribe(() => _custody.Line -= handler);
    }

    /// <summary>The dispatch strategy owns the worker and the ticker, and disposes them with the simulation.</summary>
    public void Dispose() { }

    private sealed class Nothing : IDisposable { public static readonly Nothing Instance = new(); public void Dispose() { } }
}

/// <summary>The body of a frame, one JSON object per line. Only one of the payload fields is set for a given tag.</summary>
public sealed record LinkFrame(
    string T,
    string? SaleId = null,
    PumpMessage? M = null,
    SaleEvent? E = null,
    DispenserView? V = null,
    WireLog? Log = null,
    LedgerEntry? L = null,
    string? Line = null,
    bool? On = null,
    LinkHello? Hello = null,
    WireHeartbeat? Hb = null,
    WireAnswer? A = null,
    WireRead? Read = null,
    IReadOnlyList<PumpTransaction>? Tx = null)
{
    public const string TagHello = "hello", TagMsg = "msg", TagPower = "power", TagEvent = "event", TagView = "view", TagLog = "log", TagLedger = "ledger", TagCustody = "custody";
    public const string TagHeartbeat = "heartbeat", TagAnswer = "answer", TagRead = "read", TagTransactions = "transactions";
}

/// <summary>The first frame each way. LastSeqReceived tells the other side where to replay from; 0 on a fresh station.</summary>
public sealed record LinkHello(string Role, string Id, string RunId, int Pid, string? CustodyPath, long LastSeqReceived = 0);

/// <summary>A log line flattened for the wire: properties as strings.</summary>
public sealed record WireLog(DateTimeOffset At, string Source, string Message, LogSeverity Severity, Dictionary<string, string?> Props)
{
    public static WireLog From(LogLine l) => new(l.At, l.Source, l.Message, l.Severity, l.Props.ToDictionary(p => p.Key, p => p.Value?.ToString()));
    public LogLine ToLine() => new(At, Source, Message, Severity, Props.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).ToArray());
}

/// <summary>Body-only JSON, kept for the tests that check a body round-trips. The wire itself uses <see cref="FrameCodec"/>.</summary>
public static class LinkJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(LinkFrame f) => JsonSerializer.Serialize(f, Options);
    public static LinkFrame? Decode(string line) => JsonSerializer.Deserialize<LinkFrame>(line, Options);
}

/// <summary>
/// The dispenser in another process, over TCP. Reconnects on its own. While
/// disconnected, posts are dropped and counted, because the station can't
/// drive a pump it can't reach and shouldn't pretend to. Every line is a
/// numbered, addressed frame; a gap is asked for and filled; a late fill is
/// recorded and never applied to the sale. A heartbeat goes out once a
/// period and the peer's is watched. Custody lines are kept so a late
/// subscriber receives the whole chain.
/// </summary>
public sealed class SocketDispenserLink : IDispenserLink
{
    private readonly string _host;
    private readonly int _port;
    private readonly IEventLog _log;
    private readonly string _pumpId;
    private readonly TimeProvider _clock;
    private readonly WireNode _me = WireNode.Station;
    private readonly WireNode _peer;
    private readonly WireSequencer _seq;
    private readonly WireReceiver _recv = new();
    private readonly Liveness _live;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly ITimer _pulse;
    private readonly List<(string RunId, string Line)> _custody = [];
    private readonly List<Action<string, string>> _custodySubscribers = [];
    private TcpClient? _client;
    private StreamWriter? _writer;
    private LinkHello? _hello;
    private DispenserView _view;
    private long _sent, _received, _dropped;
    private bool _connected;
    private bool _peerAlive = true;

    public SocketDispenserLink(DispenserLinkOptions where, string pumpId, IEventLog log, TimeProvider? clock = null)
    {
        _host = where.Host;
        _port = where.Port;
        _pumpId = pumpId;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _peer = PumpNodes.Of(pumpId);
        _seq = new WireSequencer(_me);
        _live = new Liveness(where.HeartbeatPeriod);
        _view = DispenserView.Offline(pumpId);
        _thread = new Thread(Run) { IsBackground = true, Name = "dispenser-link" };
        _thread.Start();
        _pulse = _clock.CreateTimer(_ => Pulse(), null, where.HeartbeatPeriod, where.HeartbeatPeriod);
    }

    public string Kind => "socket";
    public bool Connected { get { lock (_gate) return _connected; } }
    public int? RemotePid { get { lock (_gate) return _hello?.Pid; } }
    public string? RemoteRunId { get { lock (_gate) return _hello?.RunId; } }
    public string? RemoteCustodyPath { get { lock (_gate) return _hello?.CustodyPath; } }
    public long FramesSent => Volatile.Read(ref _sent);
    public long FramesReceived => Volatile.Read(ref _received);
    public long FramesDropped => Volatile.Read(ref _dropped);
    public int Backlog => 0;
    public DispenserView View { get { lock (_gate) return _view; } }
    public long CustodyLines { get { lock (_gate) return _custody.Count; } }
    public long LastSeqSent => _seq.LastSeq;
    public long LastSeqReceived => _recv.LastSeq;
    public long Gaps => _recv.Gaps;
    public long Duplicates => _recv.Duplicates;
    public DateTimeOffset? LastHeartbeatAt => _live.LastBeatAt;
    public string? PeerStatus => _live.Last?.Status;
    public bool PeerAliveNow { get { lock (_gate) return _peerAlive; } }

    public event Action<SaleEvent>? Event;
    public event Action<DispenserView>? ViewChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<WireAnswer>? Answered;
    public event Action<IReadOnlyList<PumpTransaction>>? TransactionsReported;
    public event Action<bool>? PeerAlive;
    public event Action<SaleEvent>? LateEvent;
    public event Action<LogLine>? RemoteLog;
    public event Action<LedgerEntry>? RemoteLedger;

    public void Post(PumpMessage m, string saleId) => Send(new LinkFrame(LinkFrame.TagMsg, SaleId: saleId, M: m), WireKind.Command);

    public string Command(PumpMessage m, string saleId, TimeSpan ttl)
    {
        var corr = Guid.NewGuid().ToString("N")[..8];
        Send(new LinkFrame(LinkFrame.TagMsg, SaleId: saleId, M: m), WireKind.Command, corr, ttl);
        return corr;
    }

    public void RequestTransactions() => Send(new LinkFrame(LinkFrame.TagRead, Read: new WireRead(WireRead.Transactions)), WireKind.Read);

    public void PowerOff()
    {
        Send(new LinkFrame(LinkFrame.TagPower, On: false), WireKind.Command, ttl: TimeSpan.Zero);
        // Wait 50 ms so the frame can leave before the socket closes.
        Thread.Sleep(50);
        lock (_gate) { try { _writer?.Flush(); } catch (IOException) { } }
    }

    private void Send(LinkFrame body, WireKind kind, string? correlationId = null, TimeSpan? ttl = null)
    {
        var dest = kind == WireKind.Heartbeat ? WireNode.Broadcast : _peer;
        _seq.Send(kind, dest, body, _clock.GetUtcNow(), Write, correlationId, ttl);
    }

    /// <summary>True when the frame went out. False gives its number back to the sequencer.</summary>
    private bool Write(WireFrame f)
    {
        StreamWriter? w;
        lock (_gate) w = _connected ? _writer : null;
        if (w is null) { Interlocked.Increment(ref _dropped); return false; }
        try
        {
            lock (w) { w.WriteLine(FrameCodec.Encode(f)); w.Flush(); }
            Interlocked.Increment(ref _sent);
            return true;
        }
        catch (IOException) { Interlocked.Increment(ref _dropped); return false; }
        catch (ObjectDisposedException) { Interlocked.Increment(ref _dropped); return false; }
    }

    /// <summary>Once a period: send our heartbeat, and judge the peer's.</summary>
    private void Pulse()
    {
        if (!Connected) return;
        Send(new LinkFrame(LinkFrame.TagHeartbeat, Hb: new WireHeartbeat("-", "station", _seq.LastSeq, _recv.LastSeq)), WireKind.Heartbeat);
        var alive = _live.IsAlive(_clock.GetUtcNow());
        bool changed;
        lock (_gate) { changed = alive != _peerAlive; _peerAlive = alive; }
        if (!changed) return;
        if (alive) _log.Write("link", "dispenser heartbeats are back");
        else _log.Error("link", "dispenser silent: no heartbeat for {Window} with the socket open", ("Window", _live.Window));
        PeerAlive?.Invoke(alive);
    }

    private void Run()
    {
        var announcedWaiting = false;
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient { NoDelay = true };
                client.Connect(_host, _port);
                var stream = client.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                var reader = new StreamReader(stream, Encoding.UTF8);
                lock (_gate) { _client = client; _writer = writer; _connected = true; _peerAlive = true; }
                _live.Reset();
                announcedWaiting = false;
                Send(new LinkFrame(LinkFrame.TagHello, Hello: new LinkHello("station", _pumpId, "-", Environment.ProcessId, null, _recv.LastSeq)), WireKind.Event);
                _log.Write("link", "connected to dispenser at {Host}:{Port}", ("Host", _host), ("Port", _port));
                // ConnectionChanged(true) waits for the dispenser's hello, so subscribers see who answered.

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    Interlocked.Increment(ref _received);
                    var (f, fault, detail) = FrameCodec.Decode(line, _me);
                    if (f is null) { _log.Warn("link", "frame refused: {Fault} {Detail}", ("Fault", fault), ("Detail", detail)); continue; }
                    Receive(f);
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                bool wasConnected;
                lock (_gate)
                {
                    wasConnected = _connected;
                    _connected = false;
                    _writer = null;
                    _client = null;
                    if (wasConnected) _view = DispenserView.Offline(_pumpId);
                }
                client?.Dispose();
                if (wasConnected)
                {
                    _log.Error("link", "dispenser link lost ({Host}:{Port})", ("Host", _host), ("Port", _port));
                    ViewChanged?.Invoke(View);
                    ConnectionChanged?.Invoke(false);
                }
                else if (!announcedWaiting && !_cts.IsCancellationRequested)
                {
                    _log.Warn("link", "waiting for a dispenser at {Host}:{Port}", ("Host", _host), ("Port", _port));
                    announcedWaiting = true;
                }
            }
            if (!_cts.IsCancellationRequested) _cts.Token.WaitHandle.WaitOne(400);
        }
    }

    private void Receive(WireFrame f)
    {
        // A hello from a new dispenser run restarts its numbering; check that before the sequence rule runs.
        if (f.Body is { T: LinkFrame.TagHello, Hello: { } h })
        {
            bool newRun;
            lock (_gate)
            {
                newRun = _hello?.RunId != h.RunId;
                _hello = h;
                if (newRun) _custody.Clear();
            }
            if (newRun) _recv.Reset();
            _recv.Check(f);
            _log.Write("link", "dispenser {PumpId} run {RunId} pid {Pid}, custody {Custody}", ("PumpId", h.Id), ("RunId", h.RunId), ("Pid", h.Pid), ("Custody", h.CustodyPath));
            ConnectionChanged?.Invoke(true);
            return;
        }

        var (verdict, from, to) = _recv.Check(f);
        switch (verdict)
        {
            case Accept.Duplicate:
                return;
            case Accept.Gap:
                _log.Warn("link", "gap in dispenser frames {From}..{To}, asking for them", ("From", from), ("To", to));
                Send(new LinkFrame(LinkFrame.TagRead, Read: new WireRead(WireRead.Frames, from, to)), WireKind.Read);
                break;
            case Accept.Filled:
                // A late fill. Records are welcome; a sale event is out of order and must not move the sale.
                if (f.Body is { T: LinkFrame.TagEvent, E: { } late }) { LateEvent?.Invoke(late); return; }
                break;
        }

        switch (f.Kind)
        {
            case WireKind.Heartbeat when f.Body.Hb is { } hb:
                _live.Beat(hb, _clock.GetUtcNow());
                return;
            case WireKind.Answer when f.Body.A is { } a:
                Answered?.Invoke(a);
                return;
            case WireKind.Read when f.Body.Read is { What: WireRead.Frames, FromSeq: { } rf }:
                foreach (var old in _seq.Replay(rf, f.Body.Read.ToSeq)) _ = Write(old);
                return;
        }

        switch (f.Body.T)
        {
            case LinkFrame.TagEvent when f.Body.E is { } e:
                Event?.Invoke(e);
                break;
            case LinkFrame.TagView when f.Body.V is { } v:
                lock (_gate) _view = v;
                ViewChanged?.Invoke(v);
                break;
            case LinkFrame.TagLog when f.Body.Log is { } l:
                RemoteLog?.Invoke(l.ToLine());
                break;
            case LinkFrame.TagLedger when f.Body.L is { } le:
                RemoteLedger?.Invoke(le);
                break;
            case LinkFrame.TagTransactions when f.Body.Tx is { } tx:
                TransactionsReported?.Invoke(tx);
                break;
            case LinkFrame.TagCustody when f.Body.Line is { } line:
                Action<string, string>[] subs;
                string runId;
                lock (_gate)
                {
                    runId = _hello?.RunId ?? "-";
                    _custody.Add((runId, line));
                    subs = _custodySubscribers.ToArray();
                }
                foreach (var s in subs) s(runId, line);
                break;
        }
    }

    public IDisposable SubscribeCustody(Action<string, string> onLine)
    {
        lock (_gate)
        {
            foreach (var (runId, line) in _custody) onLine(runId, line);
            _custodySubscribers.Add(onLine);
        }
        return new Unsubscribe(() => { lock (_gate) _custodySubscribers.Remove(onLine); });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pulse.Dispose();
        lock (_gate) { _client?.Dispose(); _client = null; _writer = null; _connected = false; }
        _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}

internal sealed class Unsubscribe(Action dispose) : IDisposable
{
    private int _done;
    public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) dispose(); }
}
