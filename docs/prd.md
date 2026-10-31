# MMP.GasPumpSimulator PRD

Status: slice 1 started 2026-10-31. Decisions below are the build contract until Steve changes one.

## What

A simulator of a pay-at-the-pump fuel dispenser, on .NET 10, with an Electron front end. It models the five parts the research note on fuel dispenser card payment found in the real systems:

1. **Payment.** Card validation, a simulated issuer, a simulated network, and a two-phase authorization: pre-auth hold, then completion for the real amount, with reversal when no fuel moved.
2. **Security.** The pump controller is the authority over fuel. It arms only from a signed authorization it can verify. It clamps every ceiling to hard limits it owns. It keeps a hash-chained event ledger and a sealed totalizer. A hacked chain upstream cannot turn a $30 sale into 600 gallons.
3. **Card failures.** Insufficient funds with partial approval, expired card, lost or stolen, wrong PIN on debit, velocity limit, issuer down, network timeout, network flapping, completion while the host is down.
4. **The pump.** Hydraulics, meter, flow controller. With a $30 ceiling the flow runs full until the last 20 percent, then tapers down, and the final drop lands on the ceiling to the cent, never over.
5. **Concurrency.** Three workers, pump, sale, and payment, talk through bounded mailboxes. How the workers run is a Strategy. Today: dedicated threads. Also today: Task and Channel. Later: MMP.AsyncProxy state machines. Tests run the same sale under every strategy.

## Why

The research note answered "how does a gas pump take a card". This repo answers "show me". A reader can run a sale, watch the taper, inject a decline, pull the network, replay a token, and read the ledger. Every claim in the note that can be demonstrated gets a button.

## Acceptance

Kill condition: all true on one date, written here.

1. `dotnet test` green. Count on sign-off date: ___ of ___.
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
5. No AsyncProxy strategy in slice 1. The interface is shaped for it and the slot is named.

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

1. Slice 1: Core, Payment, Security, Pump, Dispatch with both strategies, tests. Host with WebSocket. Electron app with the pump face, card panel, fault toggles, attack buttons, ledger view.
2. Slice 2: AsyncProxy strategy once MMP.AsyncProxy is referenced. Scenario scripting. Receipt and completion timing rules.

## Open items

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
