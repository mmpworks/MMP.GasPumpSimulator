using GasPump.Core;
using GasPump.Dispatch;
using Xunit;

namespace GasPump.Tests;

/// <summary>
/// The envelope on its own: codec, sequence numbers, gap detection, replay,
/// and the heartbeat watchdog. Pure classes, no sockets, so every rule is a
/// one-line case.
/// </summary>
public class WireEnvelopeTests
{
    private static readonly DateTimeOffset T0 = SimHarness.Start;
    private static LinkFrame Body(string s) => new(LinkFrame.TagMsg, SaleId: s, M: new PumpMessage.Stop("test"));

    [Fact]
    public void A_frame_round_trips_with_every_envelope_field()
    {
        var seq = new WireSequencer(WireNode.Station);
        var f = seq.Next(WireKind.Command, WireNode.Pump(7), Body("S1"), T0, correlationId: "c-1", ttl: TimeSpan.FromSeconds(5));
        var line = FrameCodec.Encode(f);
        var (back, fault, _) = FrameCodec.Decode(line, WireNode.Pump(7));
        Assert.Equal(FrameFault.None, fault);
        Assert.Equal(f, back);
        Assert.Equal(1, back!.Seq);
        Assert.Equal("1.1", back.Source.ToString());
        Assert.Equal("2.7", back.Dest.ToString());
        Assert.Equal(WireKind.Command, back.Kind);
        Assert.Equal("c-1", back.CorrelationId);
        Assert.Equal(TimeSpan.FromSeconds(5), back.Ttl);
        var m = Assert.IsType<PumpMessage.Stop>(back.Body.M);
        Assert.Equal("test", m.Reason);
    }

    [Fact]
    public void The_codec_refuses_bad_json_the_wrong_version_and_someone_elses_frame_with_a_reason()
    {
        Assert.Equal(FrameFault.BadJson, FrameCodec.Decode("{not json", WireNode.Station).Fault);
        var seq = new WireSequencer(WireNode.Pump(7));
        var f = seq.Next(WireKind.Event, WireNode.Station, Body("S1"), T0);
        var v9 = FrameCodec.Encode(f with { Version = 9 });
        Assert.Equal(FrameFault.WrongVersion, FrameCodec.Decode(v9, WireNode.Station).Fault);
        var forPump3 = FrameCodec.Encode(f with { Dest = WireNode.Pump(3) });
        Assert.Equal(FrameFault.WrongDest, FrameCodec.Decode(forPump3, WireNode.Station).Fault);
        // A broadcast is for everyone.
        var hb = FrameCodec.Encode(f with { Dest = WireNode.Broadcast, Kind = WireKind.Heartbeat });
        Assert.Equal(FrameFault.None, FrameCodec.Decode(hb, WireNode.Station).Fault);
    }

    [Fact]
    public void Sequence_numbers_count_up_and_the_ring_can_replay_a_gap_but_not_forever()
    {
        var seq = new WireSequencer(WireNode.Pump(7), ring: 3);
        for (var i = 1; i <= 5; i++) seq.Next(WireKind.Event, WireNode.Station, Body($"S{i}"), T0);
        Assert.Equal(5, seq.LastSeq);
        Assert.Equal([3L, 4L, 5L], seq.Replay(3).Select(f => f.Seq));
        Assert.Equal([3L, 4L], seq.Replay(3, 4).Select(f => f.Seq));
        Assert.True(seq.CanReplayFrom(3));
        Assert.False(seq.CanReplayFrom(2));                      // older than the ring
        Assert.Empty(seq.Replay(6));
    }

    [Fact]
    public void A_frame_nobody_could_send_gives_its_number_back_so_a_later_peer_sees_no_gap()
    {
        var seq = new WireSequencer(WireNode.Pump(7));
        Assert.True(seq.Send(WireKind.Event, WireNode.Station, Body("S1"), T0, _ => true));
        Assert.False(seq.Send(WireKind.Event, WireNode.Station, Body("S2"), T0, _ => false));   // no station on the line
        Assert.False(seq.Send(WireKind.Event, WireNode.Station, Body("S3"), T0, _ => false));
        Assert.Equal(1, seq.LastSeq);
        Assert.Equal([1L], seq.Replay(1).Select(f => f.Seq));
        WireFrame? sent = null;
        Assert.True(seq.Send(WireKind.Event, WireNode.Station, Body("S4"), T0, f => { sent = f; return true; }));
        Assert.Equal(2, sent!.Seq);
        Assert.Equal(Accept.Ok, new WireReceiver().Check(seq.Replay(1)[0]).Verdict);
    }

