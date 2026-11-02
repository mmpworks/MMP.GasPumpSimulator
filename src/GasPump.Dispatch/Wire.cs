using System.Text.Json;
using System.Text.Json.Serialization;

namespace GasPump.Dispatch;

/// <summary>
/// A device on the forecourt wire: a (subnet, node) pair, the way the IFSF
/// standard addresses one. The station is subnet 1, node 1. Pumps are subnet
/// 2, numbered. Broadcast is (0, 0) and is used only by heartbeats.
/// </summary>
public readonly record struct WireNode(int Subnet, int Node)
{
    public static readonly WireNode Station = new(1, 1);
    public static readonly WireNode Broadcast = new(0, 0);
    public static WireNode Pump(int number) => new(2, number);
    public override string ToString() => $"{Subnet}.{Node}";
}

/// <summary>What kind of frame this is. The receiver routes on it before it looks inside.</summary>
public enum WireKind
{
    /// <summary>Something to do. Carries a correlation id; an Answer comes back with the same id.</summary>
    Command,
    /// <summary>Something that happened. No answer.</summary>
    Event,
    /// <summary>The result of a Command, matched by correlation id.</summary>
    Answer,
    /// <summary>I am alive, here is my status and my last sequence number. Once a second, each way.</summary>
    Heartbeat,
    /// <summary>Send me something: the frames I missed, your payable transactions, or your totals.</summary>
    Read,
    /// <summary>Data the station sets on the device, such as the price board. Carries a correlation id and is answered like a Command.</summary>
    Write,
}

/// <summary>What a Command came back as.</summary>
public enum AnswerCode
{
    /// <summary>Ran. The events it caused follow or came first.</summary>
    Done,
    /// <summary>Did not run, by the device's own rule. Detail names the reason or code.</summary>
    Refused,
    /// <summary>Arrived after its time to live. Never run.</summary>
    Stale,
    /// <summary>A payable transaction is still in the buffer. Clear it first.</summary>
    Busy,
    /// <summary>The device does not know this command.</summary>
    Unknown,
}

public sealed record WireAnswer(string CorrelationId, AnswerCode Code, string? Detail = null);

/// <summary>The sender's pulse: its run, its one-word status, and how far its own sequence has gone, so the other side can see a gap.</summary>
public sealed record WireHeartbeat(string RunId, string Status, long LastSeqSent, long LastSeqReceived);

/// <summary>Send me frames from this sequence number on (gap fill), your payable transactions, or your totals.</summary>
public sealed record WireRead(string What, long? FromSeq = null, long? ToSeq = null)
{
    public const string Frames = "frames", Transactions = "transactions", Totals = "totals";
}

/// <summary>
/// The envelope every line on the wire wears. The body is the same record
/// the two mailboxes passed before there was a wire; the envelope adds what
/// a device protocol needs and a message bus does not: who, to whom, in what
/// order, what kind, which request this answers, when it was sent, and how
/// long it is good for.
/// </summary>
public sealed record WireFrame(
    int Version,
    long Seq,
    WireNode Source,
    WireNode Dest,
    WireKind Kind,
    DateTimeOffset SentAt,
    LinkFrame Body,
    string? CorrelationId = null,
    TimeSpan? Ttl = null)
{
    public const int CurrentVersion = 1;

    /// <summary>True when a Command's time to live has run out by the receiver's clock.</summary>
    public bool IsStale(DateTimeOffset now) => Ttl is { } ttl && now - SentAt > ttl;
}

/// <summary>Why a frame was refused at the codec or the receiver.</summary>
public enum FrameFault { None, BadJson, WrongVersion, WrongDest }

