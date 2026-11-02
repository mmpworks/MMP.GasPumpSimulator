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
| **Fuelling point states** | The forecourt standard (IFSF Part 3-01) gives a fuelling point nine states. The pump controller's phases map onto them: Inoperative = `Faulted`; Closed = `Closed` (the operator's switch, every part works, every arm refused); Idle = `Disarmed` and `Stopped`; Idle with sub-state Authorised = `Armed` with the nozzle in the boot; Calling = `Calling` (nozzle off the boot, no authorization yet, the pump asking for one); Started = `Armed` with the nozzle out; Suspended started = `Armed` after a release before the first pour; Fuelling = `Dispensing`; Suspended fuelling = `Paused`. | `PumpPhase`, `PumpController.LiftNozzle` (Calling), `CloseForSales` / `OpenForSales` (Closed), `DiagnosticCodes.PumpClosed` |
| **Interlocks** | The conditions that must all be true before the valve opens: armed, grade selected, nozzle lifted from the boot, spout inserted (bellows pressed). The lever is then the driver's command. | `PumpController.SqueezeLever` |
| **Breakaway coupling** | A fitting in the hose that separates and seals both ends when a car drives off with the spout in the fill pipe. | `Hydraulics.BreakawaySeparated`, fault `PUMP.HOSE.BREAKAWAY`, `ResetBreakaway` |
| **Grade** | The product selected: Regular, Plus, Premium. Grade select buttons on the dispenser. | `Grade`, `PumpMessage.SelectGrade` |
| **Flow switch** (simulator instrument) | Real dispensers infer "valve open, nothing moving" from a silent pulser, and cannot see fuel moving when the pulser is dead. The simulator keeps a separate flow signal so the two faults can be told apart and the pulser can be checked against it. No real part corresponds. | `Hydraulics.FlowSwitch` |
| **Price board** | The posted per-gallon prices by grade. | `Pricing`, `IPriceSchedule` |
| **Unit price, in mills** | A posted price ends in nine tenths of a cent: $3.42 and 9/10 is $3.429. A tenth of a cent is a mill, so the code keeps every price as a whole number of mills per gallon (3429) and shows it with three decimals. Volume is kept in thousandths of a gallon. | `Pricing.TenthCentsPerGallon`, `Pricing.Display`, `Volume.MilliGallons` |
| **Money rounding** | The sale total is computed from the whole metered volume on every tick and rounded to the nearest cent once, half a cent and up rounding up (5.000 gal at $3.497 is $17.485, shown as $17.49). NIST Handbook 44 requires the money value to agree with the quantity "to the nearest 1 cent" (General Code G-S.5.5) and the total to be computed on quantity steps no coarser than 0.01 gal (Section 3.30, S.1.6.5.2); the simulator's 0.001 gal steps are finer. Handbook 44 names no tie rule, so half-up is this project's choice: it is what a driver gets doing the arithmetic on the receipt. Banker's rounding (half to even) belongs to reports that sum or average many sales, where it removes bias; it is never used on a single sale. | `Pricing.CentsFor`, test `Money_rounds_half_up_once_per_sale_and_on_steps_finer_than_handbook_44_asks` |

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

## The wire

