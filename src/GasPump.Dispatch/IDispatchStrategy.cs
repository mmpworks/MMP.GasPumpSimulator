namespace GasPump.Dispatch;

/// <summary>
// Quick vocabulary for the threading code in this folder.
// Message: a small record that says what happened, such as "nozzle lifted".
// Mailbox: the line of messages waiting for one worker.
// Worker: a loop that takes one message, handles it, and takes the next.
// Handler: the method the worker calls for each message.
// Thread: one path of running code. The system can run many at once.
/// A bounded queue between two workers. Post waits when the queue is full,
/// because a pulse stream can't drop a count. GEM:back-pressure-ring: the
/// producer waits so the consumer never loses data.
/// </summary>
public interface IMailbox<T>
{
    string Name { get; }
    int Count { get; }
    void Post(T item);
    bool TryPost(T item);
}

public interface IWorkerHandle : IDisposable
{
    string Name { get; }
    bool IsRunning { get; }
}

/// <summary>
/// The Strategy. Three things differ between a thread-per-worker design, a
/// Task-and-Channel design, and a hand-built state machine design: how workers
/// run, how mailboxes block, and how time arrives. Everything above this
/// interface is the same code in all three.
///
/// Rules every strategy keeps:
///   1. A worker handles one message at a time, on one logical thread. Handlers never run concurrently.
///   2. Timers never run code. They post a message. The worker runs the code.
///   3. Workers never share state. They pass messages.
///
/// The third strategy is AsyncProxyDispatch, built on MMP.AsyncProxy. Each
/// worker is an iterator-driven state machine and the mailbox is a promise per
/// message.
/// </summary>
// Swap the strategy and the pump code above it stays the same. Compare the
// three versions to see what each one spends while it waits for a message:
// ThreadedDispatch parks a whole thread. The other two hold no thread at all.
public interface IDispatchStrategy : IDisposable
{
    string Name { get; }

    /// <summary>True when a worker should block its own thread on I/O instead of continuing on a callback.</summary>
    bool PrefersBlockingIo { get; }

    IMailbox<T> CreateMailbox<T>(string name, int capacity);

    /// <summary>Starts a worker that drains the mailbox through the handler until disposed.</summary>
    IWorkerHandle StartWorker<T>(string name, IMailbox<T> inbox, Action<T> handle);

    /// <summary>A repeating clock that posts a message each period. Disposing stops it.</summary>
    IDisposable StartTicker<T>(string name, TimeSpan period, IMailbox<T> target, Func<T> make);

    /// <summary>A one-shot clock that posts a message once. Disposing before it fires cancels it.</summary>
    IDisposable StartTimer<T>(string name, TimeSpan due, IMailbox<T> target, Func<T> make);

    /// <summary>True when every mailbox is empty and no handler is mid-message. Tests use it to wait for quiet.</summary>
    bool IsIdle { get; }
}
