using System.Collections.Concurrent;

namespace GasPump.Dispatch;

/// <summary>
/// The 2008 strategy. Each worker gets a dedicated thread, each mailbox is a
/// BlockingCollection, and timers from the TimeProvider post messages. A worker
/// thread waits in Take() when it has nothing to do, which costs one thread per
/// worker and nothing else. For three workers that is a fine trade. For three
/// thousand it is too many threads.
/// </summary>
public sealed class ThreadedDispatch(TimeProvider clock) : IDispatchStrategy
{
    // _gate protects the two lists below. It is held only for quick list changes,
    // and never while a handler runs.
    private readonly List<IDisposable> _owned = [];
    private readonly List<Func<bool>> _idleChecks = [];
    private readonly Lock _gate = new();
    // How many handlers are running right now. Workers on different threads
    // change it, so every change goes through Interlocked.
    private int _busyHandlers;

    public string Name => "threaded";
    public bool PrefersBlockingIo => true;

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
        // BlockingCollection does the hard part. Add waits while the queue is full,
        // and a reader waits while it is empty. The capacity is what makes it bounded.
        public readonly BlockingCollection<T> Queue = new(new ConcurrentQueue<T>(), capacity);
        private int _pending;
        public string Name => name;
        /// <summary>Posted and not yet fully handled. Includes the one a worker is holding right now.</summary>
        public int Count => Volatile.Read(ref _pending);
        // Post makes the caller wait when the mailbox is full. Count goes up first, so
        // the message is never in the queue uncounted. If the mailbox closes during the
        // wait, Add throws, and the count comes back down.
        public void Post(T item)
        {
            if (Queue.IsAddingCompleted) return;
            Interlocked.Increment(ref _pending);
            try { Queue.Add(item); } catch (InvalidOperationException) { Interlocked.Decrement(ref _pending); }
        }
        // TryPost never waits. A full mailbox returns false, and the caller decides what to do.
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
            // This worker owns one thread for its whole life, even when it has no work.
            // That is the cost of this strategy, and it is also the simplest to read.
            _thread = new Thread(() =>
            {
                try
                {
                    // The thread sleeps here until a message arrives. One loop means one message
                    // at a time, so handlers never need a lock against each other.
                    foreach (var item in box.Queue.GetConsumingEnumerable(_stop.Token))
                    {
                        // Raise the busy count before the handler and lower it after. The mailbox
                        // count drops first, so IsIdle can't see "empty and not busy" while this
                        // message is still being handled.
                        Interlocked.Increment(ref owner._busyHandlers);
                        try { handle(item); }
                        catch (Exception) { /* a handler that throws can't kill the worker; the sale machine logs faults itself */ }
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
                // A background thread won't keep the program alive after the main thread ends.
                IsBackground = true,
                Name = $"worker:{name}",
            };
            _thread.Start();
        }

        public string Name { get; }
        public bool IsRunning => _running;

        public void Dispose()
        {
            // Exchange sets the flag and returns the old value in one step. If two
            // threads call Dispose together, only the first one does the work.
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            // Wake the sleeping loop, then wait up to two seconds for its thread to finish.
            _stop.Cancel();
            if (!_thread.Join(TimeSpan.FromSeconds(2))) { /* background thread, process exit will take it */ }
            _stop.Dispose();
        }
    }
}