| Term | What it is | In the code |
|---|---|---|
| **Forecourt protocol** | The language the station (forecourt controller) and each dispenser speak over the cable. It carries, in both directions: authorize this pump up to an amount, here is the price, nozzle lifted, delivering, here is the running volume and money, delivery ended, here are the totals, here is a fault. It never carries card data; the payment terminal has its own secured link. Three generations exist side by side on real forecourts. | The whole `PumpMessage` and `SaleEvent` traffic on `SocketDispenserLink` / `DispenserServer` is this protocol for one pump, as one JSON object per line over TCP |
| **Current loop (legacy)** | Each maker's own two-wire serial link, a current loop rather than a voltage signal so it survives long noisy runs across the forecourt. Slow, a few thousand bits per second. The controller is the master and polls each dispenser in turn; a dispenser speaks only when asked. Gilbarco Two-Wire, Wayne, and Tokheim each had one, and they are not interchangeable. | The simulator has no polling; the dispenser pushes events as they happen |
| **IFSF** | The International Forecourt Standards Forum. Its Dispenser application standard (Part 3-01) defines the messages; its transport parts carry them first over LON twisted pair (Part 2-01, IFSF-LON, the large installed base in Europe) and since 2015 over Ethernet as IFSF-IP (Part 2-02), with the same application layer. A REST API over HTTP (Part 2-03, with OpenRetailing) is the newest layer. One controller can then drive mixed makers. | Closest real shape to the simulator's wire: fixed message types, one link per dispenser, Ethernet transport |
| **CAN bus** | A multi-master serial bus made for short noisy runs between controller boards. It appears **inside** some dispenser cabinets, between the pump control board, pulser and valve drivers, and displays. It is not the station-to-dispenser link. | Nothing. The simulator's `PumpController` talks to `Hydraulics` and `Meter` as in-process objects, which stands in for whatever bus a cabinet uses internally |
| **Message envelope** | What every forecourt message carries around its body: who sent it (subnet and node), who it is for, its sequence number, what kind it is, when it was sent, and for a command the id its answer will quote and how long it stays good. The body is the device's record; the envelope is what a device protocol needs that a message bus does not. | `WireFrame`, `WireNode` (station `1.1`, pump `2.n`), `FrameCodec`, `WireSequencer` (numbers and a replay ring), `WireReceiver` (in order, duplicate, gap) |
| **Heartbeat** | A frame each side sends once a second to say it is alive, with its status word and the last number it sent. Two missed beats means the device stopped talking while the cable may be fine, which is a different fault from the cable going. Heartbeats take no sequence number. | `WireHeartbeat`, `Liveness`, `LINK.DISPENSER.SILENT` beside `LINK.DISPENSER.LOST` |
| **Answer (acknowledgement)** | What a command earns back, quoting the command's correlation id: Done, Refused with the device's reason, Busy when a payable transaction blocks it, Stale when it arrived after its time to live and was never run. The driver's hand messages earn no answer; the pump's events are their answer. | `WireAnswer`, `AnswerCode`, `IDispenserLink.Command` / `Answered`, ledger `cmd.answer` and `cmd.unanswered` |
| **Transaction buffer** | The forecourt standard's memory on the dispenser of each delivery not yet paid: number, volume, money, when, why it ended. The controller locks a transaction (this controller is settling it), then clears it (the money landed; forget it). While one is open the pump refuses to arm, so a delivery is never paid twice or lost between the hang-up and the settlement. A controller that restarts reads the buffer and settles from the dispenser's numbers, which are the true ones. | `TransactionBuffer`, `PumpTransaction`, `PumpMessage.Lock` / `Clear`, `SaleCommand.ClearTransaction`, `PUMP.TX.PAYABLE`, `read transactions` on connect, ledger `tx.payable` / `tx.locked` / `tx.cleared` / `tx.reported` / `tx.orphan` / `recovery.reconciled` |
| **Price write** | The controller sets the unit price per grade on the dispenser, and the dispenser keeps it across its own restarts. The dispenser answers the write. A new price written during a delivery applies to the next one, so the driver pays the price the pump showed. The dispenser bills by the board it holds, never by what the controller wishes it held. | `PumpMessage.SetPrices` as a `Write` frame, `PumpController.SetPrices` / `Prices` / `PendingPrices`, `DispenserView.Prices`, `Simulation.AdminSetPrice`, `PUMP.PRICE.INVALID`, ledger `admin.prices` / `price.set` / `price.pending` / `price.refused` |
| **Totals read** | The controller asks the dispenser for its lifetime counters: the sealed totalizer, and per grade (per nozzle in the standard) the volume, the money, and the count of deliveries. An inspector's close-out and a weights-and-measures check read these from the device, never from the controller's copy. | `read totals` answered with `PumpTotals` and `GradeTotal`, `GradeTotals` on the pump, `Simulation.ReadTotals` / `LastTotals`, ledger `totals.read` |
| **Connected dispenser (IoT)** | A dispenser with its own network identity that reports totals, faults, and tamper events to a cloud service, takes price and software updates over the air, and plays media on its screen. Both large makers sell this. It widens the attack surface, which is why the custody chain and "the pump pumps only under its own limits" matter more as dispensers connect. | The custody stream and the station's verified copy are the simulator's version of a dispenser reporting out; the hard limits in `PumpAuthority` are its defense |

