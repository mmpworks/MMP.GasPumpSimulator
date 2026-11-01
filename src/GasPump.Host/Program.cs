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
var sim = new Simulation(dispatch, clock, SimulationOptions.Default with { HoldAmount = Money.FromDollars(30) });
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
app.MapGet("/api/ledger", () => Results.Json(new { verify = sim.Ledger.Verify(), totalizer = sim.Totalizer.Total.Gallons, entries = sim.Ledger.Snapshot() }, json));

app.MapPost("/api/card", (CardRequest r) => Results.Json(new { saleId = sim.PresentCard(r.Name, r.Grade) }, json));
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

app.Lifetime.ApplicationStopping.Register(() => sim.Dispose());
sim.Log.Write("host", $"listening on http://localhost:5177 with {sim.StrategyName} dispatch");
app.Run();

record CardRequest(string Name, Grade? Grade);
record GradeRequest(Grade Grade);
record NetworkRequest(NetworkMode? Mode, int? LatencyMs, int? LateResponseMs, double? FlapFailureRate);
record AttackRequest(long? Cents, decimal? Gallons, long? Pulses);
