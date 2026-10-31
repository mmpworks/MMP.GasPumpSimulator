# MMP.GasPumpSimulator

A pay-at-the-pump fuel dispenser, simulated. Present a card, get a two-phase authorization, lift the nozzle, watch the flow taper as the sale closes in on its ceiling, and land on the cent. Then attack it: replay a token, tamper a ceiling, sign a 600-gallon authorization with the real key, spoof the pulser. The pump holds.

.NET 10 on the back, Electron and Vue 3 on the front, one WebSocket between them.

## What it covers

1. **Payment.** Luhn and expiry at the terminal. A simulated issuer with named test cards: `Good`, `LowBalance` (partial approval), `InsufficientFunds`, `Expired`, `Stolen`, `Frozen`, `Velocity`, `DebitGoodPin`, `DebitBadPin`. A network you can set to Up, Down, Timeout, or Flapping. Pre-authorization hold, completion for the real amount, reversal when no fuel moved, store-and-forward when the host is down at completion.
2. **Security.** The pump controller is the authority over fuel. It arms only from an HMAC-signed token it verifies itself, single-use by nonce, bound to its own pump id and a time window. Every ceiling is clamped to hard limits the pump owns: 50 gallons, $500, 10 minutes, 12 gal/min. There is no "set limit" message to attack. A hash-chained ledger records every arm, open, close, refusal, and fault. A sealed totalizer only goes up. Pulses the valve could not have passed stop the pump dead.
3. **The pump.** Hydraulics, meter, taper. With a $30 ceiling the flow is full to $24, falls in a straight line to an 8 percent creep, and the last slice is clamped so the money lands on 3000 cents and never 3001.
4. **Concurrency.** Three workers, sale, pump, and payment, pass messages through bounded mailboxes and share no state. How they run is a Strategy: `ThreadedDispatch` is a thread per worker with blocking queues. `ChannelDispatch` is a Task per worker over bounded channels. The third slot is for MMP.AsyncProxy state machines. One end-to-end test runs the same sale under every strategy.

## Run it

```
dotnet test                       # 80 tests, about 11 s, on a fake clock
cd app && npm install && npm run build && npx electron .
```

The Electron app starts the host for you on `localhost:5177`. Set `GASPUMP_STRATEGY=channel` to run the Task and Channel dispatch instead of threads. Set `GASPUMP_HOST=external` if you started the host yourself with `dotnet run --project src/GasPump.Host -- --strategy channel`.

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

## The 600-gallon case

In 2018 a Detroit station lost about 600 gallons in 90 minutes while the clerk could not stop the pump. The research behind this repo put the fix in one sentence: the reader protects the card, the controller protects the fuel. Two trust boundaries. This simulator builds the second one.

Press "Signed token: 600 gal, $2000" in the app. That token is signed with the real key, which models an upstream that is fully compromised. The pump authority verifies the signature, then clamps the ceiling to its own limits: 50 gallons, $500. The ledger shows `arm.ok ... clamped from $2000.00/600.000 gal to $500.00/50.000 gal`. Lift the nozzle and it stops at 50.000 gallons and $174.95. The test `Signed_600_gallon_token_is_clamped_to_50_gallons_and_500_dollars` runs that under both dispatch strategies.

`docs/threat-model.md` lists every attack and the test that proves its control.

## Layout

```
src/GasPump.Core       units, pricing, the sale state machine (a table, no I/O)
src/GasPump.Payment    cards, issuer, network, gateway, signed token
src/GasPump.Security   hard limits, verifier, nonce store, pump authority, ledger, totalizer
src/GasPump.Pump       hydraulics, meter, taper policy, pump controller
src/GasPump.Dispatch   IDispatchStrategy, ThreadedDispatch, ChannelDispatch, Simulation
src/GasPump.Host       minimal API + WebSocket hub
app/                   Electron + Vite + Vue 3 + TypeScript
tests/GasPump.Tests    xunit on a FakeTimeProvider
docs/                  prd, threat model, architecture
```

## What this is not

No real card data. No EMV kernel. No PCI claim. No serial pump protocol. One pump, one hose, three grades. One shared HMAC key where a real site would use per-device keys and a MAC counter. Each of these is a stand-in for a shape, and the shape is what the repo teaches.

## Licence

MIT.