## The payment path

| Term | What it is | In the code |
|---|---|---|
| **POS** | The register the cashier uses. Takes a card or cash at the counter and tells the forecourt controller which pump to authorize for how much. | `CardSource.Station`, `Simulation.PayCash`, the station column of the UI |
| **Forecourt controller (FCC, FDC)** | The one owner of every pump's state: idle, authorized for this much, delivering, delivered and unpaid. Every authorization, from the counter or from the pump's own reader, passes through it, and it allows one authorization per pump at a time. Often part of the POS system, sometimes its own box. One controller serves every fuelling point on the site. | `Simulation` (the site: one payment worker, one journal, one ledger) with one `FuellingPoint` per pump (its sale worker and `SaleStateMachine`), `FuellingPoint.BeginSale` (one open sale per pump), one `IDispenserLink` per pump |
| **Fuelling point (FP)** | One fuelling position: one side of a dispenser cabinet, with its own address on the forecourt wire and its own state. A cabinet with a hose on each side is two fuelling points. | `FuellingPoint` (one per pump on the station), `PumpNodes.Of` (node 2.n), sale ids `P{n}-S{k}` (`SaleIds`) |
| **EPS (electronic payment server)** | The one box in the store that talks to the card processor. The cashier's reader and every pump's reader all feed it. It asks for the hold, signs the token, settles the final amount, and keeps the store-and-forward queue. | `PaymentGateway`, `TokenSigner`, `PendingAdvice` |
| **Outdoor payment terminal (OPT; Gilbarco CRIND)** | The card reader, PIN pad, and small screen on the dispenser, with a secure processor that encrypts the card at the read head. It is a terminal of the store's EPS, not a payment processor of its own. Card data goes terminal, store network, EPS; it never rides the forecourt protocol and the pump controller never sees it. | Modeled only as `CardSource.PumpInsert` / `PumpTap`: the pump's reader posts `CardPresented` to the station. No terminal exists in the dispenser process, which is the truth of the real layout |
| **Payment host (processor, acquirer)** | The company on the far end of the store's internet link that routes the request to the card's bank and returns the answer. The link is broadband with a cellular backup; older sites used dial-up or satellite. | `NetworkSimulator` is the link, `IssuerSimulator` is the bank. The processor in between is folded into the two |
| **Pay at pump** | Card read at the OPT, hold from the EPS, authorization from the FCC to the dispenser, settlement at hang-up. | The default path in every sale test |
| **Pay at the counter (prepay)** | Card or cash at the POS, same EPS, same FCC message to the same pump. The pump cannot tell this path from pay at pump. | `CardSource.Station`, `PayCash`; the 300 s lift wait because the cashier can cancel by hand |
| **Postpay (pay inside after fueling)** | The cashier lets a pump run, the FCC holds the delivered sale against that pump as unpaid, the driver pays at the counter, the pump stays locked until the sale is cleared. | Not modeled |
| **Stand-in (offline) authorization** | When the link to the processor is down, the EPS may approve small sales itself up to a floor limit, store them, and send them when the link returns. The station carries the risk. | Half modeled: `StoreAndForward` sends completions and reversals later. Offline approval is not modeled; a dead link declines |

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
