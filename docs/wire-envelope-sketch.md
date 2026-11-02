# The wire, done the forecourt way: a sketch

Status: built. Slices 1 to 3 landed in PR #11 and slice 4 (prices as a write, totals as a read) in the PR after it, both on 2026-11-02; `docs/architecture.md` describes what runs. This page stays as the design record. It answers two questions Steve asked on 2026-11-02: what does a real forecourt message carry that ours does not, and could a sink on the station side write frames that an adapter on the dispenser side turns back into our messages, so the two sides stay in sync the way a real controller and dispenser do.

Sources are in `docs/research.md` under "Forecourt protocols". Where a fact is from the IFSF documents read this session it is marked (A) or (B). Where it is from recollection of the member-only Part 3-01 text it is marked (D) and must be verified before any outward claim.

## 1. What we have today

One TCP connection, station to dispenser, one JSON object per line. Each line is one of these:

| Direction | Line | Carries |
|---|---|---|
| down | `msg` | a `PumpMessage` plus the sale id it is for |
| down | `power off` | the E-stop's last word |
| up | `event` | a `SaleEvent` |
| up | `view` | the `DispenserView`: phase, nozzle switches, service status |
| up | `log`, `ledger` | the dispenser's own records, so the station can show them |
| up | `custody` | one custody chain line, byte for byte, with the run id |

`PumpMessage` today: Arm, Close, SelectGrade, Lift, InsertSpout, WithdrawSpout, Squeeze, Release, Hang, Tick (never crosses), Stop, SpoofPulses, Supply, PulserDead, DriveOff, ResetBreakaway.

`SaleEvent` from the dispenser today: PumpArmed, PumpRefused, GradeSelected, NozzleLifted, SpoutInserted, SpoutWithdrawn, FlowStarted, FlowPaused, FlowResumed, Progress, CeilingReached, NozzleHung, Fault, PumpService.

What the link does for us already: reconnects, drops posts while down, sends `Stop` first on reconnect, replays the custody chain from the header on connect, and the station's copy drops lines it already has by sequence number. That last part is the one place we already have a property the forecourt protocol insists on.

## 2. What a forecourt message carries that ours does not

| Property | In IFSF | In ours | Why it matters |
|---|---|---|---|
| **Address** | Every device has a logical node address, LNAO, a (subnet, node) pair. Every message names its source and its recipient. (A, EB18) | None. One socket, one pump, so "who" is implied. | The moment there are two pumps, or two stations, a message needs to say who it is from and for. Our `PumpId` sits inside the token and nowhere on the wire. |
| **Liveness** | Each device broadcasts a 10-byte heartbeat over UDP: host IP, TCP port, LNAO, message code, device status. A device that stops sending heartbeats cannot be reached, and the address table is built from heartbeats. (A, EB18) | The TCP connection state and the ticker's `view` lines are the only sign of life. A silent-but-connected dispenser looks fine. | A heartbeat with a status byte is how the station learns "pump 7 is alive and Closed" without asking. It also separates "link down" from "device down". |
| **Message kind** | Read, write, answer, and unsolicited status messages. A write to a device's database gets an answer with an acknowledge code. (D) | Commands down, events up, no acknowledgements. `Arm` is answered by `PumpArmed` or `PumpRefused` by convention; `SelectGrade` gets `GradeSelected` or a log line; `Stop` gets nothing. | A controller needs to know a command landed. Today a lost `Close` is noticed only when the next `Progress` arrives. |
| **Correlation** | A token or message id lets an answer be matched to the request that caused it. (D) | None. Events carry the sale id only. | Two `Arm`s for the same sale (a retry) cannot be told apart from their answers. |
| **Sequence** | Transactions carry a sequence number, and status changes are numbered, so a controller can detect a gap. (D) | Only the custody chain has `seq`. `PumpMessage` and `SaleEvent` do not. | A dropped event in flight is invisible. With a sequence, the receiver asks for the gap. |
| **Fuelling point and nozzle** | A dispenser has several fuelling points (one per side), each with several nozzles (one per product). Every message names the fuelling point, and delivery messages name the nozzle. (D) | One pump, one hose, `Grade` stands in for the nozzle. | A real dispenser is two pumps in one cabinet with three hoses each. Our model is one hose. |
| **Device state machine** | Nine fuelling-point states: Inoperative, Closed, Idle (with sub-state Authorised), Calling, Started (with Suspended started), Fuelling (with Suspended fuelling). (B, Wikipedia; count A, Project 4158) | `PumpPhase`: Disarmed, Armed, Dispensing, Paused, Stopped, Faulted. The sale's `SaleState` lives on the station. | Two states we lack: **Closed** (works, but sales disabled, the operator's "pump off") and **Calling** (nozzle lifted with no authorization yet, the pump asking to be authorized). Calling is how postpay starts. |
| **Transaction buffer** | When a delivery ends, the dispenser keeps the transaction (sequence number, nozzle, product, volume, amount, unit price) in a buffer in a Payable state until a controller **locks** it and **clears** it. The pump may refuse a new authorization until the buffer is cleared. (D; lock concept B, Project 4158) | The dispenser reports `NozzleHung` with the final numbers and forgets the sale on the next `Arm`. The station is the only holder of "delivered, unpaid". | This is the big one. In the real design the dispenser is the keeper of the unpaid delivery until the controller says it is paid. A station crash mid-settlement cannot lose a sale because the pump still has it. Our journal does this job on the station side instead. |
| **Lock** | A controller device locks a fuelling point before it authorizes or clears, so two controllers cannot fight over one pump. A lock from a controller that has gone offline has to be recoverable. (B, Project 4158) | `BeginSale` refuses a second card while a nozzle is in hand, on the station. The dispenser has no notion of which station owns it. | With one station it is the same thing. With two stations (a backup POS) the pump must hold the lock. |
| **Limits in the authorization** | An authorization carries maximum amount, maximum volume, and the allowed nozzles. (D) | The token carries ceiling and max volume. `Arm` carries an optional preselected grade. | Close. Ours is inside a signed token, which IFSF does not have; the signature is our addition and worth keeping. |
| **Totals** | Each nozzle has lifetime volume and amount totals the controller can read at any time. (D) | The totalizer is in the dispenser and shows on the `view`; the station never asks for it as a message. | A read message for totals is how an inspector's close-out works. |
| **Unit prices** | The controller writes prices to the dispenser per product per fuelling mode; the dispenser keeps them across restarts. (A, Project 4158) | The price board lives on the station and rides into the pump through `IPriceSchedule` in-process; over the socket the dispenser has its own copy from options. | Price changes should be a message with an acknowledge, so a dispenser cannot run on a stale board. |
| **Time** | Messages and transactions carry the device's timestamp. (D) | Events carry none. The station stamps them on receipt. | After a link flap, the station cannot order late-arriving events by when they happened. |
| **Error codes** | Fixed numeric error codes per device. (D) | `DiagnosticCode` strings like `PUMP.STP.NOFLOW`. | Ours are readable; a mapping table to numeric codes is all an adapter would need. |