    [Fact]
    public void Heartbeats_take_no_number_carry_the_senders_last_one_and_are_never_kept_for_replay()
    {
        var seq = new WireSequencer(WireNode.Pump(7));
        seq.Next(WireKind.Event, WireNode.Station, Body("S1"), T0);
        var hb = seq.Next(WireKind.Heartbeat, WireNode.Broadcast, new LinkFrame(LinkFrame.TagHeartbeat, Hb: new WireHeartbeat("R", "Idle", 99, 0)), T0);
        Assert.Equal(0, hb.Seq);
        Assert.Equal(1, hb.Body.Hb!.LastSeqSent);                    // the sender's true last number, whatever the caller wrote
        Assert.Equal(1, seq.LastSeq);
        Assert.Equal([1L], seq.Replay(1).Select(f => f.Seq));
    }

    [Fact]
    public void A_heartbeat_reveals_frames_lost_at_the_tail_and_a_heartbeat_with_nothing_new_is_quiet()
    {
        var send = new WireSequencer(WireNode.Pump(7));
        var recv = new WireReceiver();
        recv.Check(send.Next(WireKind.Event, WireNode.Station, Body("S1"), T0));
        var lost = send.Next(WireKind.Event, WireNode.Station, Body("S2"), T0);   // never arrives
        var quiet = send.Next(WireKind.Heartbeat, WireNode.Broadcast, new LinkFrame(LinkFrame.TagHeartbeat, Hb: new WireHeartbeat("R", "Idle", 0, 0)), T0);
        var (verdict, from, to) = recv.Check(quiet);
        Assert.Equal(Accept.Gap, verdict);
        Assert.Equal((2L, 2L), (from, to));
        Assert.Equal(Accept.Filled, recv.Check(lost).Verdict);
        Assert.Equal(Accept.Ok, recv.Check(quiet).Verdict);
    }

    [Fact]
    public void The_receiver_accepts_in_order_flags_a_gap_once_and_drops_duplicates()
    {
        var send = new WireSequencer(WireNode.Pump(7));
        var frames = Enumerable.Range(1, 6).Select(i => send.Next(WireKind.Event, WireNode.Station, Body($"S{i}"), T0)).ToList();
        var recv = new WireReceiver();

        Assert.Equal(Accept.Ok, recv.Check(frames[0]).Verdict);
        Assert.Equal(Accept.Ok, recv.Check(frames[1]).Verdict);
        // 3 and 4 are lost; 5 arrives.
        var (verdict, from, to) = recv.Check(frames[4]);
        Assert.Equal(Accept.Gap, verdict);
        Assert.Equal(3, from);
        Assert.Equal(4, to);
        Assert.Equal(1, recv.Gaps);
        Assert.Equal([3L, 4L], recv.Outstanding.OrderBy(x => x));

        // The replay fills the gap: 3 and 4 are accepted once each as fills, then refused as duplicates.
        Assert.Equal(Accept.Filled, recv.Check(frames[2]).Verdict);
        Assert.Equal(Accept.Filled, recv.Check(frames[3]).Verdict);
        Assert.Empty(recv.Outstanding);
        Assert.Equal(Accept.Duplicate, recv.Check(frames[2]).Verdict);
        Assert.Equal(Accept.Duplicate, recv.Check(frames[4]).Verdict);
        Assert.Equal(2, recv.Duplicates);
        Assert.Equal(Accept.Ok, recv.Check(frames[5]).Verdict);
        Assert.Equal(6, recv.LastSeq);
    }

    [Fact]
    public void A_new_connection_resets_the_receiver_so_the_peers_numbering_can_start_over()
    {
        var recv = new WireReceiver();
        var a = new WireSequencer(WireNode.Pump(7));
        recv.Check(a.Next(WireKind.Event, WireNode.Station, Body("S1"), T0));
        recv.Check(a.Next(WireKind.Event, WireNode.Station, Body("S2"), T0));
        recv.Reset();
        var b = new WireSequencer(WireNode.Pump(7));                 // the dispenser restarted
        Assert.Equal(Accept.Ok, recv.Check(b.Next(WireKind.Event, WireNode.Station, Body("S1"), T0)).Verdict);
    }

