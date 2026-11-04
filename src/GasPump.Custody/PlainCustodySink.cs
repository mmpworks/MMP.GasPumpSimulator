using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MMP.Herald.Events;

namespace GasPump.Custody;

/// <summary>
/// The custody file for a build without the TesseraSeal sources: the same
/// events, one JSON object per line after a header line, with no MAC and no
/// chain. Anyone can edit this file and nothing will notice. It keeps the
/// simulator whole for a reader of this repo. It is a record, never evidence.
/// </summary>
public sealed partial class PlainCustodySink : ICustodySink
{
    /// <summary>The reason a plain build gives for every verify.</summary>
    public const string NotChecked = "this build has no chain verifier, so the file was not checked. See \"Custody file\" in README.md.";

    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private readonly string _tenantId;
    private readonly string _runId;
    private long _seq;
    private bool _disposed;

    /// <summary>Opens the file for append. An existing file resumes after its highest sequence number, so a restart adds to the same run.</summary>
    public PlainCustodySink(string path, string tenantId, string runId)
    {
        _tenantId = tenantId;
        _runId = runId;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var resuming = File.Exists(path) && new FileInfo(path).Length > 0;
        var cutLine = resuming && EndsMidLine(path);
        if (resuming) _seq = HighestSeq(path);

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        if (cutLine) _writer.WriteLine();
        if (!resuming) _writer.WriteLine(Header());
    }

    public void Log(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
        {
            // An evidence sink never drops an event in silence, so a late write is an error the caller sees.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(Line(++_seq, logEvent));
        }
    }

    /// <summary>A plain file carries nothing to check, so this returns false and says why.</summary>
    public static bool Verify(out long? failureAtSeq, out string? reason)
    {
        failureAtSeq = null;
        reason = NotChecked;
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private string Header() => Json(w =>
    {
        w.WriteString("format", "gaspump.custody.plain.v1");
        w.WriteBoolean("chained", false);
        w.WriteString("tenant_id", _tenantId);
        w.WriteString("run_id", _runId);
    });

    // "seq" is written first, so a reader that takes the first "seq" on the line never lands on a property of the same name.
    private string Line(long seq, LogEvent e) => Json(w =>
    {
        w.WriteNumber("seq", seq);
        w.WriteString("time_utc", e.TimeUtc.ToString("o", CultureInfo.InvariantCulture));
        w.WriteString("level", e.Level.Key);
        w.WriteString("category", e.Category.Value);
        w.WriteString("message", e.Message);
        w.WriteString("tenant_id", _tenantId);
        w.WriteString("run_id", _runId);
        w.WriteStartObject("properties");
        foreach (var p in e.Properties) w.WriteString(p.Name, Convert.ToString(p.ResolvedValue, CultureInfo.InvariantCulture));
        w.WriteEndObject();
    });

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool EndsMidLine(string path)
    {
        using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        read.Seek(-1, SeekOrigin.End);
        return read.ReadByte() != '\n';
    }

    private static long HighestSeq(string path)
    {
        long highest = 0;
        using var read = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (read.ReadLine() is { } line)
        {
            var m = SeqAtLineStart().Match(line);
            if (m.Success) highest = Math.Max(highest, long.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture));
        }
        return highest;
    }

    [GeneratedRegex("^\\{\"seq\":(?<seq>\\d+)")]
    private static partial Regex SeqAtLineStart();
}
