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
11. (2026-11-01, evening) A fault clears when its part is repaired: the pump returns to Disarmed, the face reads "Pump back in service" once the hold settles, and the next card is admitted. A pump out of service refuses card and cash. No operator action leaves a hold at the bank: a second card, the station's "New sale", the attendant's reset, the tank, the pulser, the dispenser link. Count on 2026-11-01: 233 of 233 in `dotnet test`; 8 of 8 in `npm run e2e` (Playwright against the two live processes).
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
| 32 | Two processes (2026-11-01, slice 2e) | The dispenser (pump controller, hydraulics, meter, authority, nonces, totalizer, its own ledger, its own custody chain) runs in `GasPump.Dispenser.exe`. The station (sale saga, payment, journal, admin, E-stop, station copy of the dispenser chain) runs in `GasPump.Host.exe`. The host spawns the dispenser by default and connects to it; `--dispenser inproc` keeps the old single process for the tests and for a laptop without a free port. | Steve's three beliefs: the pump pumps under its authorized limits; custody is stored on the dispenser AND sent to the station; the station owns session open and close. A seam that is only a mailbox proves nothing; a socket proves the sides share no memory. |
| 33 | Wire | TCP on localhost, one JSON object per line, `System.Text.Json` polymorphism on `PumpMessage` and `SaleEvent` with the type name as the discriminator. Station to dispenser: `hello`, `msg` (sale id plus a `PumpMessage`), `power`. Dispenser to station: `hello`, `event` (a `SaleEvent`), `view` (the pump's state for the screen), `log`, `ledger`, `custody` (one chain line, byte for byte). The tick stays on the dispenser; it never crosses the wire. | The message types already existed and were the whole contract between the two workers. A real forecourt uses a vendor protocol over current loop or Ethernet; JSON lines are the same shape with a readable wire. |
| 34 | Link loss | Dispenser gone mid-sale: the station faults the sale with `LINK.DISPENSER.LOST`, settles for the recorded fuel, and tells the driver to see the attendant. Station gone while the valve is open: the dispenser keeps its own hard limits, and the first frame the station sends on reconnect is `Stop`, which closes the valve and disarms; recovery then closes the sale from the journal. | Each side must be safe alone. The dispenser's limits are the reason the station may fail; the station's journal is the reason the dispenser may fail. |
| 35 | Keys and custody across the hop | Both processes read the same `custody.ikm` and derive the same token key under the same HKDF label, so a token the station signs verifies on the dispenser. The dispenser's chain id is its own run id; the station's copy is the same bytes and verifies with the same verifier. The station also keeps its own chain for sale, payment, admin, recovery, and E-stop entries. | One key per site is the demonstration shape. The threat model names the per-device key as the real posture. |
| 36 | E-stop over the hop | The station writes its record and final snapshot, then sends `power off` to the dispenser, which closes its chain and exits. Reset relaunches the dispenser process, then builds the new station simulation, which connects to it. | The power leaves the forecourt; the site controller stays up. Same order of operations as decision 30, now visible as one process ending and another surviving. |
| 37 | Panels show the split | The app is two columns with a link strip between them. Left, amber: DISPENSER, with the face, the physical controls (grade, nozzle, trigger), the forecourt faults, and the dispenser's own log, ledger, and chain. Right, blue: STATION, with payment, bank link, administrator, E-stop, journal, and the station's log, ledger, and chain copy. The strip shows the process ids, the port, and the frame counters both ways. Each record carries its origin; nothing on the left came from the station process and nothing on the right came from the dispenser process. | Steve: the panels must be distinct enough to show the separation. |
| 38 | Card source (2026-11-01) | `CardSource { PumpInsert, PumpTap, Station }` on `CardPresented` and the snapshot. Cash is always Station. The pump's own reader has Insert and Tap; the station panel has "Card at the counter" and cash. | Steve: a card can be used at the pump (insert or NFC tap) or at the station. |
| 39 | Lift timeout by source (2026-11-01) | `SaleTimeouts.NozzleLift` 120 s at the pump; `NozzleLiftAtStation` 300 s for a card or cash at the station. The notice says which, and that the cashier can cancel a station sale by hand. | Steve: the station wait is longer because the employee can cancel manually. |
| 40 | A new card supersedes an untouched sale (2026-11-01) | If the sale on the screen is Authorizing or ReadyToPump and the nozzle has not left the harness, a new card or cash cancels it (hold reversed, `sale.superseded` and `sale.close` in the ledger) and starts the new sale at once. If the nozzle is in someone's hand or fuel is moving, the new payment is refused with "pump in use" (HTTP 409). Before this, a second card silently replaced the sale and left the first hold at the bank. | Steve: a driver who leaves before lifting the nozzle must not block the next driver; the switch is automatic. |
| 25 | AsyncProxy strategy (2026-11-01) | `AsyncProxyDispatch`: promise-backed bounded mailbox, one iterator per worker run by `AsyncProxy.Run`, continuations on the pool (`new Promise<T>(true)`), timers still from the `TimeProvider`. Referenced by project from the sibling checkout (`AsyncProxyProjectPath`) until MMP.AsyncProxy publishes. | The point of the exercise: the same sale under the 2008 vocabulary without the 2008 threads. A NuGet package would be cleaner; publishing is gated on Steve. |
| 41 | Fault taxonomy (2026-11-01) | One table. `DiagnosticCode` gains `ClearedBy`, a `ServiceAction`: `NextArm`, `AttendantReset`, `SupplyRestored`, `PulserRepaired`, `LinkRestored`, `CashierReset`. A code with any action other than `NextArm` takes the pump out of service (`OutOfService`). `OVERPULSE`, `STRAY`, `OVERCEILING`, `MAXTIME` stay `NextArm`. | Steve: a fault must clear when the part is fixed. One column on the existing table keeps "which part" and "what fixes it" in one place; a second table would drift. Per-sale faults describe one delivery, so the next arm is the right reset. |
| 42 | Conditions are read, not latched | `PumpController.Service` derives the active conditions from the part sensors each time it is asked: breakaway separated, supply off, pulser dead. No set is stored. | A stored set is a second copy of three flags and must be kept in step with them. A derived set cannot drift, so the pump can never report a part as broken after it was fixed. |
| 43 | Pump side of the reset | `Arm` is refused while any condition holds, with the code as the reason and a bracketed `arm.refused` entry. A pump `Faulted` for an out-of-service code goes back to `Disarmed`, clears `LastFault`, and writes `pump.in-service` when no condition holds it. A per-sale fault stays `Faulted` until the next arm. The check runs after every non-tick message, because only a message can change a part. | Before this, only the breakaway refused an arm, so a dry tank approved a card and faulted 3 s after the squeeze. One rule for every part replaces the special case. |
| 44 | One emitter across the seam | `DispenserView.Service` (a `ServiceStatus` of codes) carries the status. The station posts `SaleEvent.PumpService` from the view, once per change. `DispenserView.Offline` carries `LINK.DISPENSER.LOST`, so link loss and link return use the same path. | Rejected: `Dispenser.Handle` emits the event. The station would then need its own emitter for link loss and a resync after reconnect, which is two writers of one fact. The view already crosses after every message and on every connect. |
| 45 | Sale machine reset | `PumpService` is legal in every state. Before the first pour (`Authorizing`, `ReadyToPump`) an out-of-service report faults the sale with the code, closes the valve, and reverses the hold. A sale `Faulted` for an out-of-service code clears to `Idle` at once when the pump stops reporting that code and the hold is settled; with the hold unsettled it stays, and clears at once when the hold settles. The 30 s notice timeout is unchanged and still clears any finished screen; `Idle` reads "Out of service: part" while any condition holds, then "Pump back in service". | The driver must never be told to squeeze a trigger that cannot deliver. The settled-hold rule is the existing `HoldUnsettled` rule, applied at the moment of repair instead of at the next timeout, so there is no second hold check. |
| 46 | Station refuses payment while out of service | `BeginSale` refuses a card at the pump, a card at the counter, and cash while the station's last view reports any condition. The reason names the codes; the host returns 409. | A prepaid sale on a dead pump ends in a refund at the till. The cashier should hear "out of service" before the cash leaves the driver's hand. |
| 47 | E-stop and tank alarm | `STATION.ESTOP` is `CashierReset`; the reset already builds a new `Simulation`, so nothing clears in-process. `TANK.FUELOUT` is `SupplyRestored`; the pump holds it as `PUMP.STP.NOFLOW`, the code it faults with. | The pump sees supply, not the tank. Holding the condition under the code the pump raises means a faulted sale and the out-of-service screen name the same thing. |
| 48 | Value equality for the status | `ServiceStatus` compares by its codes in order. | `DispenserServer` sends a view only when it changed. A list compared by reference would make every idle tick a new view on the wire. |
| 49 | "New sale" never loses money (2026-11-01) | The station's reset shares one path with the second-card rule (`CancelOpenSaleAndReplace`): an approved or authorizing sale is cancelled and its hold reversed before the record is replaced. A sale that is fueling gets a valve close instead, and the dispenser reports the end of the delivery so the sale settles for the metered fuel. A completing sale is left alone. | The Playwright suite found it: holds piled up at the bank across tests because the old reset replaced the record and kept the hold. One decision, one control. |
| 50 | End-to-end lane (2026-11-01) | `npm run e2e` builds the renderer and runs Playwright against the real station host, which spawns the real dispenser process, under a throwaway log directory. Specs: a sale through the wire, the second card, the drive-off and attendant reset, a dry tank, a dispenser that dies and returns, the splitters. | Steve: use Playwright to play with the UI. The unit tests cannot see the screen, and the screen is where the breakaway defect lived. |
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
7. Slice 2e (built 2026-11-01): the pump-to-station seam is a socket. `Dispenser` (the pump worker's state and handler, extracted from `Simulation`), `IDispenserLink` with `LocalDispenserLink` (same mailbox as before) and `SocketDispenserLink` (JSON lines over TCP), `DispenserServer` (hosts a `Dispenser` behind a listener, with its own ledger, log, and custody chain), `GasPump.Dispenser` console project, host flags `--dispenser socket|inproc` and `--dispenser-port`. Tests: the 191 local-mode tests unchanged; three link tests on a real clock over loopback: a full sale across the socket, dispenser power loss mid-fuel, station restart with the valve open. Program design for the slice:
   - `GasPump.Core/Sale.cs`, `GasPump.Dispatch/Messages.cs`: `[JsonPolymorphic]` and `[JsonDerivedType]` on `SaleEvent` and `PumpMessage`. Additive.
   - `GasPump.Core/Diagnostics.cs`: `LINK.DISPENSER.LOST`.
   - `GasPump.Dispatch/Dispenser.cs`: `Dispenser` (owns `PumpAuthority`, `PumpController`, `Hydraulics`, `Meter`, `Totalizer`, `NonceStore`; `Handle(PumpMessage, saleId) -> IReadOnlyList<SaleEvent>`; `View()`), `DispenserView` record.
   - `GasPump.Dispatch/DispenserLink.cs`: `IDispenserLink`, `LocalDispenserLink`, `SocketDispenserLink`, `LinkFrame` and `LinkJson`.
   - `GasPump.Dispatch/DispenserServer.cs`: listener, one station at a time, replay of the chain on connect, `dispenser-state.json` for totalizer and nonces, `PoweredOff` event.
   - `GasPump.Dispatch/Simulation.cs`: builds the link from `SimulationOptions.Dispenser`; `HandlePumpMessage` moves into `Dispenser`; `View`, `Link`, `SubscribeDispenserCustody`; link-loss fault; `Stop` on reconnect; `EmergencyStop` powers the dispenser off before tearing down.
   - `GasPump.Dispenser/Program.cs`: the second process.
   - `GasPump.Host`: `DispenserProcess` (spawn, kill, relaunch), `/api/dispenser`, `/api/dispenser/powerloss`, `/api/dispenser/poweron`; `Contracts` reads the view and tags each log and ledger record with its origin.
   - `app/`: `App.vue` two columns plus `LinkStrip.vue`; `DispenserControls.vue`, `PaymentPanel.vue`, `RecordsPanel.vue` (origin-filtered log and ledger), `StationPanel.vue`.
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