    [Fact]
    public void A_command_past_its_time_to_live_is_stale_by_the_receivers_clock()
    {
        var f = new WireSequencer(WireNode.Station).Next(WireKind.Command, WireNode.Pump(7), Body("S1"), T0, "c", TimeSpan.FromSeconds(3));
        Assert.False(f.IsStale(T0 + TimeSpan.FromSeconds(3)));
        Assert.True(f.IsStale(T0 + TimeSpan.FromSeconds(3.001)));
        var noTtl = new WireSequencer(WireNode.Station).Next(WireKind.Event, WireNode.Pump(7), Body("S1"), T0);
        Assert.False(noTtl.IsStale(T0 + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void A_price_write_and_a_totals_report_round_trip_the_codec_with_their_grade_keys()
    {
        var station = new WireSequencer(WireNode.Station);
        var board = Pricing.Default.With(Grade.Plus, 3851);
        var write = station.Next(WireKind.Write, WireNode.Pump(7), new LinkFrame(LinkFrame.TagMsg, SaleId: "S1", M: new PumpMessage.SetPrices(board.TenthCentsPerGallon, "admin")), T0, "w-1", TimeSpan.FromSeconds(3));
        var (back, fault, _) = FrameCodec.Decode(FrameCodec.Encode(write), WireNode.Pump(7));
        Assert.Equal(FrameFault.None, fault);
        Assert.Equal(WireKind.Write, back!.Kind);
        var m = Assert.IsType<PumpMessage.SetPrices>(back.Body.M);
        Assert.Equal("admin", m.By);
        Assert.True(new Pricing(m.TenthCentsPerGallon).SameAs(board));
        Assert.Equal(3851, m.TenthCentsPerGallon[Grade.Plus]);

        var pump = new WireSequencer(WireNode.Pump(7));
        var totals = new PumpTotals(Volume.FromGallons(12.345m), [new GradeTotal(Grade.Regular, Volume.FromGallons(10), Money.FromDollars(34.99m), 3), new GradeTotal(Grade.Premium, Volume.FromGallons(2.345m), Money.FromDollars(9.61m), 1)], 4, T0);
        var report = pump.Next(WireKind.Event, WireNode.Station, new LinkFrame(LinkFrame.TagTotals, Totals: totals), T0);
        var (got, fault2, _) = FrameCodec.Decode(FrameCodec.Encode(report), WireNode.Station);
        Assert.Equal(FrameFault.None, fault2);
        Assert.Equal(LinkFrame.TagTotals, got!.Body.T);
        Assert.Equal(totals.Totalizer, got.Body.Totals!.Totalizer);
        Assert.Equal(4, got.Body.Totals.LastTxSeq);
        Assert.Equal([Grade.Regular, Grade.Premium], got.Body.Totals.ByGrade.Select(g => g.Grade));
        Assert.Equal(3, got.Body.Totals.ByGrade[0].Deliveries);
        Assert.Equal(Money.FromDollars(9.61m), got.Body.Totals.ByGrade[1].Money);

        // A read for totals is one more thing the station can ask for.
        var read = station.Next(WireKind.Read, WireNode.Pump(7), new LinkFrame(LinkFrame.TagRead, Read: new WireRead(WireRead.Totals)), T0);
        Assert.Equal(WireRead.Totals, FrameCodec.Decode(FrameCodec.Encode(read), WireNode.Pump(7)).Frame!.Body.Read!.What);
    }

    [Fact]
    public void A_price_board_is_valid_only_when_every_grade_is_priced_above_zero()
    {
        Assert.True(Pricing.Default.Validate().Ok);
        var missing = new Pricing(new Dictionary<Grade, long> { [Grade.Regular] = 3499, [Grade.Plus] = 3799 });
        Assert.Equal((false, "no price for Premium"), missing.Validate());
        Assert.Equal((false, "Plus priced at 0 mills"), Pricing.Default.With(Grade.Plus, 0).Validate());
        Assert.True(Pricing.Default.SameAs(Pricing.Default.With(Grade.Regular, 3499)));
        Assert.False(Pricing.Default.SameAs(Pricing.Default.With(Grade.Regular, 3498)));
        Assert.Equal("Regular $3.499 Plus $3.799 Premium $4.099", Pricing.Default.Describe());
    }

    [Fact]
    public void The_watchdog_is_alive_until_two_beats_are_missed_and_a_beat_brings_it_back()
    {
        var live = new Liveness(TimeSpan.FromSeconds(1), missedBeatsAllowed: 2);
        Assert.True(live.IsAlive(T0));                             // nothing heard yet is not dead
        live.Beat(new WireHeartbeat("R", "Idle", 10, 4), T0);
        Assert.True(live.IsAlive(T0 + TimeSpan.FromSeconds(2.9)));
        Assert.True(live.IsAlive(T0 + TimeSpan.FromSeconds(3)));
        Assert.False(live.IsAlive(T0 + TimeSpan.FromSeconds(3.1)));  // third second with no beat
        live.Beat(new WireHeartbeat("R", "Idle", 11, 4), T0 + TimeSpan.FromSeconds(3.1));
        Assert.True(live.IsAlive(T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal(11, live.Last!.LastSeqSent);
    }
}
