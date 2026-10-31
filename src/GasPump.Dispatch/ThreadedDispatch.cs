using System.Collections.Concurrent;

namespace GasPump.Dispatch;

/// <summary>
/// The 2008 answer. One dedicated thread per worker, a BlockingCollection per
/// mailbox, and timers from the TimeProvider that post. A worker thread sits
/// in Take() when it has nothing to do, which costs a thread per worker and
/// nothing else. For three workers that is a fine trade. For three thousand it is not.
/// </summary>
public sealed class ThreadedDispatch(TimeProvider clock) : IDispatchStrategy
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<Func<bool>> _idleChecks = [];
    private readonly Lock _gate = new();
    private int _busyHandlers;

    public string Name => "threaded";
    public bool PrefersBlockingIo => true;

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
        public readonly BlockingCollection<T> Queue = new(new ConcurrentQueue<T>(), capacity);
        private int _pending;
        public string Name => name;
        /// <summary>Posted and not yet fully handled. Includes the one a worker is holding right now.</summary>
        public int Count => Volatile.Read(ref _pending);
        public void Post(T item)
        {
            if (Queue.IsAddingCompleted) return;
            Interlocked.Increment(ref _pending);
            try { Queue.Add(item); } catch (InvalidOperationException) { Interlocked.Decrement(ref _pending); }
        }
        public bool TryPost(T item)
        {
            if (Queue.IsAddingCompleted) return false;
            Interlocked.Increment(ref _pending);
            if (Queue.TryAdd(item)) return true;
            Interlocked.Decrement(ref _pending);
            return false;
        }
        internal void Handled() => Interlocked.Decrement(ref _pending);
        public void Dispose() { Queue.CompleteAdding(); }
    }

    private sealed class Worker<T> : IWorkerHandle
    {
        private readonly Thread _thread;
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _running = true;
        private int _disposed;

        public Worker(string name, Mailbox<T> box, Action<T> handle, ThreadedDispatch owner)
        {
            Name = name;
            _thread = new Thread(() =>
            {
                try
                {
                    foreach (var item in box.Queue.GetConsumingEnumerable(_stop.Token))
                    {
                        Interlocked.Increment(ref owner._busyHandlers);
                        try { handle(item); }
                        catch (Exception) { /* a handler that throws must not kill the worker; the sale machine logs faults itself */ }
                        finally
                        {
                            box.Handled();
                            Interlocked.Decrement(ref owner._busyHandlers);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally { _running = false; }
            })
            {
                IsBackground = true,
                Name = $"worker:{name}",
            };
            _thread.Start();
        }

        public string Name { get; }
        public bool IsRunning => _running;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Cancel();
            if (!_thread.Join(TimeSpan.FromSeconds(2))) { /* background thread, process exit will take it */ }
            _stop.Dispose();
        }
    }
}
