# Diagnostic codes

One code per thing that can fail, named by the part. The same code appears in three places for the same event: the Herald debug log (as the `Code` property), the ledger entry (in brackets at the front of the detail), and the custody file (because the ledger and the log categories that carry codes are routed into it). A reader of any one of the three can name the part without the prose.

Form: `SUBSYSTEM.PART.FAULT`, or `SUBSYSTEM.FAULT` when the subsystem is the part. Uppercase, dots, no spaces. The codes are this simulator's own. Vendor forecourt codes are private to each vendor and aren't reproduced here.

| Code | Part | Meaning | Raised by | Cleared by | What the pump does | What the driver hears |
|---|---|---|---|---|---|---|
| `PUMP.STP.NOFLOW` | submersible pump and supply line | Valve open and nothing moved for the no-flow window (3 s): tank out, STP tripped, or line shut down by the tank gauge | `PumpController.Tick`, from the flow switch | `SupplyRestored` | Faults, closes the valve, keeps the metered volume. Refuses every arm while the supply is off | "Fuel supply stopped. You were charged only for the N gal delivered. Please use another pump." |
| `PUMP.MTR.NOPULSE` | meter pulser | Fuel moved through the flow switch and the pulser sent nothing for the no-pulse window (300 ms): unmetered delivery | `PumpController.Tick` | `PulserRepaired` | Faults within 300 ms; the unmetered volume goes in the fault entry and is never billed. Refuses every arm while the pulser is dead | "Meter fault. You were charged only for the N gal delivered. See the attendant before you leave." |
| `PUMP.MTR.OVERPULSE` | meter pulser | More pulses in one tick than the valve could pass: spoofed pulser | `PumpController.Tick` | `NextArm` | Faults dead, totalizer untouched | "Pump out of service." |
| `PUMP.MTR.STRAY` | meter pulser | Pulses while the valve was shut: stuck valve or bypass | `PumpController.Tick` | `NextArm` | Faults | "Pump out of service." |
| `PUMP.CTL.OVERCEILING` | pump controller | Priced volume exceeded the armed ceiling. Unreachable by construction; the ledger will say if it ever is not | `PumpController.Tick` | `NextArm` | Faults | "Pump out of service." |
| `PUMP.CTL.MAXTIME` | pump controller | Sale reached the hard duration limit | `PumpController.Tick` | `NextArm` | Stops (a stop, not a fault); the sale completes | "Time limit reached." |
| `PUMP.HOSE.BREAKAWAY` | hose breakaway coupling | Coupling separated and both halves sealed: drive-off or hose strike | breakaway sensor, read in `PumpController.Tick`; also `Arm` | `AttendantReset` | Faults at once if dispensing; refuses every arm until `ResetBreakaway` | "Hose breakaway. Do not drive away. See the attendant." |
| `PUMP.OPR.CLOSED` | pump switch | The operator closed the pump for sales. A state, never a fault: every part works | `PumpController.CloseForSales`, from the station's pump switch | `OperatorOpen` | Idle: moves to the `Closed` phase at once. Mid-delivery: finishes the delivery, then closes. Refuses every arm while closed. Survives a restart of either process | "Out of service: pump switch. The operator closed this pump for sales. The cashier can open it again. Please use another pump." |
| `STATION.ESTOP` | station emergency stop | Emergency shutoff pressed: power removed from every dispenser and every submersible pump | `Simulation.EmergencyStop` | `CashierReset` | Writes the record and a final snapshot, then the power drops. Reset is a cashier action; the restart closes the open sale from the final snapshot, with nothing unrecorded | On restart: "Pump restarted: closed out from the journal" with the E-stop named |
| `LINK.DISPENSER.LOST` | dispenser link | The station lost the dispenser process mid-sale: the socket closed, no frames come back | `Simulation.OnDispenserConnection`; also the offline view | `LinkRestored` | The station faults the sale for the fuel it recorded, settles that, and refuses payment until a dispenser answers. The dispenser, if it is still alive, keeps its own limits and stops on its own | "Pump offline. The station lost contact with this pump. You were charged only for the N gal delivered. See the attendant before you leave." |
| `LINK.DISPENSER.SILENT` | dispenser link | The socket is open and the dispenser stopped talking: two heartbeats missed, or a command the sale needs (`Arm`, `Close`) unanswered within `SimulationOptions.CommandTimeout`, or answered `Stale` | `Simulation.OnPeerAlive`, `Simulation.Unanswered`, `Simulation.OnAnswered` | `LinkRestored` | The station faults the sale for the fuel it recorded and settles that. The device may still be pouring under its own limits; the next heartbeat or the next connect clears the code | As `LINK.DISPENSER.LOST` |
| `PUMP.TX.PAYABLE` | transaction buffer | A delivered transaction still sits in the island's buffer, unlocked or uncleared by any station | `PumpController.Arm`, from `TransactionBuffer.HasOpen` | `NextArm` | Refuses the arm and answers `Busy`. The station clears the transaction once the money is settled or queued, and a new card during that settlement is told the pump is busy before the bank is asked | "Pump in use; settling the last sale." |
| `PUMP.PRICE.INVALID` | price board | The station wrote a board with a grade missing or a price at zero or below | `PumpController.SetPrices`, from `Pricing.Validate` | the next valid write | Refuses the write and answers `Refused`; keeps billing by the board it has. The station logs the refusal and the admin panel shows the pump still on the old board | Nothing; the face keeps showing the pump's price |
| `READER.KEY.EXHAUSTED` | card reader key registers | The card reader used every DUKPT counter value of its initial key (about 2^31 sales) | `CardReader.Read`, from the library error `DUKPT_COUNTER_EXHAUSTED` | `KeyInjected` | Nothing at the pump: the station declines the card sale with this code before the bank is called. Cash still works | "Card reader out of service, pay inside." |
| `TANK.FUELOUT` | underground tank | Product below the fuel-out level; supply to the pumps stops | `Simulation.TankRunsDry` (the tank gauge's alarm) | `SupplyRestored` | Supply off; the pump holds it as `PUMP.STP.NOFLOW` | As `PUMP.STP.NOFLOW` |

## The in-service rule

A code whose "Cleared by" is anything other than `NextArm` takes the pump out of service.

1. The pump reads its conditions from the part sensors each time it is asked: breakaway separated, supply off, pulser dead. Nothing is stored.
2. While any condition holds, the pump refuses every arm. The refusal names the first code.
3. While any condition holds, the station refuses every card and every cash payment. The reason names the codes.
4. The pump screen reads "Out of service: part" with the repair the part needs.
5. When the repair happens, a pump that faulted for that part goes back to `Disarmed` and writes `pump.in-service`. A sale that faulted for it clears to `Idle` at once, if the bank has answered about the money. If the bank has not answered, the screen holds and clears when it does.
6. A `NextArm` code describes one delivery. The pump stays `Faulted` until the next arm, and the next card is accepted.

## Where to look

- Log: filter on `pump`, `tank`, or `station`; hover a line for its `Code` and `Part` properties.
- Ledger tab: entries whose detail starts with `[CODE]`, plus `service.status` on the station and `pump.in-service` on the dispenser.
- Custody file: the same ledger entries and log lines, sealed. `grep PUMP.STP.NOFLOW custody-*.ndjson`.

## Adding a code

1. Add it to `DiagnosticCodes` in `src/GasPump.Core/Diagnostics.cs` with its part, meaning, and the `ServiceAction` that clears it.
2. Raise it through `EmergencyStop(code, detail)` or `code.Format(detail)` in a ledger entry, and put `Code` in the log properties.
3. If it takes the pump out of service, add its sensor to `PumpController.Conditions`, and give the repair a sentence in `SaleStateMachine.OutOfServiceNotice` if the action is new.
4. Give the driver a sentence in `SaleStateMachine.FaultNotice` if the next step differs from "use another pump".
5. Add the row here and a row to `docs/threat-model.md` if it defends against something.
