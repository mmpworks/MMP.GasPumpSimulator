# MMP.GasPumpSimulator

A pay-at-the-pump fuel dispenser, simulated. Present a card, get a two-phase authorization, lift the nozzle, watch the flow taper as the sale closes in on its ceiling, and land on the cent. Then attack it: replay a token, tamper a ceiling, sign a 600-gallon authorization with the real key, spoof the pulser. The pump holds.

.NET 10 on the back, Electron and Vue 3 on the front, one WebSocket between them.

## What it covers

1. **Payment.** Luhn and expiry at the terminal. A simulated issuer with named test cards: `Good`, `LowBalance` (partial approval), `InsufficientFunds`, `Expired`, `Stolen`, `Frozen`, `Velocity`, `DebitGoodPin`, `DebitBadPin`. A network you can set to Up, Down, Timeout, Flapping, or Late (the bank acts, the answer comes back after the pump gave up). Pre-authorization hold, completion for the real amount, reversal when no fuel moved, store-and-forward when the host is down at completion.
2. **Security.** The pump controller is the authority over fuel. It arms only from an HMAC-signed token it verifies itself, single-use by nonce, bound to its own pump id and a time window. Every ceiling is clamped to hard limits the pump owns: 50 gallons, $500, 10 minutes, 12 gal/min. There is no "set limit" message to attack. A hash-chained ledger records every arm, open, close, refusal, and fault. A sealed totalizer only goes up. Pulses the valve could not have passed stop the pump dead.
3. **The pump.** Hydraulics, meter, taper. With a $30 ceiling the flow is full to $24, falls in a straight line to an 8 percent creep, and the last slice is clamped so the money lands on 3000 cents and never 3001.
4. **The nozzle.** Fuel flows only with a grade selected, the nozzle out of the boot and inserted in a tank or can, and the lever held. Let go to pause, squeeze to resume, as often as you like. Once anything has poured, hanging the nozzle ends the sale. Hang before any pour and nothing changes; walk away and the pump cancels itself at 120 s.
5. **Failover.** The sale is a saga. When the bank link runs late or dies, every hold is reversed or every completion queued, never lost. A late approval that arrives after the pump gave up is reversed, never armed. The pump's screen says what happened, where your money is, and what to do: try again, use another pump, use another card, see the attendant, or go. The screen clears for the next driver after 30 s, but not while the bank still owes an answer.
6. **Tender.** Credit, debit, or cash. Credit asks the bank for the site's credit ceiling. Debit asks for the site's debit ceiling and the pump gets the lesser of that and the bank's answer, so a $50 debit ceiling against a $900 balance holds $50 and against $18.50 holds $18.50. Cash goes to the cashier, and the cash amount is the ceiling to the cent: no bank, no hold, change or a refund at the till.
7. **Administrator.** A back-office panel sets the credit and debit ceilings behind an admin PIN. Three wrong PINs lock it for five minutes. Every attempt is in the ledger. Raise the debit ceiling to $5000 and the bank will approve what the card has, and the pump will still clamp it to its own $500 hard limit, because the site's ceilings are business limits and the pump's are safety limits.
8. **Power loss.** A state journal is rewritten every second and on every sale change. A restart reads it, closes the sale it finds open for the fuel that was recorded, says in the ledger and the log that delivery is likely under-recorded by up to one second of flow, returns the hold or cash when nothing was pumped, and starts a fresh sale. Site limits, the admin lockout, burned nonces, the store-and-forward queue, the till, and the sealed totalizer all come back. There is no resume: a sale interrupted by a reboot is closed, never continued.
9. **Two files, two jobs.** The Herald.OSS NDJSON file is for debugging and takes everything. The custody file is for audit: an append-only, HMAC-chained record written by TesseraSeal's `ImmutableAuditFileSink`, one chain per run, carrying the ledger, the sale open and close, the money outcomes, the admin attempts, and the attacks. TesseraSeal's verifier checks it from the Ledger tab, and a single changed byte names the entry that broke.
10. **Forecourt faults with diagnostic codes.** Tank runs dry, pulser dies, drive-off. Each is caught by a disagreement between two facts the controller already has, names its part with a code such as `PUMP.STP.NOFLOW`, settles for the fuel that was metered, and tells the driver the right next step. The same code sits in the log, the ledger, and the custody file. `docs/diagnostics.md` is the table.
11. **E-stop.** One button, station-wide. The record is written before the power drops, so the cashier's reset closes the open sale with nothing unrecorded.
12. **Custody stream.** Every custody line goes out byte for byte as it lands. A station server keeps its own copy per run and verifies it with the same verifier. `ws://localhost:5177/ws/custody` is the same stream for anything outside.
13. **Card at the pump or at the station.** Insert or tap at the pump's own reader, or hand the card or cash to the cashier. A station sale waits 300 s for the lift, because the cashier can cancel by hand; a pump sale waits 120 s. A new card on an approved sale nobody touched cancels it, reverses its hold, and starts over. A pump with a nozzle in hand refuses the new card.
14. **Two processes.** The dispenser runs in `GasPump.Dispenser.exe` and the station in `GasPump.Host.exe`, joined by TCP on localhost. The pump's hard limits, authority, nonces, totalizer, ledger, and chain live in the dispenser process; the sale, the money, the journal, and the copy of the dispenser's chain live in the station. Pull either plug and watch the other side stay safe. The app shows them as two columns, amber and blue, with the wire between them.
15. **Logging on Herald.OSS.** Every worker writes structured events through [Herald.OSS](https://github.com/mmpworks/Herald.OSS) 0.14.0. A bridge sink feeds the UI stream and the tests; the host adds a console sink and an NDJSON file per session so a run can be reviewed later with every property intact.
16. **The keypad.** A debit card asks for its PIN and a credit card for its billing ZIP before anything goes to the bank. The bank checks the digits (test ZIP 12345, test PIN 1234) and declines a mismatch. The digits never reach a log.
17. **The idle minute.** Let go of the lever to take a phone call and the pump waits a minute for you. Squeeze again and it picks up where it left off. Stay silent and it closes the valve and settles for the fuel delivered, so a forgotten nozzle never holds a pump open.
18. **Nine fuelling-point states.** The pump has every state the forecourt standard names. Two were added last: `Calling`, a nozzle lifted with no sale, where the pump asks the station for an authorization and a card then arms it without a second lift; and `Closed`, the operator's switch, where every part works and every sale is refused until the cashier opens it again. Closed is never a fault, finishes a delivery in progress first, and survives a restart of either process.
19. **Concurrency.** Three workers (sale, pump, and payment) pass messages through bounded mailboxes and share no state. How they run is a Strategy: `ThreadedDispatch` is a thread per worker with blocking queues. `ChannelDispatch` is a Task per worker over bounded channels. `AsyncProxyDispatch` is the 2008 machine without the 2008 threads: each worker is an iterator-driven state machine from [MMP.AsyncProxy](https://github.com/mmpworks/MMP.AsyncProxy), its loop yields a future for the next message, and the loop resumes on a pool thread when one is posted. Every piece of it would compile for .NET 3.5. One end-to-end test runs the same sale under all three.

## Run it

```
dotnet test                       # 348 tests, about 105 s: most on a fake clock under all three dispatch strategies, 6 over a real TCP socket
cd app && npm run e2e             # 12 Playwright specs against the two live processes: sales, faults and repairs, the E-stop, the layout; about 2 min
dotnet run --project src/GasPump.Host -- --strategy threaded                 # station; spawns the dispenser on port 5178
dotnet run --project src/GasPump.Host -- --dispenser inproc                  # the old single process
dotnet run --project src/GasPump.Dispenser -- --port 5178                    # a dispenser by hand, in its own terminal; the station adopts it
cd app && npm install && npm run build && npx electron .
```

The Electron app starts the host for you on `localhost:5177`. Set `GASPUMP_STRATEGY=channel` or `GASPUMP_STRATEGY=asyncproxy` to run the Task-and-Channel or the AsyncProxy dispatch instead of threads. Set `GASPUMP_HOST=external` if you started the host yourself with `dotnet run --project src/GasPump.Host -- --strategy asyncproxy`.

MMP.AsyncProxy is a sibling checkout, referenced by project from `..\MMP.AsyncProxy` until the package publishes. Point elsewhere with `-p:AsyncProxyProjectPath=<path to MMP.AsyncProxy.csproj>`. TesseraSeal's chain code is compiled in from the sibling `..\Herald\Modules\Herald.Compliance\src\Audit` checkout by source link; point elsewhere with `-p:HeraldCompliancePath=<that folder>`.

### The files on disk

Everything lands in `%LOCALAPPDATA%\MMP.GasPumpSimulator\logs` (set `GASPUMP_LOG_DIR` to move it):

| File | Written by | Job |
|---|---|---|
| `gaspump-<date>-<time>.ndjson` | Herald.OSS file sink | Debug log. Every event from every worker, with properties. |
| `custody-<run>.ndjson` | TesseraSeal `ImmutableAuditFileSink` | Evidence. HMAC-SHA-256 chain, one per run, header line first, every line newline-terminated. Verified by TesseraSeal's verifier. |
| `state.json` | the simulator, every second | The journal a restart recovers from. Rewritten atomically. |
| `custody.ikm` | the host, once | The software key that seals the custody files. The spec puts this in a KMS; the simulator keeps it beside the files so the verifier can read it. `GASPUMP_CUSTODY_IKM_HEX` overrides it. |

### The log file

The host writes one Herald.OSS NDJSON file per session. The event log panel shows the path, and its **Save .txt** and **Save .json** buttons download a copy of everything since the host started. `GET /api/logfile` returns the path; `GET /api/log/export?format=text|json` returns the copy.

Each NDJSON line carries the time, level, category (the worker: sale, pump, payment, admin, attack, network), the rendered message, and the properties. To read a session back:

```powershell
Get-Content "$env:LOCALAPPDATA\MMP.GasPumpSimulator\logs\gaspump-*.ndjson" | ConvertFrom-Json | Where-Object category -eq 'admin' | Format-Table
```

If `npm install` leaves `node_modules/electron/dist` empty (the postinstall unzip failed silently on one Windows box here), unzip the file it downloaded to `%LOCALAPPDATA%\electron\Cache\...\electron-v33.2.1-win32-x64.zip` into that folder and write `electron.exe` into `node_modules/electron/path.txt`.

## What it looks like, measured

A $30 sale of Regular at $3.499, over the REST API against the running host, 2026-10-31:

| time | state | spent | gallons | flow | phase |
|---|---|---|---|---|---|
| 8 s | Fueling | $4.88 | 1.394 | 10.02 gpm | full |
| 70 s | Fueling | $29.59 | 8.457 | 0.78 gpm | creep |
| end | Idle | $30.00 | 8.574 | 0 | completed, hold settled to $30.00 |

A pulse spoof of 2000 pulses during a sale:

```
Faulted  $7.81  pump Faulted  "pulser reported 2017 pulses in one tick, valve could pass at most 60"  ledgerOk True
```

## The late bank

Set the host link to **Late** and present a card. The bank hears the request at 250 ms and puts a $30 hold on. The pump waits 30 s, gives up, tells the driver "Bank did not answer. No fuel was dispensed. Try again here, or use another pump", and sends a reversal. At 45 s the approval finally arrives on a sale that already ended. The pump never arms from it. It reverses again. The hold badge walks `hold requested` → `release queued` → `hold released`, and the issuer's open-hold list ends empty.

Against the running host with channel dispatch, Late answer set to 32 s, 2026-11-01. `openHolds` is the issuer's own book; `hold` is what the pump knows:

```
t+  1s  Authorizing  hold=Requested  openHolds=['S0001']  | Contacting your bank -> Wait
t+ 29s  Authorizing  hold=Requested  openHolds=['S0001']  | Contacting your bank -> Wait
t+ 31s  Declined     hold=Requested  openHolds=[]         | Bank did not answer -> TryAgain      (our reversal reached the bank at 30.25 s)
t+ 33s  Declined     hold=Held       openHolds=[]         | Bank did not answer -> TryAgain      (late approval arrived; never armed; reversed again)
t+ 65s  Declined     hold=Released   openHolds=[]         | Bank did not answer -> TryAgain      (the reversal's own late answer came back)
notice detail: No fuel was dispensed. Nothing was charged to your card. Try again here, or use another pump.
```

The pump's `hold` lags the bank's book by one late round trip. That lag is why the screen says "any hold is being released" rather than "nothing was charged" until the bank confirms it.

The nozzle interlocks, same session. Lift, insert, and squeeze with no grade chosen moved nothing. Grade, squeeze, release, squeeze, hang:

```
t+  3s  ReadyToPump  pump=Armed       $0.00  0.000 gal   [pump] lever ignored: select a grade first
t+  6s  Fueling      pump=Dispensing  $2.00  0.527 gal   first pour, valve open
t+  9s  Fueling      pump=Paused      $2.13  0.561 gal   flow paused: lever released
t+ 12s  Fueling      pump=Dispensing  $4.20  1.105 gal   flow resumed
t+ 13s  Idle         pump=Stopped     $4.26  1.122 gal   nozzle hung, completion $4.26 sent, hold=Settled
```

Pull the link **Down** instead and the reversal itself cannot get through. It goes into the same store-and-forward queue as completions, the screen says "A temporary hold may show on your card. It releases automatically when our bank link returns", and **Drain queued** sends it when the link is back. The tests `Late_approval_is_reversed_not_armed_and_the_card_ends_untouched` and `Bank_unreachable_after_timeout_queues_the_reversal_and_releases_when_the_link_returns` run under every dispatch strategy.

## Pull the plug

Start a sale, let it pump a few seconds, and press **Pull the plug** on the Host link tab. The host tears the simulation down with no close-out and no final save, then builds a new one on the same journal. The dispenser shows what the restart decided:

```
Pump restarted mid-sale: closed out from the journal
S0001 · Credit · Regular · 1.054 gal for $3.69 · up to 0.167 gal unrecorded · journal was 0.4 s old
```

The ledger carries `recovery.restart` and `recovery.closeout ... DELIVERY LIKELY UNDER-RECORDED by up to 0.167 gal`, the log carries the same at Error level, the hold settles to the recorded money, and the next driver starts clean. Pull the plug while the pump is armed and nothing has poured, and the hold is reversed instead. The tests `Power_loss_mid_fuel_closes_the_sale_for_the_recorded_money_and_flags_under_delivery`, `Power_loss_while_armed_with_no_fuel_reverses_the_hold`, and `Power_loss_during_a_cash_sale_owes_change_for_the_unpumped_part` run under every dispatch strategy.

The under-recording is by design. The journal is one second old at worst, so up to one second of flow at the last rate can leave the hose after the last record. The restart reports that bound and doesn't guess a number.

## Two processes, two plugs

- **Pull the dispenser's plug** (left column, while fuel is flowing). `GasPump.Dispenser.exe` dies. The left column goes OFFLINE, the strip goes red, and the station faults the sale with `LINK.DISPENSER.LOST`: "Pump offline. You were charged only for the N gal delivered. See the attendant." Restore dispenser power and a new dispenser run connects; the station starts a new copy of its chain.
- **Pull the station's plug** (right column, Power tab). `GasPump.Host.exe` restarts its simulation while the dispenser keeps pouring under its own limits. The new station's first frame is `Stop`; the valve closes; the journal closes the sale and says how much may be unrecorded.
- **E-STOP**. The station writes the record, then cuts the dispenser's power: the dispenser process exits. Reset at the cashier restarts it, then the station.

## Forecourt faults

A fault is a latched service condition. The part that failed holds the pump out of service until its repair: the attendant resets the breakaway, the tank is refilled, the pulser is repaired, the dispenser reconnects. While a condition holds, the face reads "Out of service" with the part, and the reader and the counter refuse every card and all cash. When the last condition clears, the pump returns to Disarmed and the face reads "Pump back in service" as soon as the bank has settled the faulted sale. Per-sale faults (spoofed or stray pulses) clear on the next arm.

In the left column, while fuel is flowing:

- **Tank runs dry.** Three seconds later the pump faults with `PUMP.STP.NOFLOW`, closes for the gallons it metered, and the screen reads "Fuel supply stopped. You were charged only for the N gal delivered. Please use another pump."
- **Kill the pulser.** 300 ms later: `PUMP.MTR.NOPULSE`. The fuel that moved uncounted is in the fault entry and is never billed.
- **Drive-off.** At once: `PUMP.HOSE.BREAKAWAY`, "Do not drive away. See the attendant." The next card is refused until the attendant resets the breakaway.
- **E-STOP.** The station goes dark. The ledger and the custody file already hold `station.estop` with `STATION.ESTOP`. Reset at the cashier and the dispenser shows the close-out, with nothing unrecorded because the E-stop saved after its last tick.

## The custody file, attacked

Press **Verify custody file** on the Ledger tab: `Status: PASS · 41 lines`. Change one byte of any entry in the file and press it again: `Status: FAIL · payload_hash MAC mismatch at seq 7`. Cut the final newline, as a crash mid-write would, and the verifier refuses the whole file. The test `Custody_file_verifies_under_TesseraSeal_and_a_tampered_byte_fails_at_the_right_entry` does all three.

## The admin panel, attacked

Two attacks live on the Administrator panel. **Brute-force the PIN** tries 0000, 0001, 0002 and stops: the third wrong PIN locks the panel for five minutes, the ledger shows `admin.refused`, `admin.refused`, `admin.locked`, and nothing changed. **Debit ceiling $5000 with this PIN** needs the real PIN, and then shows why the panel is business logic and the pump is the safety: the bank approves the card's $900, and the pump arms at $500 with `arm.ok ... clamped` in the ledger. The tests `Brute_forcing_the_admin_pin_locks_the_panel_and_changes_nothing` and `Admin_raises_the_debit_ceiling_to_5000_and_the_pump_still_clamps_at_its_hard_limit` run under every dispatch strategy.

## The 600-gallon case

In 2018 a Detroit station lost about 600 gallons in 90 minutes while the clerk could not stop the pump. The research behind this repo put the fix in one sentence: the reader protects the card, the controller protects the fuel. This simulator builds the second of those two trust boundaries.

Press "Signed token: 600 gal, $2000" in the app, pick a grade, lift, put the spout in the fill pipe, and squeeze. That token is signed with the real key, which models an upstream that is fully compromised. The pump authority verifies the signature, then clamps the ceiling to its own limits: 50 gallons, $500. The ledger shows `arm.ok ... clamped from $2000.00/600.000 gal to $500.00/50.000 gal`. Lift the nozzle and it stops at 50.000 gallons and $174.95. The test `Signed_600_gallon_token_is_clamped_to_50_gallons_and_500_dollars` runs that under every dispatch strategy.

`docs/glossary.md` is the source for the trade terms every name in the code follows: nozzle boot, lever, spout, bellows interlock, pulser, totalizer, STP, and the rest. `docs/threat-model.md` lists every attack and the test that proves its control. `docs/research.md` is the sources register: every outside fact the design rests on, with its evidence tier and where it is used.

## Layout

```
src/GasPump.Core       units, pricing, the sale state machine (a table, no I/O)
src/GasPump.Payment    cards, issuer, network, gateway, signed token, site limits (admin), cash drawer
src/GasPump.Security   hard limits, verifier, nonce store, pump authority, ledger, totalizer
src/GasPump.Pump       hydraulics, meter, taper policy, pump controller
src/GasPump.Custody    TesseraSeal's chain-of-custody sink and verifier, compiled in from Herald.Compliance by source link
src/GasPump.Dispatch   IDispatchStrategy, ThreadedDispatch, ChannelDispatch, AsyncProxyDispatch, Simulation, HeraldEventLog (Herald.OSS), journal and recovery
src/GasPump.Host       minimal API + WebSocket hub
app/                   Electron + Vite + Vue 3 + TypeScript
tests/GasPump.Tests    xunit on a FakeTimeProvider
docs/                  prd, threat model, architecture
```

## What this is not

No real card data. No EMV kernel. No PCI claim. No serial pump protocol. One pump, one hose, three grades. One shared HMAC key where a real site would use per-device keys and a MAC counter. Each one is a stand-in, and the repo teaches what it stands in for.

## Licence

MIT.
