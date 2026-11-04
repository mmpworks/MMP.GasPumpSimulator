using System.Threading.Channels;

namespace GasPump.Dispatch;

/// <summary>
/// The modern strategy. Each worker is a Task that awaits a bounded Channel, so
/// a worker with nothing to do holds no thread. The three rules from the
/// interface apply, with the same handlers and the same tests. Only the
/// plumbing changed.
/// </summary>
public sealed class ChannelDispatch(TimeProvider clock) : IDispatchStrategy
{
    // _gate protects the two lists below. It is held only for quick list changes,
    // and never while a handler runs.
    private readonly List<IDisposable> _owned = [];
    private readonly List<Func<bool>> _idleChecks = [];
    private readonly Lock _gate = new();
    // How many handlers are running right now. Workers on different threads
    // change it, so every change goes through Interlocked.
    private int _busyHandlers;

    public string Name => "channel";
    public bool PrefersBlockingIo => false;

    // IsIdle answers one question for the tests: has everything stopped moving?
    // It is true when no handler is running and every mailbox is empty.
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
        // A timer fires on a thread pool thread. It only posts a message, so the
        // worker still does all the work, one message at a time. TryPost never blocks.
        // If the mailbox is full, this tick is lost and the next tick tries again.
        var timer = clock.CreateTimer(_ => target.TryPost(make()), null, period, period);
        lock (_gate) _owned.Add(timer);
        return timer;
    }

    public IDisposable StartTimer<T>(string name, TimeSpan due, IMailbox<T> target, Func<T> make)
    {
        // Same idea, but it fires once and then cleans itself up. TryPost means a
        // full mailbox loses this message, and no second try comes.
        ITimer? timer = null;
        timer = clock.CreateTimer(_ => { target.TryPost(make()); timer?.Dispose(); }, null, due, Timeout.InfiniteTimeSpan);
        lock (_gate) _owned.Add(timer);
        return timer;
    }

    public void Dispose()
    {
        // Copy the list and clear it under the lock, then stop things outside the
        // lock. A worker can take up to two seconds to stop, and nobody should wait on
        // _gate that long. Reverse order stops the newest things first, so workers and
        // timers stop before the mailboxes they were created to use.
        List<IDisposable> owned;
        lock (_gate) { owned = _owned.ToList(); _owned.Clear(); }
        foreach (var d in owned.AsEnumerable().Reverse()) d.Dispose();
    }

    private sealed class Mailbox<T>(string name, int capacity) : IMailbox<T>, IDisposable
    {
        // A Channel is .NET's built-in queue for async code. SingleReader tells it
        // that only one worker reads, which lets it use a faster path.
        public readonly Channel<T> Channel = System.Threading.Channels.Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // Messages posted and not yet handled. TryPost raises it after the write, so a
        // fast worker can finish first and the count can read one low for a moment.
        private int _count;
        public string Name => name;
        public int Count => Volatile.Read(ref _count);
        public void Post(T item)
        {
            // Bounded + Wait: a full box makes the producer wait, synchronously here because
            // the callers are workers with their own thread or a timer callback.
            while (!TryPost(item)) Thread.Yield();
        }
        // TryWrite returns false right away when the channel is full. It never waits.
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
            // This loop is a Task, and it holds no thread while it waits. When a message
            // arrives, a pool thread picks up the loop and runs the handler.
            _loop = Task.Run(async () =>
            {
                try
                {
                    // The await gives the thread back while the channel is empty. The loop still
                    // handles one message at a time, even if a different thread runs each pass.
                    await foreach (var item in box.Channel.Reader.ReadAllAsync(_stop.Token))
                    {
                        // Raise the busy count before the handler and lower it after. The mailbox
                        // count drops first, so IsIdle can't see "empty and not busy" while this
                        // message is still being handled.
                        Interlocked.Increment(ref owner._busyHandlers);
                        // The handler is ordinary blocking code. While it runs, it holds its pool thread.
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
            // Exchange sets the flag and returns the old value in one step. If two
            // threads call Dispose together, only the first one does the work.
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            // Cancel wakes the waiting loop. Then wait up to two seconds for it to finish.
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