The summary of the gap: our wire is a very good in-process message bus that happens to cross a socket. A forecourt protocol is a device protocol: it assumes many devices, many controllers, and a link that lies, so every message says who, which, and in what order, and the device keeps the money-bearing record until told otherwise.

## 3. The envelope

The fix for most of the table is one record wrapped around every line, in both directions. Nothing inside changes.

```csharp
// Core/Wire.cs (sketch)
public sealed record WireFrame(
    int Version,                 // 1. The adapter refuses a version it does not know.
    long Seq,                    // Per sender, per connection. Starts at 1 on connect. Gaps are reported.
    Node Source,                 // (subnet, node): the station is (1,1); pump 7 is (2,7).
    Node Dest,                   // Or Broadcast for a heartbeat.
    FrameKind Kind,              // Command, Event, Answer, Heartbeat, Replay, Read, Write.
    string? CorrelationId,       // A Command carries a new id; its Answer and its Events echo it.
    DateTimeOffset SentAt,       // The sender's clock. The receiver keeps its own ReceivedAt.
    TimeSpan? Ttl,               // A Command older than this on arrival is answered Stale, never run.
    string Type,                 // "PumpMessage.Arm", "SaleEvent.Progress", "Heartbeat", ...
    JsonElement Body);           // The record itself, unchanged.

public sealed record Node(int Subnet, int Node);
public enum FrameKind { Command, Event, Answer, Heartbeat, Replay, Read, Write }
public sealed record Answer(string CorrelationId, AnswerCode Code, string? Detail);
public enum AnswerCode { Done, Refused, Stale, Unknown, Busy, Locked }
public sealed record Heartbeat(Node Source, string RunId, string Status, long LastSeq);
```

The body is the record we have today. `PumpMessage.Arm` is still `PumpMessage.Arm`. The envelope is what the forecourt protocol has and we did not.

## 4. The sink and the adapter

Steve's idea: write frames through a sink on the station side, and convert them back with an adapter on the dispenser side. Here is how that fits the code we have, and where it needs a second piece.

### 4.1 What a sink is good for

Herald already gives us the sink shape: a pipeline accepts structured events and each sink writes them somewhere. The custody stream is a sink. A **WireSink** would be one more: every `SaleEvent`, `DispenserView`, log line, ledger line, and custody line that the dispenser produces goes through the pipeline and the WireSink writes it as a frame. On the station side a matching **WireSink** does the same for anything the station wants the dispenser to see.

