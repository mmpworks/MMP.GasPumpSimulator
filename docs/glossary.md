# Glossary

The names in the code follow the fuel-dispenser trade, so a technician and a programmer can read the same word and mean the same part. This page is the source for those words. When a name in code, a comment, a log line, the UI, or an article disagrees with this page, the page wins and the other one changes.

Each entry gives the trade term, what it is, and the names the code uses for it.

## The island

| Term | What it is | In the code |
|---|---|---|
| **Dispenser** | The cabinet on the island. It meters and controls fuel that a pump in the tank pushes to it. A dispenser has no pump of its own; "pump" is what drivers call it. | `GasPump.Dispenser.exe`, `Dispenser`, `PumpController` (the name "pump" stays in the code's project names because drivers say it) |
| **Island** | The raised concrete pad the dispensers stand on. | the dispenser process, "the island" in docs |
| **Submersible turbine pump (STP)** | The pump that sits in the underground tank and pushes fuel to the dispensers under pressure. | `Hydraulics.SupplyAvailable`, `PumpMessage.Supply`, fault `PUMP.STP.NOFLOW` |
| **Hydraulics** | The fluid side of the dispenser: the valve, meter, strainer, piping. | `Hydraulics` |
| **Solenoid valve** | The electrically operated valve that lets fuel through. Real units have a fast-flow and a slow-flow stage; the slow stage brings the sale in on the preset amount. The simulator models the stages as a percentage opening. | `Hydraulics.SetOpening`, `Hydraulics.Close`, `TaperPolicy` |
| **Slow-flow stage** | The valve's reduced position near the end of a sale, so the last cents land on the preset to the penny. The code calls this the taper. | `TaperPolicy`, `PumpTick.TaperPhase` ("full", "taper", "creep") |
| **Meter** | The positive-displacement (PD) meter that measures fuel by volume as it passes. | `Meter` |
| **Pulser** | The pulse transmitter on the meter shaft. It sends a fixed number of electrical pulses per gallon; the electronics count pulses to get volume. | `Meter` pulse count, fault `PUMP.MTR.NOPULSE`, `PUMP.MTR.OVERPULSE` |
| **Totalizer** | The lifetime volume counter for one hose. It never resets, and inspectors read it. | `Totalizer` |
| **Nozzle** | The hand-held unit at the end of the hose: spout, lever, hold-open latch, automatic shut-off. | `Nozzle*` events, `LiftNozzle`, `HangNozzle` |
| **Nozzle boot** | The holster on the dispenser where the nozzle rests. The **boot switch** tells the electronics the nozzle was lifted or returned. Older docs called this the boot. That word is retired. | `PumpMessage.Lift`, `PumpMessage.Hang`, `SaleEvent.NozzleLifted`, `SaleEvent.NozzleHung`, `_nozzleLifted` |
| **Lever** | The handle on the nozzle the driver squeezes to open the nozzle valve. Older docs called it the trigger. That word is retired. | `PumpMessage.Squeeze`, `PumpMessage.Release`, `SqueezeLever`, `ReleaseLever`, `_leverHeld`, `LeverHeld` |
| **Spout** | The metal tube of the nozzle that goes into the vehicle's fill pipe. | `InsertSpout`, `WithdrawSpout` |
| **Fill pipe** | The vehicle's fuel filler neck. Older docs said fill point. That word is retired. | strings "fill pipe" |
| **Bellows interlock** | On a vapor-recovery nozzle, a rubber bellows around the spout must be pressed against the fill pipe before the lever can open the valve. The simulator models this as the spout-inserted condition and reads it as a switch. A conventional nozzle has no such sensor; its full-tank shut-off is a mechanical venturi inside the nozzle. | `PumpMessage.InsertSpout`, `PumpMessage.WithdrawSpout`, `SaleEvent.SpoutInserted`, `SaleEvent.SpoutWithdrawn`, `PumpController.InsertSpout`, `_spoutInserted`, `SpoutInserted` |
| **Interlocks** | The conditions that must all be true before the valve opens: armed, grade selected, nozzle lifted from the boot, spout inserted (bellows pressed). The lever is then the driver's command. | `PumpController.SqueezeLever` |
| **Breakaway coupling** | A fitting in the hose that separates and seals both ends when a car drives off with the spout in the fill pipe. | `Hydraulics.BreakawaySeparated`, fault `PUMP.HOSE.BREAKAWAY`, `ResetBreakaway` |
| **Grade** | The product selected: Regular, Plus, Premium. Grade select buttons on the dispenser. | `Grade`, `PumpMessage.SelectGrade` |
| **Flow switch** (simulator instrument) | Real dispensers infer "valve open, nothing moving" from a silent pulser, and cannot see fuel moving when the pulser is dead. The simulator keeps a separate flow signal so the two faults can be told apart and the pulser can be checked against it. No real part corresponds. | `Hydraulics.FlowSwitch` |
| **Price board** | The posted per-gallon prices by grade. | `Pricing`, `IPriceSchedule` |

## The station

| Term | What it is | In the code |
|---|---|---|
| **Station** | The computer inside the store that runs sales, payment, and the records. In the trade this is the point-of-sale and the forecourt controller together. | `GasPump.Host.exe`, `Simulation` |
| **Forecourt** | The whole fueling area: islands, dispensers, canopy. | docs |
| **EPS (electronic payment server)** | The station box that talks to the bank on behalf of every dispenser. | `PaymentGateway` |
| **Issuer** | The bank that issued the card and decides on the hold. | `IIssuer`, `IssuerSimulator` |
| **Pre-authorization hold** | The amount the bank sets aside before fuel moves. Settles to the real amount at completion. | `HoldStatus`, `PreAuthorizeAsync` |
| **Preset / authorized amount** | The most a sale may dispense, in money and in volume. The code calls this the ceiling. | `Ceiling`, `MaxVolume` |
| **Completion** | The message that settles the hold to the real amount. Also called the completion advice. | `SendCompletion`, `CompleteAsync` |
| **Reversal** | Giving back a hold when no fuel was sold. | `Reverse`, `ReverseAsync` |
| **Partial approval** | The bank approves less than asked. Required for fuel (MCC 5542). | `IssuerDecision.Partial` |
| **PIN** | The debit card's personal identification number, typed at the keypad. | `CardData.Pin`, `DeclineReason.InvalidPin` |
| **AVS / billing ZIP** | Address verification: the credit card's billing ZIP code, typed at the keypad, checked by the issuer. | `CardData.Zip`, `DeclineReason.ZipMismatch` |
| **Token** | The signed permission the station gives the dispenser: sale, pump, preset, lifetime, nonce. | `AuthorizationToken`, `TokenSigner` |
| **Nonce** | A number used once, carried on the token so a replayed token is refused. | `NonceStore`, `VerifyFailure.Replayed` |
| **Store and forward** | Holding a completion or reversal when the bank is unreachable and sending it when the link returns. | `PendingAdvice`, `DrainPendingAsync` |
| **Custody chain** | The tamper-evident record (TesseraSeal) each process writes and the station copies. | `ImmutableAuditFileSink`, `CustodyStream`, `CustodyCopy` |
| **Journal** | The station's one-second snapshot of the open sale, for restart recovery. | `StateJournal`, `state.json` |

## Software words that stay software words

| Term | Meaning here |
|---|---|
| **Worker** | A loop that takes one message at a time from its mailbox. Three of them: sale, payment, pump. |
| **Mailbox** | A bounded queue owned by one worker. |
| **Message** | A record with a type name: `SaleEvent`, `SaleCommand`, `PumpMessage`, `PaymentMessage`. |
| **Wire** | The TCP link between the station and the dispenser, one JSON object per line. |
| **Ticker** | The 100 ms timer that drives the pump worker's `Tick`. |
| **Dispatch strategy** | How workers run: threads, Tasks over channels, or AsyncProxy iterators. |

## Retired words

| Retired | Use instead |
|---|---|
| harness (the nozzle holder) | nozzle boot |
| trigger | lever |
| seat, seated, unseat | insert spout, spout inserted, withdraw spout |
| fill point | fill pipe |
| ceiling (in prose for a reader from the trade) | preset or authorized amount; `Ceiling` stays as the code name |
