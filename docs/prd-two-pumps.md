# PRD: two fuelling points under one forecourt controller

Status: drafted and built 2026-11-02 while Steve was at dinner. Decision 65 in `docs/prd.md` points here. The rows below are the build contract until Steve changes one.

## What

The station (the forecourt controller) drives two dispensers, pump 1 and pump 2, at once. Each pump is its own board: its own `GasPump.Dispenser.exe`, its own port, its own ledger, chain, state file, hard limits, nonces, and totalizer. The station holds one sale per pump, one payment worker for both, one journal, one ledger, one custody chain, one admin panel, one bank link, one E-stop. The UI is one window with a tab per pump; the shared station panels show on every tab.

Steve, 2026-11-02: "modify BOTH simulators to track 2 individual dispensers (pump 1 and pump 2) from the one forecourt controller... NOW I know where the multi-threading issues arise since we have 2 separate pumps connected to one computer... I would make the complete interface be a tabbed component - for 1 and 2. This does mean our messaging needs to contain better identification."

## Why

A real site has one controller and many fuelling positions on a shared wire, each with its own address. One pump hides the concurrency the controller has to get right: two sales asking one payment worker for the bank at once, two links posting into one station, one journal written by two sale workers, one admin board written to two islands. With two pumps the head-of-line blocking under the threaded strategy becomes visible across pumps: pump 2's driver waits because pump 1's bank call is late.

## Acceptance (the kill condition)

All true on one date, written here.

