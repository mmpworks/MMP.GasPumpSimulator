using GasPump.Core;
using GasPump.Custody;
using GasPump.Dispatch;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The station's copy of the dispenser's chain must survive a replay. Every
/// path that hands the chain over again (a station restart, a re-bind, a
/// socket reconnect) starts from the header, so without a sequence check the
/// copy would hold every line twice and the verifier would reject it.
/// </summary>
public class CustodyCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaspump-copy-" + Guid.NewGuid().ToString("N"));

    public CustodyCopyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>The sink keeps the chain file open, so a reader has to share.</summary>
    private static string[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static string Header(string run) => $"{{\"chain_id\":\"{run}\",\"format_version\":\"v1\"}}";
    private static string Event(long seq) => $"{{\"audit_chain\":{{\"format_version\":\"v1\",\"prev_hash\":\"x\",\"seq\":{seq}}},\"category\":\"ledger\",\"message\":\"line {seq}\"}}";

    [Fact]
    public void A_replay_of_the_whole_chain_adds_nothing_to_the_copy()
    {
        var copy = new CustodyCopy(_dir);
        string[] chain = [Header("RUN-1"), Event(1), Event(2), Event(3)];
        foreach (var l in chain) Assert.True(copy.Append("RUN-1", l));
        Assert.Equal(4, copy.Lines);

        // The dispenser hands the chain over again, header first, then two new lines land.
        foreach (var l in chain) Assert.False(copy.Append("RUN-1", l));
        Assert.True(copy.Append("RUN-1", Event(4)));
        Assert.True(copy.Append("RUN-1", Event(5)));

        Assert.Equal(6, copy.Lines);
        Assert.Equal(4, copy.Duplicates);
        Assert.Equal(6, File.ReadLines(copy.Path!).Count());
        Assert.Equal(chain.Concat([Event(4), Event(5)]), File.ReadLines(copy.Path!));
    }

    [Fact]
    public void A_station_restart_picks_up_where_its_file_ends()
    {
        var first = new CustodyCopy(_dir);
        foreach (var l in new[] { Header("RUN-1"), Event(1), Event(2) }) first.Append("RUN-1", l);

        // A new station process opens the same directory and receives the full replay plus one new line.
        var second = new CustodyCopy(_dir);
        Assert.False(second.Append("RUN-1", Header("RUN-1")));
        Assert.False(second.Append("RUN-1", Event(1)));
        Assert.False(second.Append("RUN-1", Event(2)));
        Assert.True(second.Append("RUN-1", Event(3)));
        Assert.Equal(4, second.Lines);
        Assert.Equal(4, File.ReadLines(second.Path!).Count());
    }

    [Fact]
    public void A_new_dispenser_run_opens_a_new_file_and_starts_the_count_over()
    {
        var copy = new CustodyCopy(_dir);
        copy.Append("RUN-1", Header("RUN-1"));
        copy.Append("RUN-1", Event(1));
        Assert.True(copy.Append("RUN-2", Header("RUN-2")));
        Assert.True(copy.Append("RUN-2", Event(1)));          // seq 1 again, in a new chain
        Assert.Equal("RUN-2", copy.RunId);
        Assert.Equal(2, copy.Lines);
        Assert.Equal(2, Directory.GetFiles(_dir, "station-copy-*.ndjson").Length);
    }

    /// <summary>The real path: an in-process dispenser, a sale, and a second subscription that replays the whole chain into the same copy.</summary>
    [Fact]
    public void Subscribing_to_the_dispenser_twice_leaves_one_copy_of_each_line_and_the_chain_verifies()
    {
        var journalDir = Path.Combine(_dir, "journal");
        Directory.CreateDirectory(journalDir);
        var ikm = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        using var h = new SimHarness("channel", SimulationOptions.Default with
        {
            HoldAmount = Money.FromDollars(10),
            Journal = new JournalOptions(journalDir, "PUMP-07", ikm, "RUN-COPY"),
        });
        var copy = new CustodyCopy(_dir);
        using var sub1 = h.Sim.SubscribeDispenserCustody((run, line) => copy.Append(run, line));

        h.Present("Good");
        h.AdvanceUntilState(SaleState.ReadyToPump, TimeSpan.FromSeconds(5));
        h.Sim.StartFueling();
        h.AdvanceUntilState(SaleState.Idle, TimeSpan.FromMinutes(3));
        var after = copy.Lines;
        Assert.True(after > 3, $"copy holds {after} lines");

        using var sub2 = h.Sim.SubscribeDispenserCustody((run, line) => copy.Append(run, line));
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(after, copy.Lines);
        Assert.True(copy.Duplicates >= after, $"replay handed {copy.Duplicates} duplicates");
        Assert.Equal(after, File.ReadLines(copy.Path!).Count());
        // The copy is byte for byte the dispenser's chain, so the same key verifies it.
        Assert.Equal(ReadShared(h.Sim.CustodyPath!), File.ReadAllLines(copy.Path!));
        if (CustodyFile.ChainAvailable) Assert.True(CustodyFile.Verify(copy.Path!, ikm, out var seq, out var why), $"copy failed to verify at {seq}: {why}");
    }
}