/// <summary>
/// Record to line and line to record, in one place for both processes.
/// Nothing above it sees a frame. A line it cannot read is a fault with a
/// reason, never an exception, because the wire is not trusted.
/// </summary>
public static class FrameCodec
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(WireFrame f) => JsonSerializer.Serialize(f, Options);

    public static (WireFrame? Frame, FrameFault Fault, string? Detail) Decode(string line, WireNode me)
    {
        WireFrame? f;
        try { f = JsonSerializer.Deserialize<WireFrame>(line, Options); }
        catch (JsonException ex) { return (null, FrameFault.BadJson, ex.Message); }
        if (f is null) return (null, FrameFault.BadJson, "empty");
        if (f.Version != WireFrame.CurrentVersion) return (null, FrameFault.WrongVersion, $"version {f.Version}, this side speaks {WireFrame.CurrentVersion}");
        if (f.Dest != me && f.Dest != WireNode.Broadcast) return (null, FrameFault.WrongDest, $"for {f.Dest}, this is {me}");
        return (f, FrameFault.None, null);
    }
}

/// <summary>
/// One sender's side of the sequence rule. Hands out the next number and
/// keeps a ring of the last frames so a Read for a gap can be answered. The
/// ring is small on purpose: a gap older than the ring is a fault to report,
/// not a replay to attempt.
/// </summary>
public sealed class WireSequencer(WireNode me, int ring = 512)
{
    private readonly Lock _gate = new();
    private readonly Queue<WireFrame> _ring = new();
    private long _seq;

    public WireNode Me => me;
    public long LastSeq { get { lock (_gate) return _seq; } }

    /// <summary>
    /// Wrap a body in the next envelope and remember it. A heartbeat takes no
    /// number and is never kept: it carries the sender's last number instead,
    /// so the receiver can see a gap at the tail with nothing after it.
    /// </summary>
    public WireFrame Next(WireKind kind, WireNode dest, LinkFrame body, DateTimeOffset now, string? correlationId = null, TimeSpan? ttl = null)
    {
        lock (_gate)
        {
            if (kind == WireKind.Heartbeat)
            {
                var hb = body.Hb is { } h ? body with { Hb = h with { LastSeqSent = _seq } } : body;
                return new WireFrame(WireFrame.CurrentVersion, 0, me, dest, kind, now, hb, correlationId, ttl);
            }
            var f = new WireFrame(WireFrame.CurrentVersion, ++_seq, me, dest, kind, now, body, correlationId, ttl);
            _ring.Enqueue(f);
            while (_ring.Count > ring) _ring.Dequeue();
            return f;
        }
    }

    /// <summary>
    /// Build the next frame and hand it to the writer under one lock, so the
    /// wire order is the sequence order and a heartbeat's last-number claim is
    /// true. A frame the writer could not send (no peer on the line) gives its
    /// number back, so the peer that connects later sees no gap for frames
    /// that were never on the wire. Returns what the writer returned.
    /// </summary>
    public bool Send(WireKind kind, WireNode dest, LinkFrame body, DateTimeOffset now, Func<WireFrame, bool> write, string? correlationId = null, TimeSpan? ttl = null)
    {
        lock (_gate)
        {
            var f = Next(kind, dest, body, now, correlationId, ttl);
            if (write(f)) return true;
            if (kind != WireKind.Heartbeat)
            {
                _seq--;
                // The unsent frame is the newest in the ring; drop it by rebuilding without it.
                var kept = _ring.Where(x => x.Seq != f.Seq).ToList();
                _ring.Clear();
                foreach (var k in kept) _ring.Enqueue(k);
            }
            return false;
        }
    }

    /// <summary>Every remembered frame with a sequence number in the range asked for, in order. Empty when the gap is older than the ring.</summary>
    public IReadOnlyList<WireFrame> Replay(long fromSeq, long? toSeq = null)
    {
        lock (_gate) return _ring.Where(f => f.Seq >= fromSeq && (toSeq is null || f.Seq <= toSeq)).ToList();
    }

