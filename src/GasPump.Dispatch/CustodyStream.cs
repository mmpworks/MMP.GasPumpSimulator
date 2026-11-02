namespace GasPump.Dispatch;

/// <summary>
/// Tails the custody file and hands out each complete line, byte for byte,
/// as soon as it lands. The spec allows observation on the wire or on disk and
/// treats the two as equivalent, so the line a subscriber receives is the line
/// the verifier will read. A subscriber that appends what it receives to its
/// own file holds a second copy of the same chain. Only lines that end in a
/// newline are published. A partial last line waits for the rest.
/// </summary>
public sealed class CustodyStream(string path)
{
    private readonly Lock _gate = new();
    private long _offset;
    private readonly List<byte> _partial = [];

    public string Path { get; } = path;
    public long LinesPublished { get; private set; }

    /// <summary>One complete custody line, including the header line as the first event.</summary>
    public event Action<string>? Line;

    /// <summary>
    /// Attach a subscriber and first hand it every complete line already in the
    /// file, starting with the header, so a late joiner holds the whole chain
    /// and not just the tail. This runs under the lock so no line lands between
    /// the replay and the subscription.
    /// </summary>
    public void SubscribeWithReplay(Action<string> onLine)
    {
        lock (_gate)
        {
            if (File.Exists(Path))
            {
                using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                var text = reader.ReadToEnd();
                var complete = text.LastIndexOf('\n');
                if (complete >= 0)
                    foreach (var line in text[..complete].Split('\n')) onLine(line.TrimEnd('\r'));
                // Everything replayed counts as published. The next Pump starts after it.
                var replayedBytes = System.Text.Encoding.UTF8.GetByteCount(text[..(complete + 1)]);
                if (replayedBytes > _offset) { _offset = replayedBytes; _partial.Clear(); }
            }
            Line += onLine;
        }
    }

    /// <summary>
    /// Read whatever the sink appended since the last call and publish the
    /// complete lines. Subscribers are called under the lock, so two workers
    /// pumping at once can't hand a subscriber line 7 before line 6. Order is
    /// the chain, and a subscriber that appends what it gets needs it in order.
    /// </summary>
    public void Pump()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return;
            List<string> lines = [];
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length <= _offset) return;
                fs.Position = _offset;
                var buffer = new byte[fs.Length - _offset];
                var read = fs.Read(buffer, 0, buffer.Length);
                _offset += read;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        // The sink ends a line with the platform newline; the line itself is the JSON, with no CR.
                        lines.Add(System.Text.Encoding.UTF8.GetString(_partial.ToArray()).TrimEnd('\r'));
                        _partial.Clear();
                    }
                    else _partial.Add(buffer[i]);
                }
            }
            LinesPublished += lines.Count;
            foreach (var l in lines) Line?.Invoke(l);
        }
    }
}
