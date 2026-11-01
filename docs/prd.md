# MMP.GasPumpSimulator PRD

Status: slice 1 started 2026-10-31. Decisions below are the build contract until Steve changes one.

## What

A simulator of a pay-at-the-pump fuel dispenser, on .NET 10, with an Electron front end. It models the five parts the research note on fuel dispenser card payment found in the real systems:

1. **Payment.** Card validation, a simulated issuer, a simulated network, and a two-phase authorization: pre-auth hold, then completion for the real amount, with reversal when no fuel moved.
2. **Security.** The pump controller is the authority over fuel. It arms only from a signed authorization it can verify. It clamps every ceiling to hard limits it owns. It keeps a hash-chained event ledger and a sealed totalizer. A hacked chain upstream cannot turn a $30 sale into 600 gallons.
3. **Card failures.** Insufficient funds with partial approval, expired card, lost or stolen, wrong PIN on debit, velocity limit, issuer down, network timeout, network flapping, completion while the host is down.
4. **The pump.** Hydraulics, meter, flow controller. With a $30 ceiling the flow runs full until the last 20 percent, then tapers down, and the final drop lands on the ceiling to the cent, never over.
5. **Failover.** The sale is a saga. When the bank link runs late or dies, every hold is reversed or every completion queued, and the driver's screen says where the money is and what to do next: try again, use another pump, use another card, see the attendant, or go. An abandoned authorization cancels itself.
6. **The nozzle.** Grade, lift, seat, trigger. Fuel flows only with all four. Stop and start at will. Hang after a pour ends the sale.
7. **Concurrency.** Three workers, pump, sale, and payment, talk through bounded mailboxes. How the workers run is a Strategy. Today: dedicated threads. Also today: Task and Channel. Later: MMP.AsyncProxy state machines. Tests run the same sale under every strategy.

## Why

The research note answered "how does a gas pump take a card". This repo answers "show me". A reader can run a sale, watch the taper, inject a decline, pull the network, replay a token, and read the ledger. Every claim in the note that can be demonstrated gets a button.

## Acceptance

Kill condition: all true on one date, written here.

1. `dotnet test` green. Count on 2026-10-31: 80 of 80, 0 failures in 15 consecutive full runs. Count on 2026-11-01 after the saga and interlock slice: 108 of 108. Sign-off date: ___.
7. (2026-11-01) Under `NetworkMode.Late`, a sale ends Declined, the pump never arms, and the issuer shows no open hold, under both strategies. The driver's screen names the failure and the next step at every terminal state.
8. (2026-11-01) No fuel moves until grade, lift, seat, and trigger are all present. Release pauses, squeeze resumes, hang after a pour completes the sale.
9. (2026-11-01) A debit sale's ceiling is the lesser of the site's debit ceiling and the bank's answer. A cash sale's ceiling is the cash amount. The admin panel can raise a ceiling to $5000 and the pump still arms at $500. Three wrong PINs lock the panel. Count on 2026-11-01: 126 of 126.
10. (2026-11-01) Every log line reaches a Herald.OSS NDJSON file with its properties, and the UI can save a text or JSON copy.
2. The same end-to-end sale passes under both dispatch strategies, from one test with a theory over strategies.
3. A $30 sale at $3.499 per gallon ends at 3000 cents, never 3001, under both strategies, with the taper visible in the flow-rate trace (full, then falling, then stop).
4. Every attack in `docs/threat-model.md` has a test that fails without its control and passes with it.
5. The Electron app runs a sale against the host and shows the taper, the card outcome, the ledger, and the attack results.
6. README shows the comparison output and the test count with a date.

## Non-goals

1. No real card data, no real EMV kernel, no PCI claim of any kind. The reader encrypts nothing because there is nothing to protect. The README says so.
2. No real serial or current-loop pump protocol. Messages are typed records.
3. No multi-pump forecourt. One pump, one hose, three grades.
4. No persistence across runs beyond the ledger file for the current session.
5. (Lifted 2026-11-01.) The AsyncProxy strategy was out of slice 1; it landed in slice 2a.

## Decisions

