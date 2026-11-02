using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Host;
using GasPump.Payment;

// --strategy threaded|channel|asyncproxy. Threaded is the default so the "old way" is what you see first.
// --dispenser socket|inproc. Socket is the default: each pump runs in its own GasPump.Dispenser.exe and this process is the station.
// --dispenser-port 5178. GASPUMP_STATION, GASPUMP_THEME, GASPUMP_PORT, GASPUMP_DISPENSER_PORT, and GASPUMP_PUMPS name the
// station, its ports, and how many pumps it drives, so two stations can run on one machine (see README, "Two stations across the street").
string Arg(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
string? ArgOrNull(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var identity = StationIdentity.FromEnvironment(Environment.GetEnvironmentVariable, ArgOrNull("--dispenser-port"));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(identity.Url);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var strategyName = Arg("--strategy", "threaded");
var dispenserMode = Arg("--dispenser", "socket");
var clock = TimeProvider.System;

// One directory holds the files of every process: the station's Herald log (gaspump-*.ndjson), the state journal
// (state.json, rewritten every second), the station's TesseraSeal chain (custody-RUN-*.ndjson), each dispenser's
// log (dispenser-PUMP-0n-*.ndjson), chain (dispenser-custody-DSP-*.ndjson), state (dispenser-state-PUMP-0n.json), and the
// station's copy of each dispenser's chain (station-copy-Pn-*.ndjson). GASPUMP_LOG_DIR overrides the folder.
var logDir = Environment.GetEnvironmentVariable("GASPUMP_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP.GasPumpSimulator", "logs");
Directory.CreateDirectory(logDir);
var heraldLogPath = Path.Combine(logDir, $"gaspump-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");

// The site custody key. Steve's spec puts this in a KMS or HSM; the simulator keeps a software key beside the logs
// so every process and the verifier read the same bytes. GASPUMP_CUSTODY_IKM_HEX overrides it. Use this setup in the simulator only.
var ikm = CustodyKeyFile.Load(logDir);
var ikmPath = Path.Combine(logDir, "custody.ikm");

var options = SimulationOptions.Default with
{
    PumpId = StationIdentity.PumpIdFor(1),
    PumpCount = identity.PumpCount,
    HoldAmount = Money.FromDollars(30),
    // The TesseraSeal tenant id is a plain token (the spec's character class), so the station's sign ("Howard's") cannot be it.
    Journal = new JournalOptions(logDir, "STATION", ikm),
    LogSinks = b => b.WithFileSink(heraldLogPath).WithConsoleSink(),
    Dispenser = dispenserMode == "inproc" ? null : new DispenserLinkOptions("127.0.0.1", identity.DispenserPort),
};

IDispatchStrategy MakeDispatch() => strategyName switch
{
    "channel" => new ChannelDispatch(clock),
    "asyncproxy" => new AsyncProxyDispatch(clock),
    _ => new ThreadedDispatch(clock),
};

// The dispenser processes, one per pump. Started before the station so the first connects land; adopted if one is already listening.
var processes = options.Dispenser is null
    ? new List<DispenserProcess>()
    : identity.PumpNumbers.Select(n => new DispenserProcess(n, identity.DispenserPortFor(n), StationIdentity.PumpIdFor(n), logDir, strategyName)).ToList();
foreach (var p in processes) p.Start();

var simHost = new SimulationHost(() => new Simulation(MakeDispatch(), clock, options), clock, restoreDispenserPower: () => { foreach (var p in processes) p.Start(); });
var hub = new EventHub(simHost);
// The station server: receives each dispenser's custody stream and keeps its own copy of each chain, per dispenser run.
var station = new StationServer(logDir, ikm);
station.Bind(simHost.Current);
simHost.Swapped += station.Bind;
hub.Attach(station, processes, identity);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(simHost);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton(station);

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
Simulation Sim() => simHost.Current;
/// A pump number from a route, or null when this station has no such pump.
FuellingPoint? Point(int n) => n >= 1 && n <= Sim().Points.Count ? Sim().Point(n) : null;
DispenserProcess? ProcessOf(int n) => processes.FirstOrDefault(p => p.Number == n);
IResult NoSuchPump(int n) => Results.NotFound(new { error = $"no pump {n}; this station has pumps 1 to {Sim().Points.Count}" });

app.MapGet("/api/state", () => Results.Json(Contracts.State(Sim(), simHost, station, processes, identity), json));
app.MapGet("/api/cards", () => Results.Json(TestCards.ByName.Select(kv => new { name = kv.Key, last4 = kv.Value.Last4, kind = kv.Value.Kind.ToString(), expiry = $"{kv.Value.ExpiryMonth:00}/{kv.Value.ExpiryYear}" }), json));
app.MapGet("/api/log", (int? after) => Results.Json(Sim().Log.Snapshot().Skip(after ?? 0).Select(l => Contracts.Log(l, Origins.OfLog(l), Origins.PumpOfLog(l))), json));
app.MapGet("/api/logfile", () => Results.Json(new { path = heraldLogPath, directory = logDir, format = "ndjson (Herald.OSS json_file sink)", lines = Sim().Log.Snapshot().Count }, json));
// A plain-text copy of everything since the host started, rendered from the same lines the UI shows.
app.MapGet("/api/log/export", (string? format) =>
{
    var lines = Sim().Log.Snapshot();
    if (format == "json")
        return Results.Json(lines.Select(l => Contracts.Log(l, Origins.OfLog(l), Origins.PumpOfLog(l))), json);
    var text = string.Join('\n', lines.Select(l => $"{l.At:yyyy-MM-dd HH:mm:ss.fff}  {l.Severity,-5} {l.Source,-8} {l.Message}" + (l.Props.Count == 0 ? "" : "  {" + string.Join(", ", l.Props.Select(p => $"{p.Key}={p.Value}")) + "}")));
    return Results.Text(text + "\n", "text/plain");
});
app.MapGet("/api/ledger", () => Results.Json(new { verify = Sim().Ledger.Verify(), totalizers = Sim().Points.Select(p => new { pump = p.Number, gallons = new Volume(p.View.TotalizerMilliGallons).Gallons }), entries = Sim().Ledger.Snapshot() }, json));

// The custody files: TesseraSeal's verifier over the station's chain, plus the list of files on disk from every side.
app.MapGet("/api/custody", () =>
{
    var s = Sim();
    var files = Directory.GetFiles(logDir, "*custody-*.ndjson").OrderByDescending(f => f).Select(Path.GetFileName).ToArray();
    return Results.Json(new { runId = s.RunId, path = s.CustodyPath, journalPath = s.JournalPath, ikmPath, files, dispensers = s.Points.Select(p => new { pump = p.Number, path = p.Link.RemoteCustodyPath, runId = p.Link.RemoteRunId }) }, json);
});
app.MapPost("/api/custody/verify", () =>
{
    var (ok, seq, reason, lines) = Sim().VerifyCustody();
    Sim().Log.Write("custody", "verify {Status} {Reason} ({Lines} lines)", ("Status", ok ? "PASS" : "FAIL"), ("Reason", reason ?? "chain intact"), ("Lines", lines));
    return Results.Json(new { ok, failureAtSeq = seq, reason, lines, status = ok ? "Status: PASS" : $"Status: FAIL\nReason: {reason}" }, json);
});
// Power loss on purpose: no close-out, no final snapshot. The replacement recovers from the last periodic one, pump by pump.
app.MapPost("/api/host/powerloss", () =>
{
    simHost.PowerLoss();
    return Results.Json(new { recoveries = Sim().Recoveries.Select(Contracts.Recovery), runId = Sim().RunId }, json);
});

// The station. E-stop drops power to everything and stays down until the cashier resets.
app.MapPost("/api/station/estop", (EStopRequest? r) => { simHost.EmergencyStop(r?.PressedBy ?? "forecourt button"); return Results.Json(new { estopActive = simHost.EStopActive, estopAt = simHost.EStopAt }, json); });
app.MapPost("/api/station/reset", () =>
{
    simHost.Reset();
    return Results.Json(new { estopActive = simHost.EStopActive, recoveries = Sim().Recoveries.Select(Contracts.Recovery), runId = Sim().RunId }, json);
});
app.MapGet("/api/station/custody", () => Results.Json(new
{
    copies = Sim().Points.Select(p => { var c = station.Copy(p.Number); return new { pump = p.Number, runId = c.RunId, copyPath = c.Path, linesReceived = c.Lines, replayedDuplicatesDropped = c.Duplicates, dispenserLines = p.DispenserCustodyLines }; }),
}, json));
app.MapPost("/api/station/custody/verify", (int? pump) =>
{
    var (ok, seq, reason, lines) = station.VerifyCopy(pump ?? 1);
    return Results.Json(new { pump = pump ?? 1, ok, failureAtSeq = seq, reason, lines, status = ok ? "Status: PASS" : $"Status: FAIL\nReason: {reason}" }, json);
});
app.MapPost("/api/admin/limits", (AdminRequest r) =>
{
    var (ok, reason) = Sim().AdminSetHold(r.Pin ?? "", r.Tender, new Money(r.Cents));
    return Results.Json(new { ok, reason, creditHoldCents = Sim().Site.CreditHold.Cents, debitHoldCents = Sim().Site.DebitHold.Cents }, json);
});
// A posted price, in mills per gallon ($3.499 is 3499). The station writes the whole board to every island, and each answers.
app.MapPost("/api/admin/prices", (PriceRequest r) =>
{
    var (ok, reason) = Sim().AdminSetPrice(r.Pin ?? "", r.Grade, r.Mills);
    return Results.Json(new { ok, reason, priceBoard = Sim().Prices.TenthCentsPerGallon.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) }, json);
});
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
// The one attack on the site rather than on a pump.
app.MapPost("/api/attack/pinbrute", () => { Sim().AttackBruteForceAdminPin(); return Results.Ok(); });
// The tank gauge: one tank feeds every pump, so Fuel Out and its clearing reach every island.
app.MapPost("/api/fault/{kind}", (string kind) =>
{
    if (simHost.EStopActive) return Results.Conflict(new { error = "E-stop active; reset at the cashier first" });
    switch (kind)
    {
        case "tankout": Sim().TankRunsDry(); break;
        case "tankback": Sim().TankRefilled(); break;
        default: return Results.NotFound(new { error = $"unknown site fault '{kind}'; pump faults live under /api/pump/{{n}}/fault" });
    }
    return Results.Ok();
});

// ----- Everything about one pump lives under /api/pump/{n}. The number is the fuelling point, the way a frame names its node.
var pump = app.MapGroup("/api/pump/{n:int}");

pump.MapGet("/", (int n) => Point(n) is { } p
    ? Results.Json(new { number = p.Number, pumpId = p.PumpId, view = Contracts.Pump(p.View, n), link = p.Link.Kind, connected = p.Link.Connected, pid = p.Link.RemotePid, runId = p.Link.RemoteRunId, custodyPath = p.Link.RemoteCustodyPath, framesSent = p.Link.FramesSent, framesReceived = p.Link.FramesReceived, processRunning = ProcessOf(n)?.Running, external = ProcessOf(n)?.External, lastExit = ProcessOf(n)?.LastExit, port = ProcessOf(n)?.Port }, json)
    : NoSuchPump(n));

// The dispenser process: its power, from the station's side of the seam.
pump.MapPost("/dispenser/powerloss", (int n) =>
{
    if (Point(n) is null) return NoSuchPump(n);
    if (ProcessOf(n) is not { } proc) return Results.Conflict(new { error = "the dispenser is in this process; use /api/host/powerloss" });
    if (proc.External) return Results.Conflict(new { error = "the dispenser was started by hand; stop it from its own terminal" });
    proc.Kill();
    return Results.Ok();
});
pump.MapPost("/dispenser/poweron", (int n) =>
{
    if (Point(n) is null) return NoSuchPump(n);
    if (ProcessOf(n) is not { } proc) return Results.Conflict(new { error = "the dispenser is in this process" });
    proc.Start();
    return Results.Ok();
});

pump.MapPost("/card", (int n, CardRequest r) =>
{
    if (Point(n) is not { } p) return NoSuchPump(n);
    var (ok, saleId, reason) = p.TryPresentCard(r.Name, r.Grade, r.Tender ?? Tender.Credit, r.Source ?? CardSource.PumpInsert);
    return ok ? Results.Json(new { saleId }, json) : Results.Conflict(new { saleId, error = reason });
});
// The keypad. The digits go to the sale worker and on to the bank; the host never logs them.
pump.MapPost("/code", (int n, CodeRequest r) => Point(n) is { } p ? Do(() => p.EnterCode(r.Code ?? "")) : NoSuchPump(n));
pump.MapPost("/cash", (int n, CashRequest r) => Point(n) is { } p ? Results.Json(new { saleId = p.PayCash(new Money(r.Cents), r.Grade) }, json) : NoSuchPump(n));
pump.MapPost("/cash/collected", (int n) => Point(n) is { } p ? Do(p.CollectChange) : NoSuchPump(n));
pump.MapPost("/grade", (int n, GradeRequest r) => Point(n) is { } p ? Do(() => p.SelectGrade(r.Grade)) : NoSuchPump(n));
pump.MapPost("/nozzle/lift", (int n) => Point(n) is { } p ? Do(p.LiftNozzle) : NoSuchPump(n));
pump.MapPost("/nozzle/hang", (int n) => Point(n) is { } p ? Do(p.HangNozzle) : NoSuchPump(n));
pump.MapPost("/spout/insert", (int n) => Point(n) is { } p ? Do(p.InsertSpout) : NoSuchPump(n));
pump.MapPost("/spout/withdraw", (int n) => Point(n) is { } p ? Do(p.WithdrawSpout) : NoSuchPump(n));
pump.MapPost("/lever/squeeze", (int n) => Point(n) is { } p ? Do(p.SqueezeLever) : NoSuchPump(n));
pump.MapPost("/lever/release", (int n) => Point(n) is { } p ? Do(p.ReleaseLever) : NoSuchPump(n));
pump.MapPost("/cancel", (int n) => Point(n) is { } p ? Do(() => p.Cancel()) : NoSuchPump(n));
pump.MapPost("/reset", (int n) => Point(n) is { } p ? Do(p.ResetSale) : NoSuchPump(n));
// The inspector's close-out: ask this island for its counters. The answer lands in the pump's state as `totals` and in the ledger as totals.read.
pump.MapPost("/totals", (int n) => Point(n) is { } p ? Do(p.ReadTotals) : NoSuchPump(n));
// The operator's pump switch. Closed is a state, never a fault: the pump works and refuses sales until opened.
pump.MapPost("/switch/close", (int n, PumpSwitchRequest? r) => Point(n) is { } p ? Do(() => p.ClosePump(r?.By ?? "operator")) : NoSuchPump(n));
pump.MapPost("/switch/open", (int n, PumpSwitchRequest? r) => Point(n) is { } p ? Do(() => p.OpenPump(r?.By ?? "operator")) : NoSuchPump(n));

// Forecourt faults on one pump: what a sensor on that island would deliver. The tank is the site's; see /api/fault below.
pump.MapPost("/fault/{kind}", (int n, string kind) =>
{
    if (simHost.EStopActive) return Results.Conflict(new { error = "E-stop active; reset at the cashier first" });
    if (Point(n) is not { } p) return NoSuchPump(n);
    switch (kind)
    {
        case "pulserdead": p.KillPulser(); break;
        case "pulserfix": p.FixPulser(); break;
        case "driveoff": p.DriveOff(); break;
        case "breakawayreset": p.ResetBreakaway(); break;
        default: return Results.NotFound(new { error = $"unknown fault '{kind}'" });
    }
    return Results.Ok();
});

// Attacks on one pump's authorization path. Each goes through the same mailboxes an attacker would have to reach.
pump.MapPost("/attack/{kind}", (int n, string kind, AttackRequest? r) =>
{
    if (Point(n) is not { } p) return NoSuchPump(n);
    switch (kind)
    {
        case "replay": p.Log.Write("attack", "replaying the last token"); p.AttackReplayLastToken(); break;
        case "tamper": p.Log.Write("attack", $"tampering ceiling to {new Money(r?.Cents ?? 200_000)}"); p.AttackTamperCeiling(new Money(r?.Cents ?? 200_000)); break;
        case "overlimit":
            var gallons = Volume.FromGallons(r?.Gallons ?? 600);
            var money = new Money(r?.Cents ?? 200_000);
            p.Log.Write("attack", $"signed token for {gallons} and {money}, bypassing the issuer");
            p.AttackSignedOverLimit(gallons, money);
            break;
        case "spoof": p.AttackSpoofPulses(r?.Pulses ?? 2000); break;
        case "notoken": p.Log.Write("attack", "arming with an empty token"); p.AttackArmWithoutToken(); break;
        default: return Results.NotFound(new { error = $"unknown attack '{kind}'" });
    }
    return Results.Ok();
});

static IResult Do(Action a) { a(); return Results.Ok(); }

// The custody stream for outside listeners: every line every dispenser's sink writes, byte for byte, as it lands.
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

app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await hub.ServeAsync(socket, json, ctx.RequestAborted);
});

