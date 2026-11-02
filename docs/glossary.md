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
| **Connected dispenser (IoT)** | A dispenser with its own network identity that reports totals, faults, and tamper events to a cloud service, takes price and software updates over the air, and plays media on its screen. Both large makers sell this. It widens the attack surface, which is why the custody chain and "the pump pumps only under its own limits" matter more as dispensers connect. | The custody stream and the station's verified copy are the simulator's version of a dispenser reporting out; the hard limits in `PumpAuthority` are its defense |

## The payment path

| Term | What it is | In the code |
|---|---|---|
| **POS** | The register the cashier uses. Takes a card or cash at the counter and tells the forecourt controller which pump to authorize for how much. | `CardSource.Station`, `Simulation.PayCash`, the station column of the UI |
| **Forecourt controller (FCC, FDC)** | The one owner of every pump's state: idle, authorized for this much, delivering, delivered and unpaid. Every authorization, from the counter or from the pump's own reader, passes through it, and it allows one authorization per pump at a time. Often part of the POS system, sometimes its own box. | `Simulation` (the sale worker and `SaleStateMachine`), `BeginSale` (one open sale per pump), `IDispenserLink` |
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
