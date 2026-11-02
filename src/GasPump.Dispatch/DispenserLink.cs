using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GasPump.Core;
using GasPump.Security;

namespace GasPump.Dispatch;

/// <summary>Where the dispenser is. Null on <see cref="SimulationOptions.Dispenser"/> means in this process.</summary>
public sealed record DispenserLinkOptions(string Host, int Port);

/// <summary>
/// The seam between the station and the pump. The station posts
/// <see cref="PumpMessage"/>s and receives <see cref="SaleEvent"/>s, a view of
/// the pump for the screen, and the dispenser's custody lines. Two
/// implementations: a mailbox in this process, and a socket to another one.
/// The station code above this interface is the same for both.
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

    void Post(PumpMessage m, string saleId);
    /// <summary>The station cuts the dispenser's power. In-process this is a no-op; the process ends with the station's.</summary>
    void PowerOff();

    event Action<SaleEvent>? Event;
    event Action<DispenserView>? ViewChanged;
    event Action<bool>? ConnectionChanged;
    /// <summary>A log line written by a dispenser in another process. Never raised in-process, where the log is shared.</summary>
    event Action<LogLine>? RemoteLog;
    /// <summary>A ledger entry written by a dispenser in another process. Never raised in-process, where the ledger is shared.</summary>
    event Action<LedgerEntry>? RemoteLedger;

    /// <summary>Every custody line the dispenser has written, from the header on, then live. Carries the dispenser's run id so a copy can be filed per chain.</summary>
    IDisposable SubscribeCustody(Action<string, string> onLine);
}

/// <summary>
/// The dispenser in this process, behind a mailbox and a ticker. The pump
/// worker belongs to the dispatch strategy, so a threaded strategy gives it a
/// thread and a channel strategy gives it a task.
/// </summary>
public sealed class LocalDispenserLink : IDispenserLink
{
    private readonly Dispenser _dispenser;
    private readonly IMailbox<(string? SaleId, PumpMessage M)> _box;
    private readonly Func<string> _currentSaleId;
    private readonly CustodyStream? _custody;
    private readonly string _runId;
    private readonly string? _custodyPath;
    private long _sent, _received;

    public LocalDispenserLink(Dispenser dispenser, IDispatchStrategy dispatch, TimeSpan tickPeriod, Func<string> currentSaleId, CustodyStream? custody, string runId, string? custodyPath)
    {
        _dispenser = dispenser;
        _currentSaleId = currentSaleId;
        _custody = custody;
        _runId = runId;
        _custodyPath = custodyPath;
        _box = dispatch.CreateMailbox<(string?, PumpMessage)>("pump", 1024);
        dispatch.StartWorker("pump", _box, Handle);
        dispatch.StartTicker("pump-tick", tickPeriod, _box, () => ((string?)null, (PumpMessage)new PumpMessage.Tick(tickPeriod)));
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

    public event Action<SaleEvent>? Event;
    public event Action<DispenserView>? ViewChanged;
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public event Action<LogLine>? RemoteLog { add { } remove { } }
    public event Action<LedgerEntry>? RemoteLedger { add { } remove { } }

    /// <summary>The sale id is read when the message is handled, so a replaced sale is seen.</summary>
    public void Post(PumpMessage m, string saleId) { Interlocked.Increment(ref _sent); _box.Post((null, m)); }

    public void PowerOff() { }

    private void Handle((string? SaleId, PumpMessage M) env)
    {
        var events = _dispenser.Handle(env.M, env.SaleId ?? _currentSaleId());
        foreach (var e in events) { Interlocked.Increment(ref _received); Event?.Invoke(e); }
        if (env.M is not PumpMessage.Tick || events.Count > 0) ViewChanged?.Invoke(View);
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

/// <summary>What crosses the wire, one JSON object per line. Only one of the payload fields is set for a given tag.</summary>
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
    LinkHello? Hello = null)
{
    public const string TagHello = "hello", TagMsg = "msg", TagPower = "power", TagEvent = "event", TagView = "view", TagLog = "log", TagLedger = "ledger", TagCustody = "custody";
}

public sealed record LinkHello(string Role, string Id, string RunId, int Pid, string? CustodyPath);

/// <summary>A log line flattened for the wire: properties as strings.</summary>
public sealed record WireLog(DateTimeOffset At, string Source, string Message, LogSeverity Severity, Dictionary<string, string?> Props)
{
    public static WireLog From(LogLine l) => new(l.At, l.Source, l.Message, l.Severity, l.Props.ToDictionary(p => p.Key, p => p.Value?.ToString()));
    public LogLine ToLine() => new(At, Source, Message, Severity, Props.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)).ToArray());
}

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
/// drive a pump it can't reach and shouldn't pretend to. Custody lines are
/// kept so a late subscriber receives the whole chain.
/// </summary>
public sealed class SocketDispenserLink : IDispenserLink
{
    private readonly string _host;
    private readonly int _port;
    private readonly IEventLog _log;
    private readonly string _pumpId;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly List<(string RunId, string Line)> _custody = [];
    private readonly List<Action<string, string>> _custodySubscribers = [];
    private TcpClient? _client;
    private StreamWriter? _writer;
    private LinkHello? _hello;
    private DispenserView _view;
    private long _sent, _received, _dropped;
    private bool _connected;

