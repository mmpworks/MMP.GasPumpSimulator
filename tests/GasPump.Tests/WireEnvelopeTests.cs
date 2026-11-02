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
        Assert.True(seq.CanReplayFrom(3));
        Assert.False(seq.CanReplayFrom(2));                      // older than the ring
        Assert.Empty(seq.Replay(6));
    }

    [Fact]
    public void Heartbeats_take_a_number_but_are_never_kept_for_replay()
    {
        var seq = new WireSequencer(WireNode.Pump(7));
        seq.Next(WireKind.Event, WireNode.Station, Body("S1"), T0);
        seq.Next(WireKind.Heartbeat, WireNode.Broadcast, new LinkFrame(LinkFrame.TagHeartbeat, Hb: new WireHeartbeat("R", "Idle", 1, 0)), T0);
        Assert.Equal(2, seq.LastSeq);
        Assert.Equal([1L], seq.Replay(1).Select(f => f.Seq));
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

        // The replay fills the gap: 3 and 4 are accepted once each, then refused as duplicates.
        Assert.Equal(Accept.Ok, recv.Check(frames[2]).Verdict);
        Assert.Equal(Accept.Ok, recv.Check(frames[3]).Verdict);
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