| # | Question | Pick | Why |
|---|----------|------|-----|
| 1 | Runtime | .NET 10, C# 14, `TimeProvider` everywhere | Simulator, so modern is fine. `TimeProvider` makes every test deterministic. |
| 2 | UI to host link | ASP.NET Core minimal API plus one WebSocket for the event stream, on `localhost:5177`. Electron spawns the host. | A UI with live telemetry wants push. One socket, JSON events. Electron.NET would couple the two. |
| 3 | Front end | Electron, Vite, Vue 3, TypeScript | House frontend stack. |
| 4 | Money and volume | `long` cents, `long` milli-gallons, price in tenths of a cent per gallon | Integer math only. The research note's rule 4. |
| 5 | Ceiling enforcement | The pump controller computes the last tick so cents never exceed the ceiling. Display rounds the same way. | "Never over" is a contract, and it must hold in the authority, never in the UI. |
| 6 | Taper | Full flow until 80 percent of the ceiling is spent, then valve opening falls linearly with remaining money to a floor of 8 percent, then stop on the ceiling | Visible in a five-second sale. Tunable in `TaperPolicy`. |
| 7 | Authorization token | HMAC-SHA256 over sale id, pump id, ceiling cents, max volume, issued at, expires at, nonce. Key shared between the payment host and the pump authority for the simulation. | Shows the shape. A real site would use per-device keys and a MAC counter. Documented as such. |
| 8 | Hard limits | Owned by the pump authority, immutable after construction: 50 gallons per sale, $500 per sale, 10 minutes per sale, 12 gal/min flow, pulse rate sanity | The 600-gallon defense. A valid token above a cap is clamped to the cap. A command with no token is refused. |
| 9 | Ledger | Append-only, each entry carries the SHA-256 of the previous entry. `Verify()` walks the chain. The totalizer is a monotonic counter the ledger records on every sale. | Weights-and-measures shape. A tampered entry breaks every hash after it. |
| 10 | Dispatch strategy interface | `IDispatchStrategy` creates bounded mailboxes, starts named workers, and starts tickers that post into a mailbox. Workers never share state; they pass messages. | The seam AsyncProxy will plug into. A ticker that posts, rather than a timer that runs code, is what keeps the state machine single-writer. |
| 11 | Strategies in slice 1 | `ThreadedDispatch` (one `Thread` per worker, `BlockingCollection` mailboxes, `System.Threading.Timer` ticker) and `ChannelDispatch` (`Task` per worker, bounded `Channel<T>`, `PeriodicTimer`) | Two strategies prove the seam. The third is the point of the exercise. |
| 12 | Issuer accounts | In-memory table keyed by PAN, seeded with named test cards | Each card name tells you the failure it produces. |
| 13 | Network faults | A `NetworkFaultPolicy` the UI can set: latency, timeout, refuse, flap with a percentage | One object, one place to inject trouble. |
| 14 | Host-down completions | Completions queue in a store-and-forward list and drain when the network returns | The research note's `StoreAndForward` state. |
| 15 | Licence and repo | MIT, `mmpworks/MMP.GasPumpSimulator`, private | Steve said private. |
| 16 | Saga shape (2026-11-01) | The sale machine tracks `HoldStatus` apart from `SaleState`. Every forward money step has a compensation: reverse the hold, queue the completion. The gateway's store-and-forward list is the compensation log and holds reversals as well as completions. | Steve: failover must be graceful when the network runs late. The two parties cannot see each other, so the machine must know where the money is independent of where the pump is. |
| 17 | Late answers | `NetworkMode.Late`: the bank acts at the latency, the answer returns after `LateResponse` (45 s default) against a 30 s host timeout. A late `Approved` is reversed, never armed. A late approval for a replaced sale is reversed by the simulation directly. The issuer voids a sale id whose reversal arrived before its authorization. | This is the saga case nobody tests without a fault injector. |
| 18 | Driver notice | `DriverNotice(Headline, Detail, Action)` is a pure function of state, hold, and end reason. Actions: Wait, LiftNozzle, HangToStop, Leave, TryAgain, UseAnotherCard, UseAnotherPump, SeeAttendant. `DeadlineAt` on the snapshot drives a live countdown. | Steve: the driver must know what happened and whether to start over or go to the next pump. |
| 19 | Self-cancel | Approved with no pour for 120 s cancels and reverses. A finished screen clears to Idle after 30 s, except while the bank still owes an answer about the hold. | Steve: an abandoned authorization must cancel itself. |
| 20 | Nozzle interlocks | Fuel flows only with grade selected, nozzle lifted, nozzle seated in a fill point, and trigger held. Release and squeeze any number of times. Grade locks at the first drop. Hang after any pour ends the delivery. Hang before any pour changes nothing. `Fueling` starts at the first pour, never at the lift. | Steve, 2026-11-01: grade and fill-point sensor before any pour; stop and start many times; harness return after a pour ends the delivery. |
| 21 | Fault with fuel poured | Settle for the metered amount. Fault before fuel reverses. | The old shape left the hold on after a mid-sale fault. |
| 22 | Logging (2026-11-01) | Herald.OSS 0.14.0 via `HeraldEventLog`: bridge sink to the UI and tests, console and NDJSON file sinks in the host, one file per session under `%LOCALAPPDATA%\MMP.GasPumpSimulator\logs` or `GASPUMP_LOG_DIR`. `IEventLog` gains template overloads with properties and Warn/Error. | Steve: add Herald.OSS; keep the log for later review. The package exposes `Information/Warning/Error`; the README's `Info` does not compile against 0.14.0 (doc defect, reported). |
| 23 | Tender (2026-11-01) | Credit, Debit, Cash. `SiteLimits` holds the credit and debit ceilings; the bank is asked for the site ceiling and the pump gets the lesser of that and the approved amount. Cash: `CashDrawer`, the amount is the ceiling, no bank, change or refund at the till. | Steve: debit hold = lesser of a configurable threshold or the bank's answer; credit has its own, higher; cash is the amount paid, to the cent, with no automated threshold. |
| 26 | Journal and recovery (2026-11-01) | Snapshot every second and on every sale change, atomic rename. A restart closes the open sale for the recorded volume and money, flags likely under-delivery (up to one period at the last flow), reverses when nothing pumped, resends a completion in flight, restores queue, till, limits, lockout, nonces, totalizer, sale counter, and the in-process bank. No resume. Token key derived from the custody key so it survives the reboot. `SimulatePowerLoss()` and `POST /api/host/powerloss` for the drill. | Steve: a reboot must close out the transaction as far as it was recorded, flag the likely loss, and force a new session. Dynamic resume was considered and rejected. |
| 27 | Custody file (2026-11-01) | TesseraSeal's `ImmutableAuditFileSink` and `VerifyChain` compiled in from Herald.Compliance by source link (`GasPump.Custody`, `HeraldCompliancePath`). A second Herald bridge routes the evidence categories into it. One chain per run, software key file beside the logs, `GASPUMP_CUSTODY_IKM_HEX` override. | Steve: a regular file for us and an auditable chain-of-custody file per the spec. Reuse the reference implementation rather than re-implement a primitive. |
| 28 | Diagnostic codes (2026-11-01) | `DiagnosticCodes` in Core, `SUBSYSTEM.PART.FAULT`. The same code goes to the log (`Code` property), the ledger (bracketed prefix), and the custody file. Table in `docs/diagnostics.md`. | Steve: assign a code to the pump parts so the log and the custody output show it. |
| 29 | Forecourt faults (2026-11-01) | Supply loss (`PUMP.STP.NOFLOW`, 3 s window, flow switch), dead pulser (`PUMP.MTR.NOPULSE`, 300 ms, unmetered volume recorded and never billed), drive-off (`PUMP.HOSE.BREAKAWAY`, sensed, arm refused until attendant reset), tank gauge Fuel Out (`TANK.FUELOUT`). Each settles for the metered fuel and names the driver's next step. | Research register rows for Veeder-Root alarms, the Gilbarco meter-error patents, and the breakaway patents. |
| 30 | E-stop (2026-11-01) | Station-wide. Record first (ledger `station.estop`, custody), final snapshot with `StopReason`, then power off. Cashier reset builds a new Simulation; recovery reports nothing unrecorded. `POST /api/station/estop`, `/api/station/reset`. | NFPA 30A 6.7 as restated by IAEI and the Montana PEI RP1200 form: all dispensers, all STPs, all signal circuits; manual reset. |
| 31 | Custody stream (2026-11-01) | `CustodyStream` tails the sink's file and publishes each complete line byte for byte, under a lock so order holds, with replay for late subscribers. `StationServer` keeps a per-run copy and verifies it; `/ws/custody` streams to outside listeners. | Steve: the chain must be stored on the dispenser and sent to the station server. Spec: wire and disk forms are equivalent; distributed topology is conformant. |
| 25 | AsyncProxy strategy (2026-11-01) | `AsyncProxyDispatch`: promise-backed bounded mailbox, one iterator per worker run by `AsyncProxy.Run`, continuations on the pool (`new Promise<T>(true)`), timers still from the `TimeProvider`. Referenced by project from the sibling checkout (`AsyncProxyProjectPath`) until MMP.AsyncProxy publishes. | The point of the exercise: the same sale under the 2008 vocabulary without the 2008 threads. A NuGet package would be cleaner; publishing is gated on Steve. |
| 24 | Administrator (2026-11-01) | Back-office panel behind a PIN ("1234" default, `SimulationOptions.AdminPin`). Sets credit and debit ceilings. Three wrong PINs lock it for five minutes. Every attempt goes to the ledger. Attack buttons: brute-force the PIN; raise the debit ceiling to $5000 and watch the pump clamp. | Steve: an admin section we can attempt to hack. The pump's hard limits stay the authority. |