    public SocketDispenserLink(DispenserLinkOptions where, string pumpId, IEventLog log)
    {
        _host = where.Host;
        _port = where.Port;
        _pumpId = pumpId;
        _log = log;
        _view = DispenserView.Offline(pumpId);
        _thread = new Thread(Run) { IsBackground = true, Name = "dispenser-link" };
        _thread.Start();
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

    public event Action<SaleEvent>? Event;
    public event Action<DispenserView>? ViewChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<LogLine>? RemoteLog;
    public event Action<LedgerEntry>? RemoteLedger;

    public void Post(PumpMessage m, string saleId) => Send(new LinkFrame(LinkFrame.TagMsg, SaleId: saleId, M: m));

    public void PowerOff()
    {
        Send(new LinkFrame(LinkFrame.TagPower, On: false));
        // Wait 50 ms so the frame can leave before the socket closes.
        Thread.Sleep(50);
        lock (_gate) { try { _writer?.Flush(); } catch (IOException) { } }
    }

    private void Send(LinkFrame f)
    {
        StreamWriter? w;
        lock (_gate) w = _connected ? _writer : null;
        if (w is null) { Interlocked.Increment(ref _dropped); return; }
        try
        {
            lock (w) { w.WriteLine(LinkJson.Encode(f)); w.Flush(); }
            Interlocked.Increment(ref _sent);
        }
        catch (IOException) { Interlocked.Increment(ref _dropped); }
        catch (ObjectDisposedException) { Interlocked.Increment(ref _dropped); }
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
                lock (_gate) { _client = client; _writer = writer; _connected = true; }
                announcedWaiting = false;
                Send(new LinkFrame(LinkFrame.TagHello, Hello: new LinkHello("station", _pumpId, "-", Environment.ProcessId, null)));
                _log.Write("link", "connected to dispenser at {Host}:{Port}", ("Host", _host), ("Port", _port));
                // ConnectionChanged(true) waits for the dispenser's hello, so subscribers see who answered.

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    Interlocked.Increment(ref _received);
                    LinkFrame? f;
                    try { f = LinkJson.Decode(line); }
                    catch (JsonException ex) { _log.Warn("link", "bad frame: {Message}", ("Message", ex.Message)); continue; }
                    if (f is not null) Receive(f);
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

    private void Receive(LinkFrame f)
    {
        switch (f.T)
        {
            case LinkFrame.TagHello when f.Hello is { } h:
                bool newRun;
                lock (_gate)
                {
                    newRun = _hello?.RunId != h.RunId;
                    _hello = h;
                    if (newRun) _custody.Clear();
                }
                _log.Write("link", "dispenser {PumpId} run {RunId} pid {Pid}, custody {Custody}", ("PumpId", h.Id), ("RunId", h.RunId), ("Pid", h.Pid), ("Custody", h.CustodyPath));
                ConnectionChanged?.Invoke(true);
                break;
            case LinkFrame.TagEvent when f.E is { } e:
                Event?.Invoke(e);
                break;
            case LinkFrame.TagView when f.V is { } v:
                lock (_gate) _view = v;
                ViewChanged?.Invoke(v);
                break;
            case LinkFrame.TagLog when f.Log is { } l:
                RemoteLog?.Invoke(l.ToLine());
                break;
            case LinkFrame.TagLedger when f.L is { } le:
                RemoteLedger?.Invoke(le);
                break;
            case LinkFrame.TagCustody when f.Line is { } line:
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