app.Lifetime.ApplicationStopping.Register(() => { station.Dispose(); simHost.Dispose(); foreach (var p in processes) p.Dispose(); });
Sim().Log.Write("host", "station {Station} pid {Pid} listening on {Url} with {Dispatch} dispatch, {PumpCount} pump(s) via {Link}{Ports}, Herald.OSS log at {LogPath}, custody at {Custody}",
    ("Station", identity.Name), ("Pid", Environment.ProcessId), ("Url", identity.Url), ("Dispatch", Sim().StrategyName), ("PumpCount", identity.PumpCount), ("Link", Sim().Link.Kind),
    ("Ports", options.Dispenser is null ? "" : $" ports {string.Join(",", identity.PumpNumbers.Select(identity.DispenserPortFor))}"), ("LogPath", heraldLogPath), ("Custody", Sim().CustodyPath));
app.Run();

record CardRequest(string Name, Grade? Grade, Tender? Tender, CardSource? Source);
record CodeRequest(string? Code);
record EStopRequest(string? PressedBy);
record PumpSwitchRequest(string? By);
record CashRequest(long Cents, Grade? Grade);
record AdminRequest(string? Pin, Tender Tender, long Cents);
record PriceRequest(string? Pin, Grade Grade, long Mills);
record GradeRequest(Grade Grade);
record NetworkRequest(NetworkMode? Mode, int? LatencyMs, int? LateResponseMs, double? FlapFailureRate);
record AttackRequest(long? Cents, decimal? Gallons, long? Pulses);
