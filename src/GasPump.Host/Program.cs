using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Host;
using GasPump.Payment;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5177");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// --strategy threaded|channel|asyncproxy. Threaded is the default so the "old way" is what you see first.
// --dispenser socket|inproc. Socket is the default: the pump runs in GasPump.Dispenser.exe and this process is the station.
// --dispenser-port 5178.
string Arg(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
var strategyName = Arg("--strategy", "threaded");
var dispenserMode = Arg("--dispenser", "socket");
var dispenserPort = int.Parse(Arg("--dispenser-port", "5178"));
var clock = TimeProvider.System;

// One directory holds the files of both processes: the station's Herald log (gaspump-*.ndjson), the state journal
// (state.json, rewritten every second), the station's TesseraSeal chain (custody-RUN-*.ndjson), the dispenser's
// log (dispenser-*.ndjson), chain (dispenser-custody-DSP-*.ndjson), state (dispenser-state-PUMP-07.json), and the
// station's copy of the dispenser's chain (station-copy-*.ndjson). GASPUMP_LOG_DIR overrides the folder.
var logDir = Environment.GetEnvironmentVariable("GASPUMP_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP.GasPumpSimulator", "logs");
Directory.CreateDirectory(logDir);
var heraldLogPath = Path.Combine(logDir, $"gaspump-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");

// The site custody key. Steve's spec puts this in a KMS or HSM; the simulator keeps a software key beside the logs
// so both processes and the verifier read the same bytes. GASPUMP_CUSTODY_IKM_HEX overrides it. Use this setup in the simulator only.
var ikm = CustodyKeyFile.Load(logDir);
var ikmPath = Path.Combine(logDir, "custody.ikm");

var options = SimulationOptions.Default with
{
    HoldAmount = Money.FromDollars(30),
    Journal = new JournalOptions(logDir, "PUMP-07", ikm),
    LogSinks = b => b.WithFileSink(heraldLogPath).WithConsoleSink(),
    Dispenser = dispenserMode == "inproc" ? null : new DispenserLinkOptions("127.0.0.1", dispenserPort),
};

IDispatchStrategy MakeDispatch() => strategyName switch
{
    "channel" => new ChannelDispatch(clock),
    "asyncproxy" => new AsyncProxyDispatch(clock),
    _ => new ThreadedDispatch(clock),
};

// The dispenser process. Started before the station so the first connect lands; adopted if one is already listening.
DispenserProcess? dispenserProcess = options.Dispenser is null ? null : new DispenserProcess(dispenserPort, "PUMP-07", logDir, strategyName);
dispenserProcess?.Start();

var simHost = new SimulationHost(() => new Simulation(MakeDispatch(), clock, options), clock, restoreDispenserPower: () => dispenserProcess?.Start());
var hub = new EventHub(simHost);
// The station server: receives the custody stream and keeps its own copy of the dispenser's chain, per dispenser run.
var station = new StationServer(logDir, ikm);
station.Bind(simHost.Current);
simHost.Swapped += station.Bind;
hub.Attach(station, dispenserProcess);
builder.Services.AddSingleton(simHost);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton(station);

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
Simulation Sim() => simHost.Current;

app.MapGet("/api/state", () => Results.Json(Contracts.State(Sim(), simHost, station, dispenserProcess), json));

// The dispenser process: its power, from the station's side of the seam.
app.MapGet("/api/dispenser", () => Results.Json(new { view = Contracts.Pump(Sim().View), link = Sim().Link.Kind, connected = Sim().Link.Connected, pid = Sim().Link.RemotePid, runId = Sim().Link.RemoteRunId, custodyPath = Sim().Link.RemoteCustodyPath, framesSent = Sim().Link.FramesSent, framesReceived = Sim().Link.FramesReceived, processRunning = dispenserProcess?.Running, external = dispenserProcess?.External, lastExit = dispenserProcess?.LastExit, port = dispenserProcess?.Port }, json));
app.MapPost("/api/dispenser/powerloss", () =>
{
    if (dispenserProcess is null) return Results.Conflict(new { error = "the dispenser is in this process; use /api/host/powerloss" });
    if (dispenserProcess.External) return Results.Conflict(new { error = "the dispenser was started by hand; stop it from its own terminal" });
    dispenserProcess.Kill();
    return Results.Ok();
});
app.MapPost("/api/dispenser/poweron", () =>
{
    if (dispenserProcess is null) return Results.Conflict(new { error = "the dispenser is in this process" });
    dispenserProcess.Start();
    return Results.Ok();
});
app.MapGet("/api/cards", () => Results.Json(TestCards.ByName.Select(kv => new { name = kv.Key, last4 = kv.Value.Last4, kind = kv.Value.Kind.ToString(), expiry = $"{kv.Value.ExpiryMonth:00}/{kv.Value.ExpiryYear}" }), json));
app.MapGet("/api/log", (int? after) => Results.Json(Sim().Log.Snapshot().Skip(after ?? 0).Select(l => Contracts.Log(l, Origins.OfLog(l))), json));
app.MapGet("/api/logfile", () => Results.Json(new { path = heraldLogPath, directory = logDir, format = "ndjson (Herald.OSS json_file sink)", lines = Sim().Log.Snapshot().Count }, json));
// A plain-text copy of everything since the host started, rendered from the same lines the UI shows.
app.MapGet("/api/log/export", (string? format) =>
{
    var lines = Sim().Log.Snapshot();
    if (format == "json")
        return Results.Json(lines.Select(l => Contracts.Log(l, Origins.OfLog(l))), json);
    var text = string.Join('\n', lines.Select(l => $"{l.At:yyyy-MM-dd HH:mm:ss.fff}  {l.Severity,-5} {l.Source,-8} {l.Message}" + (l.Props.Count == 0 ? "" : "  {" + string.Join(", ", l.Props.Select(p => $"{p.Key}={p.Value}")) + "}")));
    return Results.Text(text + "\n", "text/plain");
});
app.MapGet("/api/ledger", () => Results.Json(new { verify = Sim().Ledger.Verify(), totalizer = new Volume(Sim().View.TotalizerMilliGallons).Gallons, entries = Sim().Ledger.Snapshot() }, json));

// The custody files: TesseraSeal's verifier over the station's chain, plus the list of files on disk from both sides.
app.MapGet("/api/custody", () =>
{
    var s = Sim();
    var files = Directory.GetFiles(logDir, "*custody-*.ndjson").OrderByDescending(f => f).Select(Path.GetFileName).ToArray();
    return Results.Json(new { runId = s.RunId, path = s.CustodyPath, journalPath = s.JournalPath, ikmPath, files, dispenserPath = s.Link.RemoteCustodyPath, dispenserRunId = s.Link.RemoteRunId }, json);
});
app.MapPost("/api/custody/verify", () =>
{
    var (ok, seq, reason, lines) = Sim().VerifyCustody();
    Sim().Log.Write("custody", "verify {Status} {Reason} ({Lines} lines)", ("Status", ok ? "PASS" : "FAIL"), ("Reason", reason ?? "chain intact"), ("Lines", lines));
    return Results.Json(new { ok, failureAtSeq = seq, reason, lines, status = ok ? "Status: PASS" : $"Status: FAIL\nReason: {reason}" }, json);
});
// Power loss on purpose: no close-out, no final snapshot. The replacement recovers from the last periodic one.
app.MapPost("/api/host/powerloss", () =>
{
    simHost.PowerLoss();
    return Results.Json(new { recovery = Contracts.Recovery(Sim().LastRecovery), runId = Sim().RunId }, json);
});

// The station. E-stop drops power to everything and stays down until the cashier resets.
app.MapPost("/api/station/estop", (EStopRequest? r) => { simHost.EmergencyStop(r?.PressedBy ?? "forecourt button"); return Results.Json(new { estopActive = simHost.EStopActive, estopAt = simHost.EStopAt }, json); });
app.MapPost("/api/station/reset", () =>
{
    simHost.Reset();
    return Results.Json(new { estopActive = simHost.EStopActive, recovery = Contracts.Recovery(Sim().LastRecovery), runId = Sim().RunId }, json);
});
app.MapGet("/api/station/custody", () => Results.Json(new { station.RunId, copyPath = station.CopyPath, linesReceived = station.LinesReceived, dispenserLines = Sim().DispenserCustodyLines }, json));
app.MapPost("/api/station/custody/verify", () =>
{
    var (ok, seq, reason, lines) = station.VerifyCopy();
    return Results.Json(new { ok, failureAtSeq = seq, reason, lines, status = ok ? "Status: PASS" : $"Status: FAIL\nReason: {reason}" }, json);
});

// Forecourt faults: what a sensor or the tank gauge would deliver to the pump.
app.MapPost("/api/fault/{kind}", (string kind) =>
{
    if (simHost.EStopActive) return Results.Conflict(new { error = "E-stop active; reset at the cashier first" });
    var s = Sim();
    switch (kind)
    {
        case "tankout": s.TankRunsDry(); break;
        case "tankback": s.TankRefilled(); break;
        case "pulserdead": s.KillPulser(); break;
        case "pulserfix": s.FixPulser(); break;
        case "driveoff": s.DriveOff(); break;
        case "breakawayreset": s.ResetBreakaway(); break;
        default: return Results.NotFound(new { error = $"unknown fault '{kind}'" });
    }
    return Results.Ok();
});

// The custody stream for outside listeners: every line the dispenser's sink writes, byte for byte, as it lands.
app.Map("/ws/custody", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var queue = System.Threading.Channels.Channel.CreateUnbounded<string>();
    using var sub = station.Subscribe(line => queue.Writer.TryWrite(line));
    try
    {
        await foreach (var line in queue.Reader.ReadAllAsync(ctx.RequestAborted))
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
});

app.MapPost("/api/card", (CardRequest r) =>
{
    var (ok, saleId, reason) = Sim().TryPresentCard(r.Name, r.Grade, r.Tender ?? Tender.Credit, r.Source ?? CardSource.PumpInsert);
    return ok ? Results.Json(new { saleId }, json) : Results.Conflict(new { saleId, error = reason });
});
app.MapPost("/api/cash", (CashRequest r) => Results.Json(new { saleId = Sim().PayCash(new Money(r.Cents), r.Grade) }, json));
app.MapPost("/api/cash/collected", () => { Sim().CollectChange(); return Results.Ok(); });
app.MapPost("/api/admin/limits", (AdminRequest r) =>
{
    var (ok, reason) = Sim().AdminSetHold(r.Pin ?? "", r.Tender, new Money(r.Cents));
    return Results.Json(new { ok, reason, creditHoldCents = Sim().Site.CreditHold.Cents, debitHoldCents = Sim().Site.DebitHold.Cents }, json);
});
app.MapPost("/api/grade", (GradeRequest r) => { Sim().SelectGrade(r.Grade); return Results.Ok(); });
app.MapPost("/api/nozzle/lift", () => { Sim().LiftNozzle(); return Results.Ok(); });
app.MapPost("/api/nozzle/seat", () => { Sim().SeatNozzle(); return Results.Ok(); });
app.MapPost("/api/nozzle/unseat", () => { Sim().UnseatNozzle(); return Results.Ok(); });
app.MapPost("/api/nozzle/hang", () => { Sim().HangNozzle(); return Results.Ok(); });
app.MapPost("/api/trigger/squeeze", () => { Sim().SqueezeTrigger(); return Results.Ok(); });
app.MapPost("/api/trigger/release", () => { Sim().ReleaseTrigger(); return Results.Ok(); });
app.MapPost("/api/cancel", () => { Sim().Cancel(); return Results.Ok(); });
app.MapPost("/api/reset", () => { Sim().ResetSale(); return Results.Ok(); });
app.MapPost("/api/drain", () => { Sim().DrainPendingCompletions(); return Results.Ok(); });

app.MapPost("/api/network", (NetworkRequest r) =>
{
    var s = Sim();
    if (r.Mode is { } m) s.Network.Mode = m;
    if (r.LatencyMs is { } l) s.Network.Latency = TimeSpan.FromMilliseconds(l);
    if (r.LateResponseMs is { } late) s.Network.LateResponse = TimeSpan.FromMilliseconds(late);
    if (r.FlapFailureRate is { } f) s.Network.FlapFailureRate = f;
    s.Log.Write("network", $"mode {s.Network.Mode}, latency {s.Network.Latency.TotalMilliseconds} ms, late answer {s.Network.LateResponse.TotalSeconds} s");
    return Results.Ok();
});

app.MapPost("/api/attack/{kind}", (string kind, AttackRequest? r) =>
{
    var s = Sim();
    switch (kind)
    {
        case "replay": s.Log.Write("attack", "replaying the last token"); s.AttackReplayLastToken(); break;
        case "tamper": s.Log.Write("attack", $"tampering ceiling to {new Money(r?.Cents ?? 200_000)}"); s.AttackTamperCeiling(new Money(r?.Cents ?? 200_000)); break;
        case "overlimit":
            var gallons = Volume.FromGallons(r?.Gallons ?? 600);
            var money = new Money(r?.Cents ?? 200_000);
            s.Log.Write("attack", $"signed token for {gallons} and {money}, bypassing the issuer");
            s.AttackSignedOverLimit(gallons, money);
            break;
        case "spoof": s.AttackSpoofPulses(r?.Pulses ?? 2000); break;
        case "notoken": s.Log.Write("attack", "arming with an empty token"); s.AttackArmWithoutToken(); break;
        case "pinbrute": s.AttackBruteForceAdminPin(); break;
        default: return Results.NotFound(new { error = $"unknown attack '{kind}'" });
    }
    return Results.Ok();
});

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.ServeAsync(socket, json, ctx.RequestAborted);
});

app.Lifetime.ApplicationStopping.Register(() => { station.Dispose(); simHost.Dispose(); dispenserProcess?.Dispose(); });
Sim().Log.Write("host", "station pid {Pid} listening on {Url} with {Dispatch} dispatch, dispenser via {Link}{Port}, Herald.OSS log at {LogPath}, custody at {Custody}",
    ("Pid", Environment.ProcessId), ("Url", "http://localhost:5177"), ("Dispatch", Sim().StrategyName), ("Link", Sim().Link.Kind), ("Port", options.Dispenser is { } d ? $" port {d.Port}" : ""), ("LogPath", heraldLogPath), ("Custody", Sim().CustodyPath));
app.Run();

record CardRequest(string Name, Grade? Grade, Tender? Tender, CardSource? Source);
record EStopRequest(string? PressedBy);
record CashRequest(long Cents, Grade? Grade);
record AdminRequest(string? Pin, Tender Tender, long Cents);
record GradeRequest(Grade Grade);
record NetworkRequest(NetworkMode? Mode, int? LatencyMs, int? LateResponseMs, double? FlapFailureRate);
record AttackRequest(long? Cents, decimal? Gallons, long? Pulses);
