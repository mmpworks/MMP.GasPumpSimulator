using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using GasPump.Dispatch;

namespace GasPump.Host;

/// <summary>
/// Fans simulation events out to every connected socket. Each socket gets its
/// own bounded queue; a slow client drops its oldest messages rather than
/// slowing the simulation. The pump never waits on a browser. When the host
/// swaps the simulation (power loss), the hub rebinds and pushes a full state.
/// </summary>
public sealed class EventHub
{
    private readonly SimulationHost _host;
    private readonly Lock _gate = new();
    private readonly List<Channel<object>> _clients = [];
    private StationServer? _station;

    public EventHub(SimulationHost host)
    {
        _host = host;
        Bind(host.Current);
        host.Swapped += sim =>
        {
            Bind(sim);
            // A new run: tell the UI to start its ledger over, replay what the new simulation wrote while it was
            // being built (the recovery entries), then the full state.
            Broadcast(new { type = "run", runId = sim.RunId });
            foreach (var l in sim.Log.Snapshot()) Broadcast(Contracts.Log(l));
            foreach (var e in sim.Ledger.Snapshot()) Broadcast(Contracts.Ledger(e));
            Broadcast(StateFor(sim));
        };
    }

    /// <summary>The station server, once built, so the state push can carry its counters.</summary>
    public void Attach(StationServer station) => _station = station;

    private object StateFor(Simulation sim) => Contracts.State(sim, _host, _station);

    private void Bind(Simulation sim)
    {
        sim.SaleChanged += s => Broadcast(Contracts.Sale(s));
        sim.Log.Written += l => Broadcast(Contracts.Log(l));
        sim.Ledger.Appended += e => Broadcast(Contracts.Ledger(e));
    }

    private void Broadcast(object message)
    {
        Channel<object>[] clients;
        lock (_gate) clients = _clients.ToArray();
        foreach (var c in clients) c.Writer.TryWrite(message);
    }

    public async Task ServeAsync(WebSocket socket, JsonSerializerOptions json, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<object>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) _clients.Add(channel);
        try
        {
            // Full state first, then the stream. The UI never has to merge a gap.
            var sim = _host.Current;
            channel.Writer.TryWrite(StateFor(sim));
            foreach (var l in sim.Log.Snapshot().TakeLast(200)) channel.Writer.TryWrite(Contracts.Log(l));
            foreach (var e in sim.Ledger.Snapshot().TakeLast(200)) channel.Writer.TryWrite(Contracts.Ledger(e));

            var pump = Task.Run(async () =>
            {
                await foreach (var m in channel.Reader.ReadAllAsync(ct))
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(m, json);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                }
            }, ct);

            // Every 500 ms push the full state too, so a UI that missed something self-heals.
            var heartbeat = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                while (await timer.WaitForNextTickAsync(ct)) channel.Writer.TryWrite(StateFor(_host.Current));
            }, ct);

            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_gate) _clients.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
