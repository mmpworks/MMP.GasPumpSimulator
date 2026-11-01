using System.Net.WebSockets;
using System.Security.Cryptography;
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
var strategyName = args.SkipWhile(a => a != "--strategy").Skip(1).FirstOrDefault() ?? "threaded";
var clock = TimeProvider.System;

// One directory holds the three files: the Herald debug log (NDJSON, one per session), the state journal
// (state.json, rewritten every second), and the TesseraSeal custody file (one HMAC chain per run).
// GASPUMP_LOG_DIR overrides the folder. Default: %LOCALAPPDATA%\MMP.GasPumpSimulator\logs.
var logDir = Environment.GetEnvironmentVariable("GASPUMP_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP.GasPumpSimulator", "logs");
Directory.CreateDirectory(logDir);
var heraldLogPath = Path.Combine(logDir, $"gaspump-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");

// The custody key. Steve's spec puts this in a KMS or HSM; the simulator keeps a software key beside the logs so
// the verifier can read the same bytes. GASPUMP_CUSTODY_IKM_HEX overrides it. Never a production posture.
var ikmPath = Path.Combine(logDir, "custody.ikm");
byte[] ikm;
if (Environment.GetEnvironmentVariable("GASPUMP_CUSTODY_IKM_HEX") is { Length: >= 64 } hex) ikm = Convert.FromHexString(hex);
else if (File.Exists(ikmPath)) ikm = Convert.FromHexString(File.ReadAllText(ikmPath).Trim());
else { ikm = RandomNumberGenerator.GetBytes(32); File.WriteAllText(ikmPath, Convert.ToHexString(ikm)); }

var options = SimulationOptions.Default with
{
    HoldAmount = Money.FromDollars(30),
    Journal = new JournalOptions(logDir, "PUMP-07", ikm),
    LogSinks = b => b.WithFileSink(heraldLogPath).WithConsoleSink(),
};

IDispatchStrategy MakeDispatch() => strategyName switch
{
    "channel" => new ChannelDispatch(clock),
    "asyncproxy" => new AsyncProxyDispatch(clock),
    _ => new ThreadedDispatch(clock),
};

var simHost = new SimulationHost(() => new Simulation(MakeDispatch(), clock, options), clock);
var hub = new EventHub(simHost);
// The station server: receives the custody stream and keeps its own copy of the chain, per run.
var station = new StationServer(logDir, ikm);
station.Bind(simHost.Current);
simHost.Swapped += station.Bind;
hub.Attach(station);
builder.Services.AddSingleton(simHost);
builder.Services.AddSingleton(hub);
builder.Services.AddSingleton(station);

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
Simulation Sim() => simHost.Current;

app.MapGet("/api/state", () => Results.Json(Contracts.State(Sim(), simHost, station), json));
app.MapGet("/api/cards", () => Results.Json(TestCards.ByName.Select(kv => new { name = kv.Key, last4 = kv.Value.Last4, kind = kv.Value.Kind.ToString(), expiry = $"{kv.Value.ExpiryMonth:00}/{kv.Value.ExpiryYear}" }), json));
app.MapGet("/api/log", (int? after) => Results.Json(Sim().Log.Snapshot().Skip(after ?? 0).Select(Contracts.Log), json));
app.MapGet("/api/logfile", () => Results.Json(new { path = heraldLogPath, directory = logDir, format = "ndjson (Herald.OSS json_file sink)", lines = Sim().Log.Snapshot().Count }, json));
// A plain-text copy of everything since the host started, rendered from the same lines the UI shows.
app.MapGet("/api/log/export", (string? format) =>
{
    var lines = Sim().Log.Snapshot();
    if (format == "json")
        return Results.Json(lines.Select(Contracts.Log), json);
    var text = string.Join('\n', lines.Select(l => $"{l.At:yyyy-MM-dd HH:mm:ss.fff}  {l.Severity,-5} {l.Source,-8} {l.Message}" + (l.Props.Count == 0 ? "" : "  {" + string.Join(", ", l.Props.Select(p => $"{p.Key}={p.Value}")) + "}")));
    return Results.Text(text + "\n", "text/plain");
});
app.MapGet("/api/ledger", () => Results.Json(new { verify = Sim().Ledger.Verify(), totalizer = Sim().Totalizer.Total.Gallons, entries = Sim().Ledger.Snapshot() }, json));

// The custody file: TesseraSeal's verifier over this run's chain, plus the list of earlier runs' files.
app.MapGet("/api/custody", () =>
{
    var s = Sim();
    var files = Directory.GetFiles(logDir, "custody-*.ndjson").OrderByDescending(f => f).Select(Path.GetFileName).ToArray();
    return Results.Json(new { runId = s.RunId, path = s.CustodyPath, journalPath = s.JournalPath, ikmPath, files }, json);
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
app.MapGet("/api/station/custody", () => Results.Json(new { station.RunId, copyPath = station.CopyPath, linesReceived = station.LinesReceived, dispenserLines = Sim().Custody?.LinesPublished ?? 0 }, json));
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

app.MapPost("/api/card", (CardRequest r) => Results.Json(new { saleId = Sim().PresentCard(r.Name, r.Grade, r.Tender ?? Tender.Credit) }, json));
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

app.Lifetime.ApplicationStopping.Register(() => { station.Dispose(); simHost.Dispose(); });
Sim().Log.Write("host", "listening on {Url} with {Dispatch} dispatch, Herald.OSS log at {LogPath}, custody at {Custody}", ("Url", "http://localhost:5177"), ("Dispatch", Sim().StrategyName), ("LogPath", heraldLogPath), ("Custody", Sim().CustodyPath));
app.Run();

record CardRequest(string Name, Grade? Grade, Tender? Tender);
record EStopRequest(string? PressedBy);
record CashRequest(long Cents, Grade? Grade);
record AdminRequest(string? Pin, Tender Tender, long Cents);
record GradeRequest(Grade Grade);
record NetworkRequest(NetworkMode? Mode, int? LatencyMs, int? LateResponseMs, double? FlapFailureRate);
record AttackRequest(long? Cents, decimal? Gallons, long? Pulses);
