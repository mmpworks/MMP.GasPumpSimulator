using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GasPump.Core;
using GasPump.Security;
using MMP.Herald.Compliance.Audit;
using MMP.Herald.Events;

namespace GasPump.Dispatch;

/// <summary>Builds the dispenser process: what pump, which port, where its files go, and the site key both sides share.</summary>
public sealed record DispenserServerOptions(
    DispenserOptions Dispenser,
    int Port,
    /// <summary>Where the dispenser keeps its own custody chain and its small state file. Null keeps everything in memory.</summary>
    string? Directory,
    /// <summary>The site custody key. The token key is derived from it under the same label the station uses.</summary>
    byte[] Ikm,
    Action<MMP.Herald.Quick.QuickLogBuilder>? LogSinks = null,
    string? RunId = null,
    TimeSpan? Heartbeat = null)
{
    public TimeSpan HeartbeatPeriod => Heartbeat ?? TimeSpan.FromSeconds(1);
}

/// <summary>
/// The dispenser as its own process: a <see cref="Dispenser"/> behind a TCP
/// listener, with its own ledger, its own Herald log, its own TesseraSeal
/// chain, and its own ticker. One station at a time. Every line out is a
/// numbered frame; a gap the station reports is filled from the ring; a
/// command with a correlation id gets an answer; a command past its time to
/// live is answered Stale and never run. On connect the station receives the
/// whole chain so far, then every line as it lands. Arm goes through the
/// authority and needs a valid token. The other station messages, Stop
/// included, are handled without one.
/// </summary>
public sealed class DispenserServer : IDisposable
{
    private readonly DispenserServerOptions _options;
    private readonly TimeProvider _clock;
    private readonly IDispatchStrategy _dispatch;
    private readonly TcpListener _listener;
    private readonly Thread _accept;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly IMailbox<PumpEnvelope> _box;
    private readonly ImmutableAuditFileSink? _custody;
    private readonly CustodyStream? _stream;
    private readonly string? _statePath;
    private readonly WireNode _me;
    private readonly WireNode _peer = WireNode.Station;
    private readonly WireSequencer _seq;
    private readonly WireReceiver _recv = new();
    private readonly Liveness _live;
    private readonly ITimer _pulse;
    private StreamWriter? _client;
    private TcpClient? _tcp;
    private Action<string>? _custodyHandler;
    private string _lastSaleId = "-";
    private DispenserView? _lastView;
    private bool _stationAlive = true;
    private volatile bool _heartbeatsPaused;
    private int _torndown;

    public DispenserServer(DispenserServerOptions options, TimeProvider clock, IDispatchStrategy dispatch)
    {
        _options = options;
        _clock = clock;
        _dispatch = dispatch;
        _me = PumpNodes.Of(options.Dispenser.PumpId);
        _seq = new WireSequencer(_me);
        _live = new Liveness(options.HeartbeatPeriod);
        RunId = options.RunId ?? $"DSP-{clock.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        if (options.Directory is { } dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            CustodyPath = Path.Combine(dir, $"dispenser-custody-{RunId}.ndjson");
            _custody = new ImmutableAuditFileSink(CustodyPath, options.Dispenser.PumpId, RunId, options.Ikm);
            _statePath = Path.Combine(dir, $"dispenser-state-{options.Dispenser.PumpId}.json");
        }

