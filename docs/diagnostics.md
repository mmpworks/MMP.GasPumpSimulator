# Diagnostic codes

One code per thing that can fail, named by the part. The same code appears in three places for the same event: the Herald debug log (as the `Code` property), the ledger entry (in brackets at the front of the detail), and the custody file (because the ledger and the log categories that carry codes are routed into it). A reader of any one of the three can name the part without the prose.

Form: `SUBSYSTEM.PART.FAULT`, or `SUBSYSTEM.FAULT` when the subsystem is the part. Uppercase, dots, no spaces. The codes are this simulator's own. Vendor forecourt codes are private to each vendor and are not reproduced here.

| Code | Part | Meaning | Raised by | What the pump does | What the driver hears |
|---|---|---|---|---|---|
| `PUMP.STP.NOFLOW` | submersible pump and supply line | Valve open and nothing moved for the no-flow window (3 s): tank out, STP tripped, or line shut down by the tank gauge | `PumpController.Tick`, from the flow switch | Faults, closes the valve, keeps the metered volume | "Fuel supply stopped. You were charged only for the N gal delivered. Please use another pump." |
| `PUMP.MTR.NOPULSE` | meter pulser | Fuel moved through the flow switch and the pulser sent nothing for the no-pulse window (300 ms): unmetered delivery | `PumpController.Tick` | Faults within 300 ms; the unmetered volume goes in the fault entry and is never billed | "Meter fault. You were charged only for the N gal delivered. See the attendant before you leave." |
| `PUMP.MTR.OVERPULSE` | meter pulser | More pulses in one tick than the valve could pass: spoofed pulser | `PumpController.Tick` | Faults dead, totalizer untouched | "Pump out of service." |
| `PUMP.MTR.STRAY` | meter pulser | Pulses while the valve was shut: stuck valve or bypass | `PumpController.Tick` | Faults | "Pump out of service." |
| `PUMP.CTL.OVERCEILING` | pump controller | Priced volume exceeded the armed ceiling. Unreachable by construction; the ledger will say if it ever is not | `PumpController.Tick` | Faults | "Pump out of service." |
| `PUMP.CTL.MAXTIME` | pump controller | Sale reached the hard duration limit | `PumpController.Tick` | Stops (a stop, not a fault); the sale completes | "Time limit reached." |
| `PUMP.HOSE.BREAKAWAY` | hose breakaway coupling | Coupling separated and both halves sealed: drive-off or hose strike | breakaway sensor, read in `PumpController.Tick`; also `Arm` | Faults at once if dispensing; refuses every arm until `ResetBreakaway` | "Hose breakaway. Do not drive away. See the attendant." |
| `STATION.ESTOP` | station emergency stop | Emergency shutoff pressed: power removed from every dispenser and every submersible pump | `Simulation.EmergencyStop` | Writes the record and a final snapshot, then the power drops. Reset is a cashier action; the restart closes the open sale from the final snapshot, with nothing unrecorded | On restart: "Pump restarted: closed out from the journal" with the E-stop named |
| `TANK.FUELOUT` | underground tank | Product below the fuel-out level; supply to the pumps stops | `Simulation.TankRunsDry` (the tank gauge's alarm) | Supply off; the pump sees it as `PUMP.STP.NOFLOW` on the next pour | As `PUMP.STP.NOFLOW` |

## Where to look

- Log: filter on `pump`, `tank`, or `station`; hover a line for its `Code` and `Part` properties.
- Ledger tab: entries whose detail starts with `[CODE]`.
- Custody file: the same ledger entries and log lines, sealed. `grep PUMP.STP.NOFLOW custody-*.ndjson`.

## Adding a code

1. Add it to `DiagnosticCodes` in `src/GasPump.Core/Diagnostics.cs` with its part and meaning.
2. Raise it through `EmergencyStop(code, detail)` or `code.Format(detail)` in a ledger entry, and put `Code` in the log properties.
3. Give the driver a sentence in `SaleStateMachine.FaultNotice` if the next step differs from "use another pump".
4. Add the row here and a row to `docs/threat-model.md` if it defends against something.
