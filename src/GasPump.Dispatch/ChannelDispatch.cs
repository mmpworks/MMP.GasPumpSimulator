using System.Threading.Channels;

namespace GasPump.Dispatch;

/// <summary>
/// The modern answer. A Task per worker that awaits a bounded Channel, so a
/// worker with nothing to do holds no thread at all. Same three rules as the
/// interface, same handlers, same tests. Only the plumbing changed.
/// </summary>
public sealed class ChannelDispatch(TimeProvider clock) : IDispatchStrategy
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<Func<bool>> _idleChecks = [];
    private readonly Lock _gate = new();
    private int _busyHandlers;

    public string Name => "channel";
    public bool PrefersBlockingIo => false;

    public bool IsIdle
    {
        get
        {
            lock (_gate)
                return Volatile.Read(ref _busyHandlers) == 0 && _idleChecks.All(c => c());
        }
    }

    public IMailbox<T> CreateMailbox<T>(string name, int capacity)
    {
        var box = new Mailbox<T>(name, capacity);
        lock (_gate) { _owned.Add(box); _idleChecks.Add(() => box.Count == 0); }
        return box;
    }

    public IWorkerHandle StartWorker<T>(string name, IMailbox<T> inbox, Action<T> handle)
    {
        var box = (Mailbox<T>)inbox;
        var worker = new Worker<T>(name, box, handle, this);
        lock (_gate) _owned.Add(worker);
        return worker;
    }

    public IDisposable StartTicker<T>(string name, TimeSpan period, IMailbox<T> target, Func<T> make)
    {
        var timer = clock.CreateTimer(_ => target.TryPost(make()), null, period, period);
        lock (_gate) _owned.Add(timer);
        return timer;
    }

    public IDisposable StartTimer<T>(string name, TimeSpan due, IMailbox<T> target, Func<T> make)
    {
        ITimer? timer = null;
        timer = clock.CreateTimer(_ => { target.TryPost(make()); timer?.Dispose(); }, null, due, Timeout.InfiniteTimeSpan);
        lock (_gate) _owned.Add(timer);
        return timer;
    }

    public void Dispose()
    {
        List<IDisposable> owned;
        lock (_gate) { owned = _owned.ToList(); _owned.Clear(); }
        foreach (var d in owned.AsEnumerable().Reverse()) d.Dispose();
    }

    private sealed class Mailbox<T>(string name, int capacity) : IMailbox<T>, IDisposable
    {
        public readonly Channel<T> Channel = System.Threading.Channels.Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        private int _count;
        public string Name => name;
        public int Count => Volatile.Read(ref _count);
        public void Post(T item)
        {
            // Bounded + Wait: a full box makes the producer wait, synchronously here because
            // the callers are workers with their own thread or a timer callback.
            while (!TryPost(item)) Thread.Yield();
        }
        public bool TryPost(T item)
        {
            if (!Channel.Writer.TryWrite(item)) return false;
            Interlocked.Increment(ref _count);
            return true;
        }
        internal void Took() => Interlocked.Decrement(ref _count);
        public void Dispose() => Channel.Writer.TryComplete();
    }

    private sealed class Worker<T> : IWorkerHandle
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _disposed;

        public Worker(string name, Mailbox<T> box, Action<T> handle, ChannelDispatch owner)
        {
            Name = name;
            _loop = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in box.Channel.Reader.ReadAllAsync(_stop.Token))
                    {
                        Interlocked.Increment(ref owner._busyHandlers);
                        try { handle(item); }
                        catch (Exception) { }
                        finally
                        {
                            box.Took();
                            Interlocked.Decrement(ref owner._busyHandlers);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public string Name { get; }
        public bool IsRunning => !_loop.IsCompleted;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