## Program design

```
src/
  GasPump.Core/        Money, Volume, FlowRate, Pricing, Grade
                       SaleState, SaleEvent (records), SaleCommand (records), SaleStateMachine (table)
                       IClock (TimeProvider wrapper), IEventLog
  GasPump.Payment/     CardData, CardValidator (Luhn, expiry)
                       IIssuer, IssuerSimulator (accounts, decisions), IssuerDecision
                       INetwork, NetworkSimulator, NetworkFaultPolicy
                       PaymentGateway (PreAuthorize, Complete, Reverse, store-and-forward)
                       AuthorizationToken, IAuthorizationSigner, HmacAuthorizationSigner
  GasPump.Security/    HardLimits, IAuthorizationVerifier, HmacAuthorizationVerifier, NonceStore
                       PumpAuthority (verify + clamp -> ArmedAuthorization)   GEM:decide-at-authoritative-boundary
                       EventLedger (hash chain), Totalizer
  GasPump.Pump/        Hydraulics, Meter, TaperPolicy, FlowController, PumpController, PumpTelemetry
  GasPump.Dispatch/    IDispatchStrategy, IMailbox<T>, IWorkerHandle, ThreadedDispatch, ChannelDispatch
                       Simulation (wires the three workers), SimulationOptions
  GasPump.Host/        Program (minimal API), WebSocket hub, ScenarioCatalog, JSON contracts
app/                   Electron main (spawns host), Vue renderer (pump face, card panel, faults, attacks, ledger)
tests/GasPump.Tests/   xunit, FakeTimeProvider; one theory runs the full sale under each strategy
docs/                  prd.md, threat-model.md, architecture.md
```