1. `dotnet test` green with the two-pump default. Count before: 394 of 394. Count after: 425 of 425 on 2026-11-02 (31 new: 30 in `TwoPumpTests`, 1 two-socket test in `LinkTests`).
2. `npm run e2e` green. Count before: 16. Count after: see the PR body for the run on 2026-11-02 (5 new specs in `pumps.spec.ts`).
3. Two sales fuel at once, one per pump, under all three dispatch strategies, each lands on the cent, each hold settles, no open hold left at the issuer.
4. With the bank link at 2 s latency (the network policy is the site's, so the latency applies to both calls) and pump 2's card presented 100 ms after pump 1's: under channel and asyncproxy pump 2 is approved at about 2 s, because the two calls overlap; under threaded pump 2 is approved at about 4 s, because the payment worker's one thread sits on pump 1's call for the whole latency and pump 2's request waits in the box behind it. The test states both. (Rewritten 2026-11-02 after Iolo's review: the first wording used `NetworkMode.Late`, which on this code waits the full late answer and ignores the host timeout, so pump 2 would have been declined by its own 30 s timer rather than approved late. The lesson is the same; the latency form states it as the code behaves.)
5. A token signed for pump 1 is refused by pump 2 with the wrong-pump reason in pump 2's ledger.
6. Pump 2 closed for sales refuses its cards; pump 1 keeps selling. A fault on pump 1's hose leaves pump 2 in service.
7. E-stop cuts both dispensers. The cashier's reset brings both back and settles both open sales from the islands' transaction buffers.
8. Station power loss with both pumps fueling closes both sales out; the dispensers keep pouring on their own limits; the new station's first frame to each is `Stop`.
9. One dispenser's link lost faults only that pump's sale with `LINK.DISPENSER.LOST`; the other sale is untouched.
10. Every sale id, log line, ledger entry, and wire frame says which pump it is about.
11. Both running stations (Howard's and Samms) run the two-pump build.

## Non-goals

1. No shared physical bus: each pump has its own TCP connection. The frames already carry source and destination node addresses, so the bus could be shared later by one listener that routes on `Dest`.
2. No change to the dispenser process beyond its pump id, its log file name, and the port it is given. The island does not know there is another island.
3. No per-pump admin ceilings, price boards, or bank links. Those are the site's.
4. No change to MMP.AsyncProxy's forecourt sample (Kevin's board). One board is the point of that story.
5. No change to the sale state machine. A sale is still one pump, one hose, one driver.

## Decisions

| # | Question | Pick | Why |
|---|---|---|---|
| 1 | Where the second pump lives | A second `GasPump.Dispenser.exe` on the next port (5179; Samms 5279). `DispenserProcess` per pump. | One board per fuelling position, as Kevin's board is. The two processes share no memory, which is the whole claim of the two-process design. |
| 2 | Station-side shape | New `FuellingPoint` class in `GasPump.Dispatch`: the sale machine and its timers, the link, the service status, the command table, the transaction reconciliation, the close-out wait, the dispenser log and ledger mirrors, the driver actions, the faults, the pump switch, the attacks that need a token. `Simulation` keeps what the site owns: options, log, ledger, custody, journal, issuer, network, gateway, site limits, drawer, signer, price board, capabilities, the payment worker, E-stop, power loss, recovery orchestration, snapshot. | Single responsibility: one class per pump-shaped thing, one for the site. `Simulation.cs` is 1,270 lines today; this is the split it was waiting for. |
| 3 | Sale workers | One sale worker and one sale mailbox per fuelling point (`sale-1`, `sale-2`). One payment worker for the site. | Two sale workers posting to one payment worker, and one payment worker posting back to two sale boxes, is the concurrency the exercise exists to show. The three strategy rules hold: each worker handles one message at a time, timers post, workers share no state (the shared pieces, ledger, gateway, journal, are already locked for the payment worker). |
| 4 | Sale id | `P{n}-S{k}`: `P1-S0001`, `P2-S0001`. Counter per fuelling point, journaled per point. | Steve: the messaging needs better identification. The id is on every `SaleEvent`, `PaymentMessage`, ledger line, journal record, issuer hold, and gateway advice, so the pump rides everywhere the sale does with no new field. The payment worker routes its answers by the prefix. |
| 5 | Pump ids and nodes | `PUMP-01` and `PUMP-02`; wire nodes 2.1 and 2.2 (`PumpNodes.Of`). Tokens are signed for the pump the sale is on. | The IFSF address is the pump number. A token for pump 1 presented to pump 2 fails at the pump-id check, which is a new test. |
| 6 | Records | Every log line from a fuelling point carries a `Pump` property (the number). Ledger entries about a pump carry the sale id or the pump id in the detail. The UI filters by pump; entries with no pump (admin, network, E-stop, recovery of the site) show on every tab. | One ledger per station is right: one controller, one book. The pump is a column, not a second book. |
| 7 | Payment gateway | `PreAuthorizeAsync` and `CashAuthorize` take the pump id; the constructor loses it. | The gateway signs for whichever pump the sale is on. |
| 8 | Journal | `JournalSnapshot` version 2: a `Points` list, one `JournalPoint` per pump (number, pump id, sale counter, sale, pump phase, selected grade, totalizer, nonces, dispenser conditions). Site fields stay at the top. A version 1 file is carried forward by `JournalUpgrade.FromV1`: every site field as it was (the compensation log, tenders, till, bank holds, limits, board) and its one sale as pump 1's point. Legacy `S{k}` ids route to pump 1 (`SaleIds.PumpOf`). | The two points recover independently: `RecoveryPlan.Build` runs per point and `Simulation.LastRecovery` becomes `Recoveries`, one per point. First draft said "ignore v1"; Richard and Iolo both blocked it, because the v1 file holds the site's money (queued completions, cash owed, open holds), and the two running stations upgrade from it. |
| 9 | Custody copy | `StationServer` keeps one `CustodyCopy` per pump. | `CustodyCopy` switches file when the run id changes; two dispensers interleaving would thrash one copy. |
| 10 | Host routes | Pump-scoped under `/api/pump/{n}/...`: card, code, cash, cash/collected, grade, nozzle, spout, lever, cancel, reset, totals, switch/{close,open}, fault/{kind}, attack/{replay,tamper,overlimit,notoken,spoof}, dispenser, dispenser/{powerloss,poweron}. Station-wide stay where they are: state, cards, log, ledger, custody, network, admin/{limits,prices}, drain, station/{estop,reset,custody}, host/powerloss, attack/pinbrute. | A route names its pump the way a frame names its node. |
| 11 | State contract | `state.pumps[]`, one entry per pump: number, pumpId, sale, pump (the view), dispenser (the link), recovery, totals, prices, pricesInSync. Push messages `sale`, `pump`, `log`, `ledger`, `dispenser-run` carry `pump: n`; site-wide records carry `pump: null`. | One shape for the snapshot and the stream, as before. |
| 12 | UI | A `v-tabs` under the app bar, one tab per pump, each tab the existing two-column layout for that pump. The tab label carries the pump's live state and money so the operator sees the other pump without switching. The station column's bank link, administrator, E-stop, and station power panels are the same on every tab because they are the site's; the point-of-sale panel and the station records are per pump. `activePump` lives in `sim.ts`; the api takes the pump from it. | Steve: "the complete interface be a tabbed component - for 1 and 2." |
| 13 | Ports | `GASPUMP_DISPENSER_PORT` is the first pump's port; pump n listens on it plus n minus 1. `GASPUMP_PUMPS` sets the count, default 2. Howard's: 5178, 5179. Samms: 5278, 5279. | One variable per station keeps the two-station recipe. |
| 14 | Compatibility facade | `Simulation` keeps its one-pump methods (`PresentCard`, `LiftNozzle`, `Snapshot`, `View`, `Pump`, ...) as forwards to pump 1, marked as such. New code calls `Simulation.Point(n)`. | The 394 tests are the anchor written before this change. They keep running against pump 1 unchanged, and the new two-pump tests use the explicit API. |
| 16 | Review changes (2026-11-02, Richard and Iolo on the built code) | `PaymentGateway.DrainPendingAsync` is single-flight and a completion or reversal reports "sent" when its advice is no longer pending, whichever drain carried it. `Simulation.PostToSale` reverses an approval whose sale id names no pump instead of dropping it. The tank is the site's: `Simulation.TankRunsDry` ledgers once and posts `Supply` to every point; the route is `/api/fault/{tankout,tankback}`. `SocketDispenserLink` drops a connection whose hello names another pump (`LINK.DISPENSER.WRONGID`). `CloseoutGrace` is 10 s so the slower of two islands restarting after an E-stop is heard. A sale record that cleared to Idle after a fault or a cancellation is replaced on the next card, never reused (found by the Playwright suite: the old sale's unanswered `Close` faulted the new sale under its reused id). From the C# reviewer: a journal point above the station's pump count (`GASPUMP_PUMPS` lowered) still gets its money closed out by the site (`recovery.orphan-pump`); one price lock from the read of the board to the fan-out, so two admin posts at once apply in one order on every island; every point's timers stop before the workers, so a power loss leaves no records; in-process only pump 1's custody copy is bound, since every in-process pump writes the one station chain; `SimulationOptions.PumpCount` defaults to 1 in the library and the host asks for 2, so the 394 one-pump tests run as one-pump tests. | Both reviewers blocked the first two and the journal row; the rest were their "should" items that cost one edit each. Left for later, named: a narrower `StationContext` (per-point options and a compensation view instead of the gateway), a pump number on `IStationCapability` events (a capability today keys on the sale id and sees the two pumps' events interleaved, ordered per pump only), journal save coalescing (every sale change on either pump writes and flushes under one lock), a pump tag on in-process dispenser ledger lines, a `CommandTracker` and a `TransactionReconciler` extracted from `FuellingPoint` (865 lines, over the 800-line review target), one endpoint filter for the `/api/pump/{n}` group in place of the repeated pump lookup. |
| 15 | Samms | After the merge, the Samms clone checks out the new tag and both stations restart on the two-pump build. | Steve: "modify BOTH simulators." Samms was the v0.1.0 snapshot; it is now the same code with its own name, theme, and ports. The README says so. |

## Program design

```
src/GasPump.Dispatch/
  FuellingPoint.cs      the pump as the station sees it: sale machine, timers, link, service, commands, reconciliation, actions
  Simulation.cs         the site: shared services, payment worker, journal, recovery, E-stop, Points
  StationContext.cs     what a FuellingPoint borrows from the site (dispatch, clock, options, log, ledger, gateway, signer, payment box, prices, journal save, capabilities)
  JournalRecords.cs     JournalSnapshot v2 with Points
  Recovery.cs           unchanged; Build runs per point
  DispenserLink.cs      unchanged
  DispenserServer.cs    log file named by pump id
src/GasPump.Payment/PaymentGateway.cs   pump id per call
src/GasPump.Host/
  Program.cs            DispenserProcess per pump; /api/pump/{n}/...
  Contracts.cs          pumps[]
  EventHub.cs           pump on every push
  StationServer.cs      copy per pump
  StationIdentity.cs    PumpCount, DispenserPortFor(n)
app/src/sim.ts          State.pumps, activePump, pumpState, api per pump
app/src/App.vue         tabs
app/src/components/*    read pumpState instead of state.sale/pump/dispenser
tests/GasPump.Tests/
  TwoPumpTests.cs       acceptance 3 to 8 on a fake clock, theory over strategies
  LinkTests.cs          acceptance 9 over two sockets
app/e2e/pumps.spec.ts   acceptance 12: tabs, a sale on pump 2 while pump 1 fuels
```

Build order:

1. Tests first: `TwoPumpTests.cs` against the API in decision 14 (they fail to compile until step 3).
2. Payment gateway takes the pump id; journal v2 records; `StationIdentity` ports.
3. `FuellingPoint` extracted from `Simulation`; `Simulation` holds `Points`, routes payment answers by sale id prefix, journals and recovers per point. The 394 tests run.
4. Host: processes, routes, contracts, hub, station server.
5. UI: tabs and `pumpState`.
6. e2e spec; README, architecture, prd decision rows; Samms checkout; both stations restarted.

## Anchor

Written before the code: the 394 existing tests (pump 1 through the facade), the 16 e2e specs (pump 1 tab), and the ten acceptance rows above. Independent of the implementation: the issuer's open-hold list (a bank's book), each dispenser's ledger over the socket, and the Playwright screen.

Anchor re-synced by the builder on 2026-11-02, as Iolo required the record to say: `KeypadTests` and `PaymentTests` pass the pump id to the gateway; `LinkTests` pins a one-pump `PUMP-07` station for the old socket tests and reads `P1-S0001`; `RecoveryTests` builds version 2 snapshots and reads `Point(1)`; `SimHarness` gains per-pump overloads. Every edit is a translation of a name, none changes what a test asserts. The e2e helper flattens pump 1 onto the old specs the same way. The Playwright suite then found a defect the fake-clock suite could not (decision 16, the reused sale id), which is the independence the anchor is for.

## Boundary

The agent may: write code, tests, docs; commit on a branch; open the PR; merge it green (Steve's grant of 2026-11-02 for this repo); update the Samms clone; restart both stations.
Needs Steve: any package publish; any outward claim beyond the README; a change to the money rules.
On a failing gate: stop and report. A flaky link test is reported with its output, never retried into green.
Decision owner: Steve, for every row above; this draft is the proposal.