    /// <summary>True when the oldest remembered frame is at or before the one asked for, so a replay from there is complete.</summary>
    public bool CanReplayFrom(long fromSeq)
    {
        lock (_gate) return _ring.Count > 0 && _ring.Peek().Seq <= fromSeq;
    }
}

/// <summary>Ok is in order. Filled is a frame that arrived late to fill a reported gap: real, but out of order. Gap is the first frame after missing ones.</summary>
public enum Accept { Ok, Duplicate, Gap, Filled }

/// <summary>
/// One receiver's side of the sequence rule, per peer. Tells the caller
/// whether a frame is new, a duplicate it already handled, or the first frame
/// after a gap, and in that case which numbers are missing. Heartbeats are
/// counted but never gap-checked, because they are not replayed.
/// </summary>
public sealed class WireReceiver
{
    private readonly Lock _gate = new();
    private long _last;
    private readonly HashSet<long> _seen = [];

    public long LastSeq { get { lock (_gate) return _last; } }
    public long Gaps { get; private set; }
    public long Duplicates { get; private set; }

    /// <summary>A new connection starts the peer's numbering over.</summary>
    public void Reset() { lock (_gate) { _last = 0; _seen.Clear(); } }

    /// <summary>Returns the verdict and, on a gap, the first and last missing number.</summary>
    public (Accept Verdict, long MissingFrom, long MissingTo) Check(WireFrame f)
    {
        lock (_gate)
        {
            if (f.Kind == WireKind.Heartbeat)
            {
                // No number of its own. Its claim of the sender's last number shows frames lost with nothing after them.
                if (f.Body.Hb is { } hb && hb.LastSeqSent > _last)
                {
                    Gaps++;
                    var (gf, gt) = (_last + 1, hb.LastSeqSent);
                    for (var s = gf; s <= gt; s++) _seen.Add(s);
                    _last = hb.LastSeqSent;
                    return (Accept.Gap, gf, gt);
                }
                return (Accept.Ok, 0, 0);
            }
            if (f.Seq <= _last)
            {
                if (_seen.Remove(f.Seq)) return (Accept.Filled, 0, 0);
                Duplicates++;
                return (Accept.Duplicate, 0, 0);
            }
            if (f.Seq > _last + 1)
            {
                Gaps++;
                var (from, to) = (_last + 1, f.Seq - 1);
                for (var s = from; s <= to; s++) _seen.Add(s);   // the numbers we still owe; a replay fills them
                _last = f.Seq;
                return (Accept.Gap, from, to);
            }
            if (f.Seq == _last + 1) _last = f.Seq;
            return (Accept.Ok, 0, 0);
        }
    }

    /// <summary>Sequence numbers reported as a gap and not filled yet.</summary>
    public IReadOnlyCollection<long> Outstanding { get { lock (_gate) return _seen.ToArray(); } }
}

/// <summary>
/// The heartbeat watchdog. Alive while beats arrive within the window; two
/// missed beats is dead. This is the difference between "the socket closed"
/// (the transport) and "the device stopped talking" (the peer), which are
/// two different faults with two different repairs.
/// </summary>
public sealed class Liveness(TimeSpan period, int missedBeatsAllowed = 2)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastBeat;
    private WireHeartbeat? _last;

    public TimeSpan Period => period;
    public TimeSpan Window => period * (missedBeatsAllowed + 1);
    public DateTimeOffset? LastBeatAt { get { lock (_gate) return _lastBeat; } }
    public WireHeartbeat? Last { get { lock (_gate) return _last; } }

    public void Beat(WireHeartbeat hb, DateTimeOffset now) { lock (_gate) { _lastBeat = now; _last = hb; } }
    public void Reset() { lock (_gate) { _lastBeat = null; _last = null; } }

    /// <summary>True until the first beat has been seen for longer than the window.</summary>
    public bool IsAlive(DateTimeOffset now)
    {
        lock (_gate) return _lastBeat is null || now - _lastBeat.Value <= Window;
    }
}
