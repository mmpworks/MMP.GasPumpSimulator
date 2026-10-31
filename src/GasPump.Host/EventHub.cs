using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GasPump.Core;
using GasPump.Dispatch;
using GasPump.Security;

namespace GasPump.Host;

/// <summary>
/// Fans simulation events out to every connected socket. Each socket gets its
/// own bounded queue; a slow client drops its oldest messages rather than
/// slowing the simulation. The pump never waits on a browser.
/// </summary>
public sealed class EventHub
{
    private readonly Simulation _sim;
    private readonly Lock _gate = new();
    private readonly List<Channel<object>> _clients = [];

    public EventHub(Simulation sim)
    {
        _sim = sim;
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
            channel.Writer.TryWrite(Contracts.State(_sim));
            foreach (var l in _sim.Log.Snapshot().TakeLast(200)) channel.Writer.TryWrite(Contracts.Log(l));
            foreach (var e in _sim.Ledger.Snapshot().TakeLast(200)) channel.Writer.TryWrite(Contracts.Ledger(e));

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
                while (await timer.WaitForNextTickAsync(ct)) channel.Writer.TryWrite(Contracts.State(_sim));
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
