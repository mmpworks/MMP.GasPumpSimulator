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

// --strategy threaded|channel. Threaded is the default so the "old way" is what you see first.
var strategyName = args.SkipWhile(a => a != "--strategy").Skip(1).FirstOrDefault() ?? "threaded";
var clock = TimeProvider.System;
IDispatchStrategy dispatch = strategyName switch
{
    "channel" => new ChannelDispatch(clock),
    _ => new ThreadedDispatch(clock),
};
// The simulator's log runs on Herald.OSS. The bridge back to the UI is built in; the host adds a file and the console.
// One NDJSON file per host session, so a run can be reviewed later with every property intact.
// GASPUMP_LOG_DIR overrides the folder. Default: %LOCALAPPDATA%\MMP.GasPumpSimulator\logs.
var logDir = Environment.GetEnvironmentVariable("GASPUMP_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MMP.GasPumpSimulator", "logs");
Directory.CreateDirectory(logDir);
var heraldLogPath = Path.Combine(logDir, $"gaspump-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");
var log = new HeraldEventLog(clock, b => b.WithFileSink(heraldLogPath).WithConsoleSink());
var sim = new Simulation(dispatch, clock, SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) }, log: log);
var hub = new EventHub(sim);
builder.Services.AddSingleton(sim);
builder.Services.AddSingleton(hub);

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

app.MapGet("/api/state", () => Results.Json(Contracts.State(sim), json));
app.MapGet("/api/cards", () => Results.Json(TestCards.ByName.Select(kv => new { name = kv.Key, last4 = kv.Value.Last4, kind = kv.Value.Kind.ToString(), expiry = $"{kv.Value.ExpiryMonth:00}/{kv.Value.ExpiryYear}" }), json));
app.MapGet("/api/log", (int? after) => Results.Json(sim.Log.Snapshot().Skip(after ?? 0).Select(Contracts.Log), json));
app.MapGet("/api/logfile", () => Results.Json(new { path = heraldLogPath, directory = logDir, format = "ndjson (Herald.OSS json_file sink)", lines = sim.Log.Snapshot().Count }, json));
// A plain-text copy of everything since the host started, rendered from the same lines the UI shows.
app.MapGet("/api/log/export", (string? format) =>
{
    var lines = sim.Log.Snapshot();
    if (format == "json")
        return Results.Json(lines.Select(Contracts.Log), json);
    var text = string.Join('\n', lines.Select(l => $"{l.At:yyyy-MM-dd HH:mm:ss.fff}  {l.Severity,-5} {l.Source,-8} {l.Message}" + (l.Props.Count == 0 ? "" : "  {" + string.Join(", ", l.Props.Select(p => $"{p.Key}={p.Value}")) + "}")));
    return Results.Text(text + "\n", "text/plain");
});
app.MapGet("/api/ledger", () => Results.Json(new { verify = sim.Ledger.Verify(), totalizer = sim.Totalizer.Total.Gallons, entries = sim.Ledger.Snapshot() }, json));

app.MapPost("/api/card", (CardRequest r) => Results.Json(new { saleId = sim.PresentCard(r.Name, r.Grade, r.Tender ?? Tender.Credit) }, json));
app.MapPost("/api/cash", (CashRequest r) => Results.Json(new { saleId = sim.PayCash(new Money(r.Cents), r.Grade) }, json));
app.MapPost("/api/cash/collected", () => { sim.CollectChange(); return Results.Ok(); });
app.MapPost("/api/admin/limits", (AdminRequest r) =>
{
    var (ok, reason) = sim.AdminSetHold(r.Pin ?? "", r.Tender, new Money(r.Cents));
    return Results.Json(new { ok, reason, creditHoldCents = sim.Site.CreditHold.Cents, debitHoldCents = sim.Site.DebitHold.Cents }, json);
});
app.MapPost("/api/grade", (GradeRequest r) => { sim.SelectGrade(r.Grade); return Results.Ok(); });
app.MapPost("/api/nozzle/lift", () => { sim.LiftNozzle(); return Results.Ok(); });
app.MapPost("/api/nozzle/seat", () => { sim.SeatNozzle(); return Results.Ok(); });
app.MapPost("/api/nozzle/unseat", () => { sim.UnseatNozzle(); return Results.Ok(); });
app.MapPost("/api/nozzle/hang", () => { sim.HangNozzle(); return Results.Ok(); });
app.MapPost("/api/trigger/squeeze", () => { sim.SqueezeTrigger(); return Results.Ok(); });
app.MapPost("/api/trigger/release", () => { sim.ReleaseTrigger(); return Results.Ok(); });
app.MapPost("/api/cancel", () => { sim.Cancel(); return Results.Ok(); });
app.MapPost("/api/reset", () => { sim.ResetSale(); return Results.Ok(); });
app.MapPost("/api/drain", () => { sim.DrainPendingCompletions(); return Results.Ok(); });

app.MapPost("/api/network", (NetworkRequest r) =>
{
    if (r.Mode is { } m) sim.Network.Mode = m;
    if (r.LatencyMs is { } l) sim.Network.Latency = TimeSpan.FromMilliseconds(l);
    if (r.LateResponseMs is { } late) sim.Network.LateResponse = TimeSpan.FromMilliseconds(late);
    if (r.FlapFailureRate is { } f) sim.Network.FlapFailureRate = f;
    sim.Log.Write("network", $"mode {sim.Network.Mode}, latency {sim.Network.Latency.TotalMilliseconds} ms, late answer {sim.Network.LateResponse.TotalSeconds} s");
    return Results.Ok();
});

app.MapPost("/api/attack/{kind}", (string kind, AttackRequest? r) =>
{
    switch (kind)
    {
        case "replay": sim.Log.Write("attack", "replaying the last token"); sim.AttackReplayLastToken(); break;
        case "tamper": sim.Log.Write("attack", $"tampering ceiling to {new Money(r?.Cents ?? 200_000)}"); sim.AttackTamperCeiling(new Money(r?.Cents ?? 200_000)); break;
        case "overlimit":
            var gallons = Volume.FromGallons(r?.Gallons ?? 600);
            var money = new Money(r?.Cents ?? 200_000);
            sim.Log.Write("attack", $"signed token for {gallons} and {money}, bypassing the issuer");
            sim.AttackSignedOverLimit(gallons, money);
            break;
        case "spoof": sim.AttackSpoofPulses(r?.Pulses ?? 2000); break;
        case "notoken": sim.Log.Write("attack", "arming with an empty token"); sim.AttackArmWithoutToken(); break;
        case "pinbrute": sim.AttackBruteForceAdminPin(); break;
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

app.Lifetime.ApplicationStopping.Register(() => { sim.Dispose(); log.Dispose(); });
sim.Log.Write("host", "listening on {Url} with {Dispatch} dispatch, Herald.OSS log at {LogPath}", ("Url", "http://localhost:5177"), ("Dispatch", sim.StrategyName), ("LogPath", heraldLogPath));
app.Run();

record CardRequest(string Name, Grade? Grade, Tender? Tender);
record CashRequest(long Cents, Grade? Grade);
record AdminRequest(string? Pin, Tender Tender, long Cents);
record GradeRequest(Grade Grade);
record NetworkRequest(NetworkMode? Mode, int? LatencyMs, int? LateResponseMs, double? FlapFailureRate);
record AttackRequest(long? Cents, decimal? Gallons, long? Pulses);
