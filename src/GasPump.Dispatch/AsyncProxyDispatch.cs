using MMP.AsyncProxy;

namespace GasPump.Dispatch;

/// <summary>
/// The third strategy: the 2008 machine without the 2008 threads. Each worker
/// is an iterator-driven state machine from MMP.AsyncProxy. Its loop yields a
/// future for the next message. The future completes when a message is posted,
/// and the loop resumes on a pool thread to run the handler. A worker with
/// nothing to do holds no thread, the same as in the Channel strategy. Every
/// piece here would compile for .NET 3.5: futures, promises, and a C# 2
/// iterator standing in for async.
///
/// The three rules from the interface apply. A worker handles one message at a
/// time, because the loop is sequential. Timers post and never run code.
/// Workers share nothing. Time still comes from the TimeProvider, so a test can
/// drive it from a fake clock. Only the waiting changed.
/// </summary>
public sealed class AsyncProxyDispatch(TimeProvider clock) : IDispatchStrategy
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<Func<bool>> _idleChecks = [];
    private readonly Lock _gate = new();
    private int _busyHandlers;

    public string Name => "asyncproxy";
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

    /// <summary>
    /// A bounded queue whose consumer waits on a promise instead of a thread.
    /// When the worker asks for the next item and the queue is empty, it gets
    /// a pending future. The next Post completes that future with the item,
    /// and the worker's loop resumes on the pool. Count is the number of items
    /// posted and not yet handled, which includes the item a handler holds now.
    /// </summary>
    private sealed class Mailbox<T>(string name, int capacity) : IMailbox<T>, IDisposable
    {
        private readonly Lock _lock = new();
        private readonly Queue<T> _items = new();
        private Promise<T>? _waiting;
        private int _pending;
        private bool _closed;

        public string Name => name;
        public int Count => Volatile.Read(ref _pending);

        public void Post(T item)
        {
            // Bounded: a full box makes the producer wait. The other two strategies do the same.
            while (!TryPost(item))
            {
                if (_closed) return;
                Thread.Yield();
            }
        }

        public bool TryPost(T item)
        {
            Promise<T>? wake = null;
            lock (_lock)
            {
                if (_closed) return false;
                if (_waiting is { } w) { _waiting = null; wake = w; }
                else if (_items.Count >= capacity) return false;
                else _items.Enqueue(item);
                Interlocked.Increment(ref _pending);
            }
            // Outside the lock. The promise runs continuations on the pool, so this returns right away.
            wake?.TrySetResult(item);
            return true;
        }

        /// <summary>
        /// The fast path. A message that is already queued comes out with no promise, no
        /// future, and no park. The worker tries this first and awaits only when it fails,
        /// the way a Channel reader tries TryRead before it waits. Row 7 of the training
        /// track's improvements table in MMP.AsyncProxy: the await-every-time loop paid two
        /// objects per message for a value it already had.
        /// </summary>
        public bool TryDequeue(out T item)
        {
            lock (_lock)
            {
                if (_items.Count > 0) { item = _items.Dequeue(); return true; }
                item = default!;
                return false;
            }
        }

        /// <summary>The worker's await, for when TryDequeue found nothing. Completed already if an item landed in between, pending otherwise.</summary>
        public IFuture<T> DequeueAsync()
        {
            lock (_lock)
            {
                if (_closed) return Canceled();
                if (_items.Count > 0)
                {
                    var ready = new Promise<T>(true);
                    ready.SetResult(_items.Dequeue());
                    return ready.Future;
                }
                _waiting = new Promise<T>(true);
                return _waiting.Future;
            }
        }

        internal void Handled() => Interlocked.Decrement(ref _pending);

        private static IFuture<T> Canceled()
        {
            var p = new Promise<T>(true);
            p.SetCanceled();
            return p.Future;
        }

        public void Dispose()
        {
            Promise<T>? wake;
            lock (_lock)
            {
                _closed = true;
                wake = _waiting;
                _waiting = null;
                _items.Clear();
            }
            wake?.TrySetCanceled();
        }
    }

    /// <summary>
    /// One worker is one iterator, and "yield return next" is the await. The
    /// loop ends when the mailbox closes and its pending future cancels.
    /// </summary>
    private sealed class Worker<T> : IWorkerHandle
    {
        private readonly IFuture _loop;
        private readonly Mailbox<T> _box;
        private volatile bool _stopping;
        private int _disposed;

        public Worker(string name, Mailbox<T> box, Action<T> handle, AsyncProxyDispatch owner)
        {
            Name = name;
            _box = box;
            _loop = AsyncProxy.Run(Loop(box, handle, owner));
        }

        private IEnumerable<IFuture> Loop(Mailbox<T> box, Action<T> handle, AsyncProxyDispatch owner)
        {
            while (!_stopping)
            {
                if (!box.TryDequeue(out var item))
                {
                    // Nothing queued: now, and only now, park on the mailbox's promise.
                    var next = box.DequeueAsync();
                    // Observe the future so a cancelled dequeue can't fault the loop. A cancel is how the mailbox says "closed".
                    yield return Futures.Observe(next);
                    if (next.IsCanceled || next.IsFaulted || _stopping) yield break;
                    item = next.Result;
                }

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

        public string Name { get; }
        public bool IsRunning => !_loop.IsCompleted;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stopping = true;
            _box.Dispose();
            // The loop's pending future cancels on close. Wait up to two seconds for the machine to notice,
            // on the future's own event rather than a spin: IFuture.Wait(int) arrived in MMP.AsyncProxy 0.2.0.
            _loop.Wait(2000);
        }
    }
}
