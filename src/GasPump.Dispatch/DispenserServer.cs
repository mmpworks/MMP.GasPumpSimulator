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
    string? RunId = null);

/// <summary>
/// The dispenser as its own process: a <see cref="Dispenser"/> behind a TCP
/// listener, with its own ledger, its own Herald log, its own TesseraSeal
/// chain, and its own ticker. One station at a time. On connect the station
/// receives the whole chain so far, then every line as it lands. Arm goes
/// through the authority and needs a valid token. The other station messages,
/// Stop included, are handled without one.
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
    private readonly IMailbox<(string? SaleId, PumpMessage M)> _box;
    private readonly ImmutableAuditFileSink? _custody;
    private readonly CustodyStream? _stream;
    private readonly string? _statePath;
    private StreamWriter? _client;
    private TcpClient? _tcp;
    private Action<string>? _custodyHandler;
    private string _lastSaleId = "-";
    private DispenserView? _lastView;
    private int _torndown;

    public DispenserServer(DispenserServerOptions options, TimeProvider clock, IDispatchStrategy dispatch)
    {
        _options = options;
        _clock = clock;
        _dispatch = dispatch;
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
            Send(new LinkFrame(LinkFrame.TagLedger, L: e));
            SaveState();
        };
        Log.Written += l => { if (l.Source != "ledger") Send(new LinkFrame(LinkFrame.TagLog, Log: WireLog.From(l))); };
        if (CustodyPath is not null)
        {
            _stream = new CustodyStream(CustodyPath);
            Log.Written += _ => _stream.Pump();
            Ledger.Appended += _ => _stream.Pump();
        }

        _box = dispatch.CreateMailbox<(string?, PumpMessage)>("pump", 1024);
        dispatch.StartWorker("pump", _box, Handle);
        dispatch.StartTicker("pump-tick", options.Dispenser.TickPeriod, _box, () => ((string?)null, (PumpMessage)new PumpMessage.Tick(options.Dispenser.TickPeriod)));

        _listener = new TcpListener(IPAddress.Loopback, options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "dispenser-accept" };
        _accept.Start();

        Log.Write("dispenser", "{PumpId} run {RunId} pid {Pid} listening on {Port}, custody {Custody}", ("PumpId", options.Dispenser.PumpId), ("RunId", RunId), ("Pid", Environment.ProcessId), ("Port", Port), ("Custody", CustodyPath));
        Ledger.Append("dispenser.start", $"{options.Dispenser.PumpId} run {RunId} pid {Environment.ProcessId} totalizer {Dispenser.Totalizer.Total}");
    }

    public string RunId { get; }
    public int Port { get; }
    public string? CustodyPath { get; }
    public Dispenser Dispenser { get; }
    public EventLedger Ledger { get; }
    public IEventLog Log { get; }
    public bool StationConnected { get { lock (_gate) return _client is not null; } }

    /// <summary>The station cut the power. The process that hosts this server should exit.</summary>
    public event Action? PoweredOff;

    private static bool IsEvidence(LogEvent e) => e.Category.Value is "ledger" or "attack" or "dispenser";

    private void Handle((string? SaleId, PumpMessage M) env)
    {
        if (env.SaleId is not null) _lastSaleId = env.SaleId;
        var events = Dispenser.Handle(env.M, env.SaleId ?? _lastSaleId);
        // A part broke or was fixed without a ledger entry (supply, pulser): the state file must still know.
        if (env.M is PumpMessage.Supply or PumpMessage.PulserDead or PumpMessage.DriveOff or PumpMessage.ResetBreakaway or PumpMessage.CloseForSales or PumpMessage.OpenForSales) SaveState();
        foreach (var e in events) Send(new LinkFrame(LinkFrame.TagEvent, E: e));
        var view = Dispenser.View(RunId, Environment.ProcessId, CustodyPath);
        // Every non-tick message and every tick that changed something. An idle tick sends nothing.
        if (env.M is not PumpMessage.Tick || events.Count > 0 || view != _lastView)
        {
            _lastView = view;
            Send(new LinkFrame(LinkFrame.TagView, V: view));
        }
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
                    LinkFrame? f;
                    try { f = LinkJson.Decode(line); }
                    catch (JsonException ex) { Log.Warn("dispenser", "bad frame from station: {Message}", ("Message", ex.Message)); continue; }
                    if (f is null) continue;
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
    private bool Receive(LinkFrame f)
    {
        switch (f.T)
        {
            case LinkFrame.TagHello when f.Hello is { } h:
                Log.Write("dispenser", "station {Id} pid {Pid} said hello", ("Id", h.Id), ("Pid", h.Pid));
                break;
            case LinkFrame.TagMsg when f.M is { } m:
                _box.Post((f.SaleId, m));
                break;
            case LinkFrame.TagPower when f.On == false:
                PowerOff();
                return false;
        }
        return true;
    }

    private void Attach(StreamWriter writer, TcpClient tcp)
    {
        lock (_gate)
        {
            _client = writer;
            _tcp = tcp;
            Send(new LinkFrame(LinkFrame.TagHello, Hello: new LinkHello("dispenser", _options.Dispenser.PumpId, RunId, Environment.ProcessId, CustodyPath)));
            // What this run already wrote before the station arrived: the ledger in full (the station de-duplicates by
            // run and sequence), the last stretch of the log. Then the whole chain so far, then live. SubscribeWithReplay
            // does the chain's replay and subscription under one lock, so no line is missed or doubled.
            foreach (var e in Ledger.Snapshot()) Send(new LinkFrame(LinkFrame.TagLedger, L: e));
            foreach (var l in Log.Snapshot().Where(l => l.Source != "ledger").TakeLast(200)) Send(new LinkFrame(LinkFrame.TagLog, Log: WireLog.From(l)));
            if (_stream is not null)
            {
                _custodyHandler = line => Send(new LinkFrame(LinkFrame.TagCustody, Line: line));
                _stream.SubscribeWithReplay(_custodyHandler);
            }
            Send(new LinkFrame(LinkFrame.TagView, V: Dispenser.View(RunId, Environment.ProcessId, CustodyPath)));
        }
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

    private void Send(LinkFrame f)
    {
        lock (_gate)
        {
            if (_client is null) return;
            try { _client.WriteLine(LinkJson.Encode(f)); _client.Flush(); }
            catch (IOException) { _client = null; }
            catch (ObjectDisposedException) { _client = null; }
        }
    }

    // ----- The small state the dispenser owns across its own restarts: the sealed counter and the burned nonces.

    /// <summary>Conditions lists the broken parts. Restarting the software repairs nothing on the island.</summary>
    private sealed record DispenserState(long TotalizerMilliGallons, List<string> Nonces, List<string>? Conditions = null);

    private void RestoreState()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        try
        {
            var s = JsonSerializer.Deserialize<DispenserState>(File.ReadAllText(_statePath));
            if (s is null) return;
            Dispenser.Totalizer.Restore(new Volume(s.TotalizerMilliGallons));
            Dispenser.Nonces.Import(s.Nonces);
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
        var s = new DispenserState(Dispenser.Totalizer.Total.MilliGallons, Dispenser.Nonces.Export().ToList(), Dispenser.ConditionCodes.ToList());
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
        _stream?.Pump();
        Shutdown();
        PoweredOff?.Invoke();
    }

    private void Shutdown()
    {
        _cts.Cancel();
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