That is the right tool for everything that flows **one way and needs no answer**: telemetry, records, heartbeats. A sink is fire-and-forget by design, and so are those.

### 4.2 What a sink is wrong for

Commands. `Arm`, `Close`, `Stop`, a price write. A command needs an answer, a correlation id, a timeout, and a retry rule. A logging sink has none of those and should not grow them. So the design is two lanes through one codec:

```
station                                       dispenser
-------                                       ---------
CommandPort  --Command frame-->  FrameAdapter --> Dispenser.Handle(PumpMessage)
             <--Answer frame---  FrameAdapter <-- (Done | Refused | Stale ...)
WireSink     <--Event frames---  WireSink     <-- SaleEvent, view, log, ledger, custody
             <--Heartbeat------                <-- every second, UDP or the same TCP
```

- **FrameCodec** (one class, both processes): record to frame, frame to record. Owns the `Type` string to CLR type table. This is where `Translate at the boundary` lives: nothing above it sees a frame.
- **FrameAdapter** (dispenser): reads frames, checks version, checks `Dest`, checks `Seq` for gaps, checks `Ttl`, then hands the body to `Dispenser.Handle` as the `PumpMessage` it always was, and writes the `Answer` frame with the same `CorrelationId`. `Dispenser.Handle` does not change.
- **WireSink** (both sides): a Herald sink that wraps each event in an `Event` frame with the next `Seq` and the sender's node, and writes it to the connection. The custody line keeps its own chain `seq` inside the body; the frame `Seq` is a second, per-connection counter. They answer different questions: "is the chain whole" and "did I miss a frame".
- **CommandPort** (station): what `Simulation.Execute` posts to instead of `_link.Post`. It assigns the `CorrelationId`, starts the timeout, and raises `Answered` or `Unanswered`. The sale worker treats `Unanswered` as it treats a fault today.

`IDispenserLink` stays. `SocketDispenserLink` and `DispenserServer` become one adapter pair among others, as `LocalDispenserLink` is today. That is the ports-and-adapters seam doing its job.

### 4.3 How the two sides stay in sync

Sync is five rules, each one a frame kind.

1. **Heartbeat.** Each side sends one per second: its node, its run id, its status word (`Idle`, `Closed`, `Fuelling`, `Faulted`), and the last `Seq` it sent. Two missed beats is "device down", which is a different fault from "link down" (the socket closed). The station's `LINK.DISPENSER.LOST` splits into two codes.
2. **Sequence and gap.** Every frame carries the sender's `Seq`. A receiver that sees `n+2` after `n` sends a `Read` for `n+1`. The sender keeps a short ring of recent frames to answer from. A gap that cannot be filled is a fault, named.
3. **Connect means replay.** On a new connection, each side says its last received `Seq` from the other in its first heartbeat. The other side replays from there. This is what the custody stream already does for one kind of line; the envelope makes it one rule for every kind.
4. **Command, answer, timeout.** A command is `Done`, `Refused` (with the reason the dispenser already produces, such as `PUMP.HOSE.BREAKAWAY`), `Stale` (arrived past its `Ttl`), or unanswered (timeout). The station never assumes. `Stop` on reconnect becomes a command with an answer, so the station knows the valve is shut before it does anything else.
5. **The dispenser keeps the transaction until cleared.** This is the forecourt rule we do not follow today, and it is the one that changes behavior. After `NozzleHung`, the dispenser keeps a `Transaction` (sequence number, nozzle, grade, volume, money, lines, ended-at) in a buffer marked `Payable`. The station sends `Lock(txSeq)` then `Clear(txSeq)` once the completion is settled or queued. Until `Clear`, the dispenser refuses `Arm` with `AnswerCode.Busy`. A station that restarts asks the dispenser for its payable transactions and finishes them. Our journal keeps doing its job; the dispenser becomes a second keeper of the same fact, which is the redundancy Steve asked for on the records and extends it to the money.

## 5. How each message changes

