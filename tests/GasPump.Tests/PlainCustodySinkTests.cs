using GasPump.Custody;
using GasPump.Dispatch;
using Xunit;

namespace GasPump.Tests;

/// <summary>A fact about the TesseraSeal chain. A clone without the Herald.Compliance sources skips it by name.</summary>
public sealed class ChainFactAttribute : FactAttribute
{
    public ChainFactAttribute()
    {
        if (!CustodyFile.ChainAvailable) Skip = "This build has no TesseraSeal chain: the Herald.Compliance sources are absent.";
    }
}

/// <summary>
/// The plain custody file runs in every build, so these pin it whichever way
/// the build went. The promises: same events, a sequence number the station's
/// copy can use, and a verify that never claims to have checked anything.
/// </summary>
public sealed class PlainCustodySinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-plain-" + Guid.NewGuid().ToString("N"));
    private string CustodyPath => Path.Combine(_dir, "custody-RUN-1.ndjson");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void WriteEvents(params string[] messages)
    {
        using var sink = new PlainCustodySink(CustodyPath, "tenant", "RUN-1");
        using var log = new HeraldEventLog(TimeProvider.System, null, (sink, _ => true));
        foreach (var message in messages) log.Write("ledger", message + " {Pump}", ("Pump", "PUMP-01"));
        WaitForLines(messages.Length);
    }

    // The log hands events to its bridge on its own worker, so the lines land shortly after the writes.
    private void WaitForLines(int events)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReadShared().Count(l => CustodyCopy.SeqOf(l) > 0) < events && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    private string[] ReadShared()
    {
        using var reader = new StreamReader(new FileStream(CustodyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public void A_new_file_starts_with_a_header_and_every_event_carries_a_climbing_sequence()
    {
        WriteEvents("sale.open", "valve.close");

        var lines = File.ReadAllLines(CustodyPath);
        Assert.Contains("\"chained\":false", lines[0]);
        Assert.Equal(0, CustodyCopy.SeqOf(lines[0]));
        Assert.Equal([1L, 2L], lines.Skip(1).Select(CustodyCopy.SeqOf));
        Assert.Contains("sale.open", lines[1]);
        Assert.Contains("PUMP-01", lines[1]);
        Assert.Contains("valve.close", lines[2]);
    }

    [Fact]
    public void A_restart_resumes_the_sequence_and_writes_no_second_header()
    {
        WriteEvents("first");
        using (var sink = new PlainCustodySink(CustodyPath, "tenant", "RUN-1"))
        using (var log = new HeraldEventLog(TimeProvider.System, null, (sink, _ => true)))
        {
            log.Write("ledger", "second");
            WaitForLines(2);
        }

        var lines = File.ReadAllLines(CustodyPath);
        Assert.Single(lines, l => l.Contains("\"chained\":false"));
        Assert.Equal([1L, 2L], lines.Skip(1).Select(CustodyCopy.SeqOf));
    }

    [Fact]
    public void A_line_cut_by_a_crash_stays_on_its_own_line()
    {
        WriteEvents("first");
        File.AppendAllText(CustodyPath, "{\"seq\":2,\"time_utc\":\"cut her");

        WriteEvents("second");

        var lines = File.ReadAllLines(CustodyPath);
        Assert.Equal("{\"seq\":2,\"time_utc\":\"cut her", lines[2]);
        Assert.Equal(3, CustodyCopy.SeqOf(lines[3]));
        Assert.Contains("second", lines[3]);
    }

    [Fact]
    public void The_stations_copy_drops_a_replayed_plain_line()
    {
        WriteEvents("first", "second");
        var copy = new CustodyCopy(_dir);

        foreach (var line in File.ReadAllLines(CustodyPath)) Assert.True(copy.Append("RUN-1", line));
        foreach (var line in File.ReadAllLines(CustodyPath)) Assert.False(copy.Append("RUN-1", line));

        Assert.Equal(3, copy.Lines);
        Assert.Equal(File.ReadAllLines(CustodyPath), File.ReadAllLines(copy.Path!));
    }

    [Fact]
    public void A_write_after_dispose_is_an_error_the_caller_sees()
    {
        var sink = new PlainCustodySink(CustodyPath, "tenant", "RUN-1");
        sink.Dispose();
        sink.Dispose();

        MMP.Herald.Events.LogEvent? late = null;
        using (var log = new HeraldEventLog(TimeProvider.System, null, (new Tap(e => Volatile.Write(ref late, e)), _ => true)))
        {
            log.Write("ledger", "late");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref late) is null && DateTime.UtcNow < deadline) Thread.Sleep(10);
        }

        Assert.NotNull(late);
        Assert.Throws<ObjectDisposedException>(() => sink.Log(late));
    }

    [Fact]
    public void Verify_claims_nothing()
    {
        Assert.False(PlainCustodySink.Verify(out var seq, out var reason));
        Assert.Null(seq);
        Assert.Equal(PlainCustodySink.NotChecked, reason);
    }

    [Fact]
    public void The_verdict_word_never_says_pass_or_fail_for_a_check_that_did_not_run()
    {
        Assert.Equal("PASS", CustodyFile.Verdict(true));
        Assert.Equal(CustodyFile.ChainAvailable ? "FAIL" : "NOT CHECKED", CustodyFile.Verdict(false));
    }

    private sealed class Tap(Action<MMP.Herald.Events.LogEvent> onEvent) : MMP.Herald.ILogger
    {
        public void Log(MMP.Herald.Events.LogEvent logEvent) => onEvent(logEvent);
    }
}
