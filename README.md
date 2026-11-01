# MMP.GasPumpSimulator

A pay-at-the-pump fuel dispenser, simulated. Present a card, get a two-phase authorization, lift the nozzle, watch the flow taper as the sale closes in on its ceiling, and land on the cent. Then attack it: replay a token, tamper a ceiling, sign a 600-gallon authorization with the real key, spoof the pulser. The pump holds.

.NET 10 on the back, Electron and Vue 3 on the front, one WebSocket between them.

## What it covers

1. **Payment.** Luhn and expiry at the terminal. A simulated issuer with named test cards: `Good`, `LowBalance` (partial approval), `InsufficientFunds`, `Expired`, `Stolen`, `Frozen`, `Velocity`, `DebitGoodPin`, `DebitBadPin`. A network you can set to Up, Down, Timeout, Flapping, or Late (the bank acts, the answer comes back after the pump gave up). Pre-authorization hold, completion for the real amount, reversal when no fuel moved, store-and-forward when the host is down at completion.
2. **Security.** The pump controller is the authority over fuel. It arms only from an HMAC-signed token it verifies itself, single-use by nonce, bound to its own pump id and a time window. Every ceiling is clamped to hard limits the pump owns: 50 gallons, $500, 10 minutes, 12 gal/min. There is no "set limit" message to attack. A hash-chained ledger records every arm, open, close, refusal, and fault. A sealed totalizer only goes up. Pulses the valve could not have passed stop the pump dead.
3. **The pump.** Hydraulics, meter, taper. With a $30 ceiling the flow is full to $24, falls in a straight line to an 8 percent creep, and the last slice is clamped so the money lands on 3000 cents and never 3001.
4. **The nozzle.** Fuel flows only with a grade selected, the nozzle off the harness and seated in a tank or can, and the trigger held. Let go to pause, squeeze to resume, as often as you like. Once anything has poured, hanging the nozzle ends the sale. Hang before any pour and nothing changes; walk away and the pump cancels itself at 120 s.
5. **Failover.** The sale is a saga. When the bank link runs late or dies, every hold is reversed or every completion queued, never lost. A late approval that arrives after the pump gave up is reversed, never armed. The pump's screen says what happened, where your money is, and what to do: try again, use another pump, use another card, see the attendant, or go. The screen clears for the next driver after 30 s, but not while the bank still owes an answer.
6. **Tender.** Credit, debit, or cash. Credit asks the bank for the site's credit ceiling. Debit asks for the site's debit ceiling and the pump gets the lesser of that and the bank's answer, so a $50 debit ceiling against a $900 balance holds $50 and against $18.50 holds $18.50. Cash goes to the cashier, and the cash amount is the ceiling to the cent: no bank, no hold, change or a refund at the till.
7. **Administrator.** A back-office panel sets the credit and debit ceilings behind an admin PIN. Three wrong PINs lock it for five minutes. Every attempt is in the ledger. Raise the debit ceiling to $5000 and the bank will approve what the card has, and the pump will still clamp it to its own $500 hard limit, because the site's ceilings are business limits and the pump's are safety limits.
8. **Logging on Herald.OSS.** Every worker writes structured events through [Herald.OSS](https://github.com/mmpworks/Herald.OSS) 0.14.0. A bridge sink feeds the UI stream and the tests; the host adds a console sink and an NDJSON file per session so a run can be reviewed later with every property intact.
9. **Concurrency.** Three workers, sale, pump, and payment, pass messages through bounded mailboxes and share no state. How they run is a Strategy: `ThreadedDispatch` is a thread per worker with blocking queues. `ChannelDispatch` is a Task per worker over bounded channels. The third slot is for MMP.AsyncProxy state machines. One end-to-end test runs the same sale under every strategy.

## Run it

```
dotnet test                       # 126 tests, about 17 s, on a fake clock
cd app && npm install && npm run build && npx electron .
```

The Electron app starts the host for you on `localhost:5177`. Set `GASPUMP_STRATEGY=channel` to run the Task and Channel dispatch instead of threads. Set `GASPUMP_HOST=external` if you started the host yourself with `dotnet run --project src/GasPump.Host -- --strategy channel`.

### The log file

The host writes one Herald.OSS NDJSON file per session to `%LOCALAPPDATA%\MMP.GasPumpSimulator\logs\gaspump-<date>-<time>.ndjson`. Set `GASPUMP_LOG_DIR` to put it elsewhere. The event log panel shows the path, and its **Save .txt** and **Save .json** buttons download a copy of everything since the host started. `GET /api/logfile` returns the path; `GET /api/log/export?format=text|json` returns the copy.

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

The nozzle interlocks, same session. Lift, seat, and squeeze with no grade chosen moved nothing. Grade, squeeze, release, squeeze, hang:

```
t+  3s  ReadyToPump  pump=Armed       $0.00  0.000 gal   [pump] trigger ignored: select a grade first
t+  6s  Fueling      pump=Dispensing  $2.00  0.527 gal   first pour, valve open
t+  9s  Fueling      pump=Paused      $2.13  0.561 gal   flow paused: trigger released
t+ 12s  Fueling      pump=Dispensing  $4.20  1.105 gal   flow resumed
t+ 13s  Idle         pump=Stopped     $4.26  1.122 gal   nozzle hung, completion $4.26 sent, hold=Settled
```

Pull the link **Down** instead and the reversal itself cannot get through. It goes into the same store-and-forward queue as completions, the screen says "A temporary hold may show on your card. It releases automatically when our bank link returns", and **Drain queued** sends it when the link is back. The tests `Late_approval_is_reversed_not_armed_and_the_card_ends_untouched` and `Bank_unreachable_after_timeout_queues_the_reversal_and_releases_when_the_link_returns` run both under each dispatch strategy.

## The admin panel, attacked

Two attacks live on the Administrator panel. **Brute-force the PIN** tries 0000, 0001, 0002 and stops: the third wrong PIN locks the panel for five minutes, the ledger shows `admin.refused`, `admin.refused`, `admin.locked`, and nothing changed. **Debit ceiling $5000 with this PIN** needs the real PIN, and then shows why the panel is business logic and the pump is the safety: the bank approves the card's $900, and the pump arms at $500 with `arm.ok ... clamped` in the ledger. The tests `Brute_forcing_the_admin_pin_locks_the_panel_and_changes_nothing` and `Admin_raises_the_debit_ceiling_to_5000_and_the_pump_still_clamps_at_its_hard_limit` run both under each dispatch strategy.

## The 600-gallon case

In 2018 a Detroit station lost about 600 gallons in 90 minutes while the clerk could not stop the pump. The research behind this repo put the fix in one sentence: the reader protects the card, the controller protects the fuel. Two trust boundaries. This simulator builds the second one.

Press "Signed token: 600 gal, $2000" in the app, pick a grade, lift, put the nozzle in the tank, and squeeze. That token is signed with the real key, which models an upstream that is fully compromised. The pump authority verifies the signature, then clamps the ceiling to its own limits: 50 gallons, $500. The ledger shows `arm.ok ... clamped from $2000.00/600.000 gal to $500.00/50.000 gal`. Lift the nozzle and it stops at 50.000 gallons and $174.95. The test `Signed_600_gallon_token_is_clamped_to_50_gallons_and_500_dollars` runs that under both dispatch strategies.

`docs/threat-model.md` lists every attack and the test that proves its control.

## Layout

```
src/GasPump.Core       units, pricing, the sale state machine (a table, no I/O)
src/GasPump.Payment    cards, issuer, network, gateway, signed token, site limits (admin), cash drawer
src/GasPump.Security   hard limits, verifier, nonce store, pump authority, ledger, totalizer
src/GasPump.Pump       hydraulics, meter, taper policy, pump controller
src/GasPump.Dispatch   IDispatchStrategy, ThreadedDispatch, ChannelDispatch, Simulation, HeraldEventLog (Herald.OSS)
src/GasPump.Host       minimal API + WebSocket hub
app/                   Electron + Vite + Vue 3 + TypeScript
tests/GasPump.Tests    xunit on a FakeTimeProvider
docs/                  prd, threat model, architecture
```

## What this is not

No real card data. No EMV kernel. No PCI claim. No serial pump protocol. One pump, one hose, three grades. One shared HMAC key where a real site would use per-device keys and a MAC counter. Each of these is a stand-in for a shape, and the shape is what the repo teaches.

## Licence

MIT.