| Today | Under the envelope | What changes inside |
|---|---|---|
| `PumpMessage.Arm(SaleId, Token, Grade?)` | `Command` frame, body unchanged, plus `CorrelationId`, `Ttl` of a few seconds | Answer is `Done` with the armed limits, or `Refused` with the code, or `Busy` if a payable transaction is still in the buffer. `PumpArmed` and `PumpRefused` keep flowing as events too, carrying the same `CorrelationId`. |
| `PumpMessage.Close`, `Stop` | `Command` | Answered `Done`. Today they are fire-and-forget. |
| `SelectGrade`, `Lift`, `InsertSpout`, `WithdrawSpout`, `Squeeze`, `Release`, `Hang` | `Command` | These are the driver's hands, so in a real dispenser they are not commands from the station at all: the dispenser senses them and reports them. Over the envelope they stay as test and UI commands, answered `Done`, and the dispenser still reports the resulting events. |
| `Supply`, `PulserDead`, `DriveOff`, `SpoofPulses`, `ResetBreakaway` | `Command` (test and attendant hooks) | Same. `ResetBreakaway` is the one a real attendant sends. |
| `Tick` | never on the wire | Unchanged. |
| new: `Write(Prices)` | `Write` | The station writes the price board per grade; the dispenser answers `Done` and keeps it across restarts. The dispenser's own copy from options goes away. |
| new: `Read(Totals)`, `Read(Transactions)`, `Read(State)` | `Read` | Inspector's close-out, restart recovery, and "what are you doing" without waiting for a view. |
| new: `Lock(txSeq)`, `Clear(txSeq)` | `Command` | The transaction buffer lifecycle. |
| new: `Open`, `Close` (the pump, not the valve) | `Command` | The operator's "pump off". Maps to the IFSF `Closed` state. Our `ServiceStatus` already carries out-of-service for faults; this adds the deliberate kind. |
| `SaleEvent.Progress(Volume, Money, Flow, Phase, Lines)` | `Event` frame, body gains `TxSeq` and `FuellingPoint`, `Nozzle` | Sequence lets a gap be seen. Fuelling point and nozzle are 1 and the grade's nozzle number until we model a second hose. |
| `NozzleHung`, `CeilingReached` | `Event`, body gains `TxSeq`, `EndedAt`, `Payable = true` | The event now names the transaction the dispenser is holding. |
| `Fault(Reason, Code)` | `Event`, body gains a numeric code beside the string | Mapping table in `Diagnostics.cs`. |
| `PumpService` | folds into the heartbeat status plus an `Event` on change | One source of "is it well". |
| `view`, `log`, `ledger`, `custody` | `Event` frames through the WireSink | Unchanged bodies. The custody line keeps its chain `seq`; the frame adds a connection `Seq`. |
| `power off` | `Command` with `Ttl` zero and no answer expected | The one message that may go unanswered on purpose. |
| `SaleState` | unchanged | The sale is still the station's. The dispenser gains `PumpPhase.Calling` and `Closed`. |

## 6. Two walks through the sync

**Station restarts while a delivery is paused.** Today: the journal says Fueling, the restart closes the sale for the recorded money, and the dispenser, told `Stop` on reconnect, shuts the valve; the fuel that moved between the last snapshot and the stop is under-recorded by up to one period, and the record says so. Under the envelope: the new station's first heartbeat says "last seen Seq 0"; the dispenser replays its event ring, so the station sees every `Progress` it missed, and the valve is still shut by the `Stop` command, now answered. The dispenser also holds the transaction as `Payable`. The station reads it, settles for the dispenser's number instead of its own stale one, and sends `Clear`. The under-recording goes away, because the device that counted the fuel is the one asked.

**Link flaps for ten seconds during fueling.** Today: the dispenser keeps pouring under its own ceiling and timer (correct), events posted while down are dropped, and the station learns the new total from the first `Progress` after reconnect. Under the envelope: same pouring; on reconnect the gap in `Seq` is filled from the ring, so the station's log is whole, and the two heartbeats that were missed are recorded as a `LINK.DISPENSER.FLAP` with its duration, not as a device fault.

## 7. What this costs, and what it does not buy

- A frame per message: a few dozen bytes of envelope on a few dozen bytes of body. Nothing at our rates.
- A ring buffer per sender for replay: a few hundred frames.
- Two timers per side: heartbeat out, heartbeat watchdog in.
- A transaction buffer and three new commands on the dispenser, and a recovery read on the station. This is the real work, and it is behavior, not plumbing.
- It does not buy IFSF compatibility. Our bodies are our records, in JSON. A real IFSF dispenser would need a second codec that speaks Part 3-01's databases and data ids. The envelope makes that a codec swap instead of a redesign, which is the point of putting the properties in the frame and leaving the bodies alone.

## 8. Recommendation

Build in this order when the time comes, each slice with its tests, none of them now:

1. The envelope and the codec, with heartbeat and sequence, behind the existing `IDispenserLink`. Every current test passes unchanged, because bodies do not change.
2. Answers for commands, and `Unanswered` as a sale fault.
3. The transaction buffer with `Lock` and `Clear`, and the restart read. This changes the recovery table and is the slice that needs Steve's eye on the money rules.
4. `Write(Prices)`, `Read(Totals)`, `Open`/`Close`, `Calling`.

Decision owner for slice 3: Steve, because it moves where the money-bearing record lives.
