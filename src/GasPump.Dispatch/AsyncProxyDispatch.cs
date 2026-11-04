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
    // A future is a promise of a value that isn't ready yet. Code can pause on a
    // future without holding a thread. A promise is the other side: the code that
    // has the value calls SetResult, and that wakes whoever paused.
    //
    // _gate protects the two lists below. It is held only for quick list changes,
    // and never while a handler runs.
    private readonly List<IDisposable> _owned = [];
    private readonly List<Func<bool>> _idleChecks = [];
    private readonly Lock _gate = new();
    // How many handlers are running right now. Workers on different threads
    // change it, so every change goes through Interlocked.
    private int _busyHandlers;

    public string Name => "asyncproxy";
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

    /// <summary>
    /// A bounded queue whose consumer waits on a promise instead of a thread.
    /// When the worker asks for the next item and the queue is empty, it gets
    /// a pending future. The next Post completes that future with the item,
    /// and the worker's loop resumes on the pool. Count is the number of items
    /// posted and not yet handled, which includes the item a handler holds now.
    /// </summary>
    private sealed class Mailbox<T>(string name, int capacity) : IMailbox<T>, IDisposable
    {
        // _lock protects the queue, the waiting promise, and the closed flag together.
        private readonly Lock _lock = new();
        private readonly Queue<T> _items = new();
        // Set when the worker is paused on an empty mailbox. There is one worker per
        // mailbox, so one slot is enough.
        private Promise<T>? _waiting;
        // Posted and not yet handled. Count reads it without taking the lock.
        private int _pending;
        private bool _closed;

        public string Name => name;
        public int Count => Volatile.Read(ref _pending);

        public void Post(T item)
        {
            // Bounded: a full box makes the producer wait. The other two strategies do the same.
            // The producer keeps its thread and retries. Thread.Yield lets other threads run between tries.
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
                // If the worker is paused, hand it this message directly and skip the queue.
                // Otherwise the message goes in the queue, unless the queue is full.
                if (_waiting is { } w) { _waiting = null; wake = w; }
                else if (_items.Count >= capacity) return false;
                else _items.Enqueue(item);
                Interlocked.Increment(ref _pending);
            }
            // Outside the lock. The promise runs continuations on the pool, so this returns right away.
            // Wake the worker after the lock is released. Waking it inside the lock would
            // mix our lock with the promise's own work, which is how deadlocks start.
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
                // This check and the line that sets _waiting sit under one lock. Without that,
                // Post could add a message right after the check and before the worker parks.
                // The worker would then sleep with a message sitting in its queue. People call
                // that a lost wakeup.
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
            // A cancelled future is how the paused worker learns that the mailbox closed.
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
        // Dispose sets this on one thread and the loop reads it on another. Volatile
        // makes sure the loop sees the new value.
        private volatile bool _stopping;
        private int _disposed;

        public Worker(string name, Mailbox<T> box, Action<T> handle, AsyncProxyDispatch owner)
        {
            Name = name;
            _box = box;
            // AsyncProxy.Run drives the iterator below. Each time it yields a future, Run waits for that future and then resumes the loop.
            _loop = AsyncProxy.Run(Loop(box, handle, owner));
        }

        // The compiler turns this method into a state machine, so it can stop at a
        // yield and pick up there later. That is how await worked by hand before C# 5.
        private IEnumerable<IFuture> Loop(Mailbox<T> box, Action<T> handle, AsyncProxyDispatch owner)
        {
            while (!_stopping)
            {
                if (!box.TryDequeue(out var item))
                {
                    // Nothing queued: now, and only now, park on the mailbox's promise.
                    var next = box.DequeueAsync();
                    // Observe the future so a cancelled dequeue can't fault the loop. A cancel is how the mailbox says "closed".
                    // The method stops here and no thread is held. When a message arrives, a pool
                    // thread resumes the loop on the next line.
                    yield return Futures.Observe(next);
                    if (next.IsCanceled || next.IsFaulted || _stopping) yield break;
                    item = next.Result;
                }

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

        public string Name { get; }
        public bool IsRunning => !_loop.IsCompleted;

        public void Dispose()
        {
            // Exchange sets the flag and returns the old value in one step. If two
            // threads call Dispose together, only the first one does the work.
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            // Set the flag, then close the mailbox. Closing wakes a paused loop so it can see the flag and exit.
            _stopping = true;
            _box.Dispose();
            // The loop's pending future cancels on close. Wait up to two seconds for the machine to notice,
            // on the future's own event rather than a spin: IFuture.Wait(int) arrived in MMP.AsyncProxy 0.2.0.
            _loop.Wait(2000);
        }
    }
}
