using System.Text.RegularExpressions;

namespace GasPump.Dispatch;

/// <summary>
/// A second copy of a dispenser's custody chain, one file per dispenser run,
/// written line for line as the lines arrive. The chain is append-only and
/// every event line carries its sequence number, so the copy keeps the last
/// number it wrote and drops any line at or below it. That is what makes a
/// replay safe: a station restart, a re-bind after a station swap, and a
/// socket reconnect each hand the whole chain over again, and the copy grows
/// by the lines it was missing and nothing else.
/// </summary>
public sealed partial class CustodyCopy
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly Lock _gate = new();
    private long _lastSeq = -1;

    /// <param name="label">Names the copy's file beside the run id (<c>station-copy-P1-RUN.ndjson</c>), so two pumps' copies never share a file. Null keeps the one-pump name.</param>
    public CustodyCopy(string directory, string? label = null)
    {
        _directory = directory;
        _prefix = label is null ? "" : label + "-";
    }

    public string? Path { get; private set; }
    /// <summary>The dispenser run the current copy belongs to.</summary>
    public string? RunId { get; private set; }
    /// <summary>Lines in the current copy, including the header line.</summary>
    public long Lines { get; private set; }
    /// <summary>Lines handed in and dropped because the copy already held them.</summary>
    public long Duplicates { get; private set; }

    /// <summary>
    /// Append one custody line. Returns true when the line was new and was
    /// written, false when the copy already held it. The header line has no
    /// sequence number and counts as 0.
    /// </summary>
    public bool Append(string runId, string line)
    {
        lock (_gate)
        {
            if (runId != RunId) OpenRun(runId);
            var seq = SeqOf(line);
            if (seq <= _lastSeq) { Duplicates++; return false; }
            File.AppendAllText(Path!, line + "\n");
            Lines++;
            _lastSeq = seq;
            return true;
        }
    }

    /// <summary>Under the lock. Point at the run's file and read how far it already goes.</summary>
    private void OpenRun(string runId)
    {
        RunId = runId;
        Path = System.IO.Path.Combine(_directory, $"station-copy-{_prefix}{runId}.ndjson");
        _lastSeq = -1;
        Lines = 0;
        if (!File.Exists(Path)) return;
        foreach (var existing in File.ReadLines(Path))
        {
            if (existing.Length == 0) continue;
            Lines++;
            _lastSeq = Math.Max(_lastSeq, SeqOf(existing));
        }
    }

    /// <summary>The event's sequence number from its audit_chain block, or 0 for the header line.</summary>
    public static long SeqOf(string line)
    {
        var m = SeqPattern().Match(line);
        return m.Success ? long.Parse(m.Groups["seq"].Value) : 0;
    }

    [GeneratedRegex("\"seq\":(?<seq>\\d+)")]
    private static partial Regex SeqPattern();
}