Build order:

1. Slice 1 (done 2026-10-31): Core, Payment, Security, Pump, Dispatch with both strategies, 80 tests. Host with WebSocket. Electron app with the pump face, card panel, fault toggles, attack buttons, ledger view, event log. Verified live: Electron spawned the host, the socket connected, a partial-approval sale fueled, a pulse spoof faulted the pump.
2. Slice 1b (done 2026-11-01): saga compensation (late approval, queued reversal, issuer void), `NetworkMode.Late`, driver notice with countdown, notice timeout, nozzle interlocks (grade, seat, trigger, pause and resume), fault settles for fuel poured. 108 tests. App shows the notice banner, hold badge, bank-owes line, grade and nozzle and trigger controls, Late mode.
3. Slice 1c (done 2026-11-01): Herald.OSS logging with a per-session NDJSON file; tender choice (credit, debit, cash) with site ceilings; administrator panel with PIN, lockout, and two attacks; cash drawer with change and refunds. 126 tests.
4. Slice 2a (done 2026-11-01): `AsyncProxyDispatch`, the third strategy, on MMP.AsyncProxy's new `netstandard2.0` target by project reference. Every end-to-end theory gains a row. 149 tests.
5. Slice 2c (done 2026-11-01): state journal, recovery close-out with under-delivery flag, TesseraSeal custody file, power-loss drill in the app. 174 tests.
6. Slice 2d (done 2026-11-01): diagnostic codes, supply loss, dead pulser, drive-off, tank gauge Fuel Out, station E-stop with cashier reset, custody stream to a station server. 191 tests.
7. Slice 2e: make the pump-to-station seam a socket, so the dispenser and the station server run as two processes. The message types already allow it; the journal moves to the station.
8. Slice 2b: scenario scripting. Receipt and completion timing rules. Swap the AsyncProxy project reference for a PackageReference when the package publishes.

## Open items

0. **Durable store-and-forward (2026-11-01).** CLOSED the same day by slice 2c: the journal carries the queue, the till, and everything else a restart needs, and the custody file is the durable ledger. Remaining gap, deliberate: the daily Merkle seal and HSM signature (spec primitives 2 and 3) are out of scope for one pump.
1. **Harness quiet detection (2026-10-31).** The test harness advances a fake clock when the simulation is quiet. One run in roughly ten, under the channel strategy, a 250 ms network wait completed only at the end of a 1 s advance. Logged with counters; cause not found. A 1 ms real-time stability window on "quiet" made it 0 failures in 15 full runs. The window is a mitigation, and the cause is still open. Candidates: FakeTimeProvider timer creation racing an Advance on another thread, or a thread-pool continuation outrunning the counters. Revisit with a dedicated stress test.

## Prior art

1. `Notebook-Algorithms/docs/system-design/fuel-dispenser-card-payment.md`: component map, the sale state machine in section 5.1, the pump-link threat in 4.2, the 600-gallon Detroit case. This repo is that note made runnable.
2. Gem `decide-at-authoritative-boundary`: the pump authority admits or clamps a ceiling where it has the context, and never trusts a value handed down.
3. Gem `back-pressure-ring`: the pulse mailbox waits the producer rather than dropping, because a pulse stream must never lose a count.
4. Where we differ from the real systems: one shared HMAC key, no EMV kernel, no serial protocol, one pump. Each is named in the README.

## Boundary

The agent may: write code, tests, docs, run `npm install` for the app, commit locally, create the private repo, push to it.
Needs Steve: public flip, any package publish, any outward claim.
On a failing gate: stop and report.