        Log = new HeraldEventLog(clock, options.LogSinks, _custody is null ? null : (_custody, IsEvidence));
        Ledger = new EventLedger(clock);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, options.Ikm, 32, info: Encoding.UTF8.GetBytes(CustodyKeyFile.TokenKeyLabel));
        Dispenser = new Dispenser(options.Dispenser, key, Ledger, Log, clock);
        RestoreState();

        Ledger.Appended += e =>
        {
            Log.Write("ledger", "#{Seq} {Kind} {Detail}", ("Seq", e.Seq), ("Kind", e.Kind), ("Detail", e.Detail), ("Hash", e.Hash), ("Prev", e.PrevHash));
            Send(new LinkFrame(LinkFrame.TagLedger, L: e), WireKind.Event);
            SaveState();
        };
        Log.Written += l => { if (l.Source != "ledger") Send(new LinkFrame(LinkFrame.TagLog, Log: WireLog.From(l)), WireKind.Event); };
        if (CustodyPath is not null)
        {
            _stream = new CustodyStream(CustodyPath);
            Log.Written += _ => _stream.Pump();
            Ledger.Appended += _ => _stream.Pump();
        }

        _box = dispatch.CreateMailbox<PumpEnvelope>("pump", 1024);
        dispatch.StartWorker("pump", _box, Handle);
        dispatch.StartTicker("pump-tick", options.Dispenser.TickPeriod, _box, () => new PumpEnvelope(null, new PumpMessage.Tick(options.Dispenser.TickPeriod)));

        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "dispenser-accept" };
        _accept.Start();
        _pulse = clock.CreateTimer(_ => Pulse(), null, options.HeartbeatPeriod, options.HeartbeatPeriod);

        Log.Write("dispenser", "{PumpId} run {RunId} pid {Pid} listening on {Port} as node {Node}, custody {Custody}", ("PumpId", options.Dispenser.PumpId), ("RunId", RunId), ("Pid", Environment.ProcessId), ("Port", Port), ("Node", _me.ToString()), ("Custody", CustodyPath));
        Ledger.Append("dispenser.start", $"{options.Dispenser.PumpId} run {RunId} pid {Environment.ProcessId} totalizer {Dispenser.Totalizer.Total}");
    }

    public string RunId { get; }
    public int Port { get; }
    public string? CustodyPath { get; }
    public Dispenser Dispenser { get; }
    public EventLedger Ledger { get; }
    public IEventLog Log { get; }
    public bool StationConnected { get { lock (_gate) return _client is not null; } }
    public long LastSeqSent => _seq.LastSeq;
    public long LastSeqReceived => _recv.LastSeq;
    public DateTimeOffset? StationHeartbeatAt => _live.LastBeatAt;
    public long Answers { get; private set; }
    public long StaleCommands { get; private set; }

    /// <summary>Test hook: stop sending heartbeats while the socket stays open, so the station's watchdog can be exercised.</summary>
    public bool HeartbeatsPaused { get => _heartbeatsPaused; set => _heartbeatsPaused = value; }

    /// <summary>Test hook: swallow every command from the station without running or answering it, so the station's command deadline can be exercised.</summary>
    public bool CommandsDropped { get => _commandsDropped; set => _commandsDropped = value; }
    private volatile bool _commandsDropped;

    /// <summary>The station cut the power. The process that hosts this server should exit.</summary>
    public event Action? PoweredOff;

    private static bool IsEvidence(LogEvent e) => e.Category.Value is "ledger" or "attack" or "dispenser";

    private void Handle(PumpEnvelope env)
    {
        if (env.SaleId is not null) _lastSaleId = env.SaleId;
        var events = Dispenser.Handle(env.M, env.SaleId ?? _lastSaleId);
        // A part broke or was fixed without a ledger entry (supply, pulser): the state file must still know.
        if (env.M is PumpMessage.Supply or PumpMessage.PulserDead or PumpMessage.DriveOff or PumpMessage.ResetBreakaway or PumpMessage.CloseForSales or PumpMessage.OpenForSales or PumpMessage.Lock or PumpMessage.Clear or PumpMessage.SetPrices) SaveState();
        foreach (var e in events) Send(new LinkFrame(LinkFrame.TagEvent, E: e), WireKind.Event);
        if (env.CorrelationId is { } corr)
        {
            var answer = LocalDispenserLink.AnswerFor(corr, events, Dispenser.LastRefusal, Dispenser.LastNote);
            Answers++;
            Send(new LinkFrame(LinkFrame.TagAnswer, A: answer), WireKind.Answer, corr);
        }
        var view = Dispenser.View(RunId, Environment.ProcessId, CustodyPath);
        // Every non-tick message and every tick that changed something. An idle tick sends nothing.
        if (env.M is not PumpMessage.Tick || events.Count > 0 || view != _lastView)
        {
            _lastView = view;
            Send(new LinkFrame(LinkFrame.TagView, V: view), WireKind.Event);
        }
    }

    /// <summary>Once a period: our heartbeat out (unless a test paused it), and a note when the station goes quiet or comes back.</summary>
    private void Pulse()
    {
        if (!StationConnected) return;
        if (!_heartbeatsPaused)
            Send(new LinkFrame(LinkFrame.TagHeartbeat, Hb: new WireHeartbeat(RunId, Dispenser.Pump.Phase.ToString(), _seq.LastSeq, _recv.LastSeq)), WireKind.Heartbeat);
        var alive = _live.IsAlive(_clock.GetUtcNow());
        bool changed;
        lock (_gate) { changed = alive != _stationAlive; _stationAlive = alive; }
        if (!changed) return;
        if (alive) Log.Write("dispenser", "station heartbeats are back");
        else Log.Warn("dispenser", "station silent: no heartbeat for {Window}; the pump keeps its own limits", ("Window", _live.Window));
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = _listener.AcceptTcpClient();
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
                var reader = new StreamReader(stream, Encoding.UTF8);
                Attach(writer, tcp);
                Log.Write("dispenser", "station connected from {Remote}", ("Remote", tcp.Client.RemoteEndPoint?.ToString()));
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var (f, fault, detail) = FrameCodec.Decode(line, _me);
                    if (f is null) { Log.Warn("dispenser", "frame from station refused: {Fault} {Detail}", ("Fault", fault), ("Detail", detail)); continue; }
                    if (!Receive(f)) break;
                }
            }
            catch (SocketException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                Detach();
                tcp?.Dispose();
                if (!_cts.IsCancellationRequested) Log.Warn("dispenser", "station disconnected; the pump keeps its own limits");
            }
        }
    }

    /// <summary>Returns false when the station told us the power is off.</summary>
    private bool Receive(WireFrame f)
    {
        if (f.Body is { T: LinkFrame.TagHello, Hello: { } h })
        {
            // A new station process numbers from one again.
            _recv.Reset();
            _recv.Check(f);
            _live.Reset();
            lock (_gate) _stationAlive = true;
            Log.Write("dispenser", "station {Id} pid {Pid} said hello, last seen our frame {LastSeq}", ("Id", h.Id), ("Pid", h.Pid), ("LastSeq", h.LastSeqReceived));
            return true;
        }

        var (verdict, from, to) = _recv.Check(f);
        if (verdict == Accept.Duplicate) return true;
        if (verdict == Accept.Gap)
        {
            Log.Warn("dispenser", "gap in station frames {From}..{To}, asking for them", ("From", from), ("To", to));
            Send(new LinkFrame(LinkFrame.TagRead, Read: new WireRead(WireRead.Frames, from, to)), WireKind.Read);
        }

        switch (f.Kind)
        {
            case WireKind.Heartbeat when f.Body.Hb is { } hb:
                _live.Beat(hb, _clock.GetUtcNow());
                return true;
            case WireKind.Read when f.Body.Read is { } r:
                if (r.What == WireRead.Frames && r.FromSeq is { } rf) foreach (var old in _seq.Replay(rf, r.ToSeq)) _ = Write(old);
                else if (r.What == WireRead.Transactions) Send(new LinkFrame(LinkFrame.TagTransactions, Tx: Dispenser.Pump.Transactions.Open), WireKind.Event);
                else if (r.What == WireRead.Totals) Send(new LinkFrame(LinkFrame.TagTotals, Totals: Dispenser.Pump.ReadTotals(_clock.GetUtcNow())), WireKind.Event);
                return true;
        }

        switch (f.Body.T)
        {
            case LinkFrame.TagMsg when f.Body.M is { } m:
                if (_commandsDropped) { Log.Warn("dispenser", "test hook dropped {Message}", ("Message", m.GetType().Name)); return true; }
                if (f.IsStale(_clock.GetUtcNow()))
                {
                    // Too old to act on. Say so if an answer was wanted, and never run it.
                    StaleCommands++;
                    Log.Warn("dispenser", "{Message} arrived {Age} after it was sent, past its {Ttl}; refused as stale", ("Message", m.GetType().Name), ("Age", _clock.GetUtcNow() - f.SentAt), ("Ttl", f.Ttl));
                    if (f.CorrelationId is { } sc) { Answers++; Send(new LinkFrame(LinkFrame.TagAnswer, A: new WireAnswer(sc, AnswerCode.Stale, $"{m.GetType().Name} older than {f.Ttl}")), WireKind.Answer, sc); }
                    return true;
                }
                _box.Post(new PumpEnvelope(f.Body.SaleId, m, f.CorrelationId));
                break;
            case LinkFrame.TagPower when f.Body.On == false:
                PowerOff();
                return false;
        }
        return true;
    }

    /// <summary>
    /// A station is on the line. Sends happen outside <c>_gate</c>: Send takes
    /// the sequencer's lock and then <c>_gate</c> inside Write, so holding
    /// <c>_gate</c> here while the heartbeat timer sends would deadlock. Each
    /// frame is written whole under one lock anyway, so interleaving with a
    /// live entry is harmless: the station de-duplicates ledger entries by run
    /// and sequence, and the chain replay has its own lock.
    /// </summary>
    private void Attach(StreamWriter writer, TcpClient tcp)
    {
        lock (_gate)
        {
            _client = writer;
            _tcp = tcp;
        }
        Send(new LinkFrame(LinkFrame.TagHello, Hello: new LinkHello("dispenser", _options.Dispenser.PumpId, RunId, Environment.ProcessId, CustodyPath, _recv.LastSeq)), WireKind.Event);
        // What this run already wrote before the station arrived: the ledger in full, the last stretch of the log, then
        // the whole chain so far and then live. SubscribeWithReplay does the chain's replay and subscription under one
        // lock, so no line is missed or doubled.
        foreach (var e in Ledger.Snapshot()) Send(new LinkFrame(LinkFrame.TagLedger, L: e), WireKind.Event);
        foreach (var l in Log.Snapshot().Where(l => l.Source != "ledger").TakeLast(200)) Send(new LinkFrame(LinkFrame.TagLog, Log: WireLog.From(l)), WireKind.Event);
        if (_stream is not null)
        {
            Action<string> handler = line => Send(new LinkFrame(LinkFrame.TagCustody, Line: line), WireKind.Event);
            lock (_gate) _custodyHandler = handler;
            _stream.SubscribeWithReplay(handler);
        }
        Send(new LinkFrame(LinkFrame.TagView, V: Dispenser.View(RunId, Environment.ProcessId, CustodyPath)), WireKind.Event);
    }

    private void Detach()
    {
        lock (_gate)
        {
            if (_stream is not null && _custodyHandler is not null) _stream.Line -= _custodyHandler;
            _custodyHandler = null;
            _client = null;
            // A crash or a power-off ends the connection too, so the station sees the link drop at once.
            _tcp?.Dispose();
            _tcp = null;
        }
    }

    private void Send(LinkFrame body, WireKind kind, string? correlationId = null)
    {
        var dest = kind == WireKind.Heartbeat ? WireNode.Broadcast : _peer;
        _seq.Send(kind, dest, body, _clock.GetUtcNow(), Write, correlationId);
    }

    /// <summary>True when the frame went out. False gives its number back to the sequencer, so a station that connects later sees no gap.</summary>
    private bool Write(WireFrame f)
    {
        lock (_gate)
        {
            if (_client is null) return false;
            try { _client.WriteLine(FrameCodec.Encode(f)); _client.Flush(); return true; }
            catch (IOException) { _client = null; return false; }
            catch (ObjectDisposedException) { _client = null; return false; }
        }
    }

    // ----- The small state the dispenser owns across its own restarts: the sealed counter, the burned nonces, the broken parts, the unpaid deliveries.

    /// <summary>Conditions lists the broken parts. Transactions are the deliveries not yet cleared by a station. Prices is the board the station last wrote. Restarting the software repairs nothing, forgets no money, and keeps its prices.</summary>
    private sealed record DispenserState(long TotalizerMilliGallons, List<string> Nonces, List<string>? Conditions = null, long TxSeq = 0, List<PumpTransaction>? Transactions = null,
        Dictionary<Grade, long>? Prices = null, List<GradeTotal>? GradeTotals = null);

    private void RestoreState()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        try
        {
            var s = JsonSerializer.Deserialize<DispenserState>(File.ReadAllText(_statePath));
            if (s is null) return;
            Dispenser.Totalizer.Restore(new Volume(s.TotalizerMilliGallons));
            Dispenser.Nonces.Import(s.Nonces);
            if (s.GradeTotals is { Count: > 0 } gt) Dispenser.Pump.GradeTotals.Restore(gt);
            if (s.Prices is { Count: > 0 } pr)
            {
                var board = new Pricing(pr);
                if (board.Validate().Ok) { Dispenser.Pump.RestorePrices(board); Log.Write("dispenser", "prices from the state file: {Board}", ("Board", board.Describe())); }
                else Log.Warn("dispenser", "state file prices unusable, keeping the built-in board: {Reason}", ("Reason", board.Validate().Reason));
            }
            if (s.Transactions is { Count: > 0 } || s.TxSeq > 0)
            {
                Dispenser.Pump.Transactions.Restore(s.TxSeq, s.Transactions ?? []);
                var open = Dispenser.Pump.Transactions.Open;
                if (open.Count > 0) Log.Warn("dispenser", "restarted holding {Count} unpaid delivery(s): {Tx}", ("Count", open.Count), ("Tx", string.Join(", ", open.Select(t => $"#{t.TxSeq} {t.SaleId} {t.Money}"))));
            }
            if (s.Conditions is { Count: > 0 } c)
            {
                Dispenser.RestoreConditions(c);
                Log.Warn("dispenser", "restarted with parts still broken: {Service}", ("Service", Dispenser.Pump.Service.ToString()));
            }
        }
        catch (JsonException ex) { Log.Error("dispenser", "state file unreadable: {Message}", ("Message", ex.Message)); }
    }

    private void SaveState()
    {
        if (_statePath is null) return;
        var (txSeq, txItems) = Dispenser.Pump.Transactions.Export();
        var prices = (Dispenser.Pump.Prices as Pricing)?.TenthCentsPerGallon.ToDictionary(kv => kv.Key, kv => kv.Value);
        var s = new DispenserState(Dispenser.Totalizer.Total.MilliGallons, Dispenser.Nonces.Export().ToList(), Dispenser.ConditionCodes.ToList(), txSeq, txItems.ToList(),
            prices, Dispenser.Pump.GradeTotals.Snapshot().ToList());
        var tmp = _statePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (IOException ex) { Log.Error("dispenser", "state save failed: {Message}", ("Message", ex.Message)); }
    }

    /// <summary>
    /// The power is gone. Record it, close the chain, and stop the pump worker.
    /// Losing power has already shut the valve, so here the ticker stops and
    /// nothing touches the hydraulics again.
    /// </summary>
    public void PowerOff()
    {
        if (Interlocked.Exchange(ref _torndown, 1) == 1) return;
        Log.Error("dispenser", "power removed by the station; pump {Phase} at {Volume} {Money}", ("Phase", Dispenser.Pump.Phase), ("Volume", Dispenser.Pump.Volume), ("Money", Dispenser.Pump.Money));
        Ledger.Append("dispenser.poweroff", $"{_options.Dispenser.PumpId} run {RunId} pump {Dispenser.Pump.Phase} {Dispenser.Pump.Volume} {Dispenser.Pump.Money} totalizer {Dispenser.Totalizer.Total}");
        // The delivery so far goes to the buffer, and the buffer to disk, before the process ends.
        Dispenser.Pump.PowerLost();
        SaveState();
        _stream?.Pump();
        Shutdown();
        PoweredOff?.Invoke();
    }

    private void Shutdown()
    {
        _cts.Cancel();
        _pulse.Dispose();
        try { _listener.Stop(); } catch (SocketException) { }
        Detach();
        _dispatch.Dispose();
        if (Log is IDisposable d) d.Dispose();
        _custody?.Dispose();
    }

    /// <summary>A crash: no record, no close-out. The chain file is left as the sink left it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _torndown, 1) == 1) return;
        Shutdown();
    }
}

/// <summary>The site custody key file both processes read, and the label both derive the token key under.</summary>
public static class CustodyKeyFile
{
    public const string TokenKeyLabel = "gaspump.token-key.v1";

    /// <summary>Uses GASPUMP_CUSTODY_IKM_HEX if it is set. Otherwise reads custody.ikm from the directory. Otherwise writes a new random key there.</summary>
    public static byte[] Load(string directory)
    {
        if (Environment.GetEnvironmentVariable("GASPUMP_CUSTODY_IKM_HEX") is { Length: >= 64 } hex) return Convert.FromHexString(hex);
        var path = Path.Combine(directory, "custody.ikm");
        if (File.Exists(path)) return Convert.FromHexString(File.ReadAllText(path).Trim());
        var ikm = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToHexString(ikm));
        return ikm;
    }

    public static byte[] TokenKey(byte[] ikm) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, info: Encoding.UTF8.GetBytes(TokenKeyLabel));
}
