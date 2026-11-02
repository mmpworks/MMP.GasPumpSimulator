using GasPump.Core;
using GasPump.Pump;
using GasPump.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The island's transaction buffer on its own: a delivery is written as
/// Payable, one controller locks it, that controller clears it, and the
/// record survives the state file. The forecourt standard's rules, each as
/// a one-line case.
/// </summary>
public class TransactionBufferTests
{
    private static (TransactionBuffer Buffer, EventLedger Ledger) Make()
    {
        var ledger = new EventLedger(new FakeTimeProvider(SimHarness.Start));
        return (new TransactionBuffer(ledger), ledger);
    }

    private static PumpTransaction Record(TransactionBuffer b, string saleId = "S1") =>
        b.Record(saleId, Grade.Regular, Volume.FromGallons(2), Money.FromDollars(7), [], SimHarness.Start, "hung");

    [Fact]
    public void A_delivery_is_payable_until_the_station_locks_and_clears_it()
    {
        var (b, ledger) = Make();
        Assert.False(b.HasOpen);
        var t = Record(b);
        Assert.Equal(1, t.TxSeq);
        Assert.Equal(TransactionState.Payable, t.State);
        Assert.True(b.HasOpen);
        Assert.Equal([1L], b.Open.Select(x => x.TxSeq));

        Assert.True(b.Lock(1, "station").Ok);
        Assert.Equal(TransactionState.Locked, b.Open.Single().State);
        Assert.Equal("station", b.Open.Single().LockedBy);
        Assert.True(b.Clear(1, "station").Ok);
        Assert.False(b.HasOpen);
        Assert.Equal(["tx.payable", "tx.locked", "tx.cleared"], ledger.Snapshot().Select(e => e.Kind));
    }

    [Fact]
    public void A_second_controller_cannot_lock_or_clear_what_the_first_holds()
    {
        var (b, _) = Make();
        Record(b);
        Assert.True(b.Lock(1, "station A").Ok);
        var (ok, reason) = b.Lock(1, "station B");
        Assert.False(ok);
        Assert.Contains("locked by station A", reason);
        Assert.False(b.Clear(1, "station B").Ok);
        Assert.True(b.Lock(1, "station A").Ok);                     // the holder may lock again
        Assert.True(b.Clear(1, "station A").Ok);
    }

    [Fact]
    public void Clearing_twice_is_fine_and_an_unknown_number_is_refused_with_a_reason()
    {
        var (b, _) = Make();
        Record(b);
        Assert.True(b.Clear(1, "station").Ok);                      // a clear needs no lock first
        var again = b.Clear(1, "station");
        Assert.True(again.Ok);
        Assert.Equal("already cleared", again.Reason);
        Assert.False(b.Lock(1, "station").Ok);                      // nothing to claim any more
        Assert.Contains("no transaction #9", b.Clear(9, "station").Reason);
    }

    [Fact]
    public void Numbers_count_up_across_deliveries_and_the_last_one_is_known_cleared_or_not()
    {
        var (b, _) = Make();
        Record(b, "S1");
        Record(b, "S2");
        Assert.Equal(2, b.Last!.TxSeq);
        Assert.Equal(["S1", "S2"], b.Open.Select(x => x.SaleId));
        b.Clear(2, "station");
        Assert.Equal("S2", b.Last.SaleId);                          // cleared, still the last recorded
        Assert.Equal(["S1"], b.Open.Select(x => x.SaleId));
    }

    [Fact]
    public void The_state_file_round_trip_keeps_the_open_delivery_and_never_reuses_a_number()
    {
        var (b, _) = Make();
        Record(b, "S1");
        Record(b, "S2");
        b.Clear(1, "station");
        var (seq, items) = b.Export();
        Assert.Equal(2, seq);
        Assert.Equal(2, items.Count);

        var (fresh, _) = Make();
        fresh.Restore(seq, items);
        Assert.Equal(["S2"], fresh.Open.Select(x => x.SaleId));
        Assert.Equal(3, Record(fresh, "S3").TxSeq);

        // A state file that lost its counter still cannot hand out a number already on the ledger.
        var (older, _) = Make();
        older.Restore(0, items);
        Assert.Equal(3, Record(older, "S3").TxSeq);
    }

    [Fact]
    public void Cleared_transactions_are_trimmed_to_a_short_tail_so_the_state_file_stays_small()
    {
        var (b, _) = Make();
        for (var i = 0; i < 40; i++) { var t = Record(b, $"S{i}"); b.Clear(t.TxSeq, "station"); }
        var (seq, items) = b.Export();
        Assert.Equal(40, seq);
        Assert.Equal(32, items.Count);
        Assert.Equal(40, items[^1].TxSeq);
    }
}
