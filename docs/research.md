# Research register

What the simulator's design rests on, where each fact came from, and how far to trust it. One row per source. Every claim in the README, the PRD, the threat model, and the architecture doc that comes from outside this repo should trace to a row here. Add a row when a slice brings in a new source.

Evidence tiers, strictest first:

| Tier | Meaning |
|---|---|
| A | The standard's own text, or the vendor's own technical document |
| B | A regulator or inspector restating a standard (state fire marshal, state UST program, inspection form) |
| C | A vendor marketing or product page, a patent, or a trade-press article |
| D | A forum or a secondary summary. Direction only; never a number |

## Standards and codes

| Source | Tier | What it says | Where the simulator uses it |
|---|---|---|---|
| NFPA 30A section 6.7, emergency electrical disconnects, as restated by [IAEI, "Dispenser disconnects: critical safety net"](https://iaeimagazine.org/2008/november2008/dispenser-disconnects-critical-safety-net-that-is-overlooked/) | B | The emergency shutoff disconnects all dispensing devices, all remote pumps, all associated power, control, and signal circuits, and other electrical equipment in the classified area. Located 20 to 100 ft from the dispensers. Multiple devices are interconnected. Reset is manual and approved by the authority having jurisdiction. | The E-stop (slice 2d): station-wide, cuts the pump's power, cashier reset. The NFPA text itself was not read; verify the adopted edition before quoting a number. |
| NEC 514.11 and 514.13, as restated by the same IAEI article | B | Per-circuit disconnects for dispensing equipment, remote from the dispensers, opening all conductors at once; per-dispenser maintenance disconnect, lockable. | Distinguishes the E-stop (station-wide) from maintenance isolation (one pump). The simulator models only the E-stop. |
| [Montana DEQ, E-stop inspection form, PEI/RP1200-19 Appendix C-11](https://deq.mt.gov/files/Land/UST/Documents/PDFfiles/Montana_C-11_Emergency_Stop.pdf) | B | Each E-stop is tested alone. Pass requires power removed from all dispensers on all islands, all STPs for all grades, all power/control/signal circuits, and all non-intrinsically-safe equipment in the classified area. Intrinsically safe equipment stays energized. | The E-stop drill asserts the same four facts. The tank gauge keeps reading after an E-stop because it is intrinsically safe. |
| [Illinois Administrative Code Title 41, motor fuel dispensing](https://ilga.gov/ftp/JCAR/AdminCode/041/041001750B02500R.html) | B | Emergency stops are interconnected to shut off power to all dispenser and submersible pump motors from any one location. | One button stops every pump. |
| [Iowa DNR, International Fire Code disconnect language](https://iowadnr.gov/media/3086/download) | B | The disconnect shuts off the pumps, all dispensing devices, and all associated power, control, and signal circuits. | Same. |
| [West Virginia State Fire Marshal, unattended fuel dispensing](https://firemarshal.wv.gov/Documents/Unattended%20Fuel%20Dispensing.pdf) | B | Restates the 20 to 100 ft range, citing the 2003 editions of NFPA 30 and 30A. | Confirms the distance rule from a second state. |
| [Wisconsin ATCP 93.605 and the DATCP E-stop fact sheet](https://docs.legis.wisconsin.gov/document/administrativecode/ATCP%2093.605(5)(a)) | B | Adopts NFPA 30A section 6.7 by reference. The fact sheet URL returned 404 on 2026-11-01. | Shows adoption by reference is the common pattern. |
| [PEI, emergency shutoff valves](https://pei.org/?p=11381) | C | The shear valve sits in the product line at island grade under the dispenser. It closes on impact (the top shears, a spring poppet shuts) and on fire (a fusible link melts). Also called impact, fire, or crash valve. | The drive-off and fire paths. Works with no power. |
| MMPWorks, [Chain-of-Custody Specification PRD-3 (0.3.0)](https://github.com/mmpworks/ffiec-public), local `E:\dev\ffiec-public\spec\chain-of-custody-DRAFT-0.3.0.md` | A | Primitive 1: per-event HMAC-SHA-256 under an HKDF session key bound to the tenant, 32 zero bytes as genesis, JCS canonical payload excluding the stamp fields, NDJSON with a header line and a newline on every line, verifier steps 1 to 9 with fixed reason strings. Wire-or-on-disk observation only. Cross-run chain isolation. Distributed topology conformant. | The custody file, its verifier, one chain per run, verify-from-a-copy, and the planned export to the station server. Primitives 2 and 3 (daily Merkle seal, HSM signature) are out of scope and said so. |
| MMPWorks, TesseraSeal reference implementation, `E:\dev\Herald\Modules\Herald.Compliance\src\Audit` | A | `ImmutableAuditFileSink` writes the chain file; `VerifyChain` walks it; `CanonicalEventBuilder`, `KeyDerivation`, `HmacChainCore` implement primitive 1. Reads live files from a copy because the sink holds the file open for append. | Compiled into `GasPump.Custody` by source link. The simulator adds no cryptography. |
| Herald.OSS 0.14.0, [github.com/mmpworks/Herald.OSS](https://github.com/mmpworks/Herald.OSS) | A | Structured logging pipeline: bridge, file, console, and network sinks; categories; properties. The package exposes `Information`, `Warning`, `Error` on `StructuredLogger`. | The debug log, the UI bridge, and the custody bridge. The README's `Info` example did not compile against 0.14.0; fixed in Herald.OSS PR #18. |
| [NIST Handbook 44 (2023), Section 1.10 General Code](https://www.nist.gov/system/files/documents/2023/01/28/1-10-23-HB44.pdf), paragraph G-S.5.5 Money Values, Mathematical Agreement | A | "Any recorded money value and any digital money-value indication on a computing-type weighing or measuring device used in retail trade shall be in mathematical agreement with its associated quantity representation or indication to the nearest 1 cent of money value." G-S.5.2.2(c): a digital value "rounds off" to the nearest minimum unit that can be indicated or recorded. Read 2026-11-02 from the extracted PDF text. No tie rule (half up or half to even) is stated. | `Pricing.CentsFor` rounds the sale total to the nearest cent. The half-up tie rule is the project's own choice and is written down in `docs/glossary.md`. |
| [NIST Handbook 44 (2026), Section 3.30 Liquid-Measuring Devices](https://www.nist.gov/document/2026-nist-handbook-44-section-330), paragraphs S.1.6.5.2 and N.4.3 | A | S.1.6.5.2 Money-Value Divisions, Digital: a computing device with digital indications "shall comply with the requirements of paragraph G.S.5.5 ... and the total price computation shall be based on quantities not exceeding 0.05 L for devices indicating in metric units and 0.01 gal intervals for devices indicating in U.S. customary units." N.4.3 describes the money-value computation tests inspectors run. Read 2026-11-02. | The pump prices on 0.001 gal steps, ten times finer than the 0.01 gal the handbook allows. Pinned by the test named in the glossary. |

## Tank gauge, supply, and line faults

| Source | Tier | What it says | Where the simulator uses it |
|---|---|---|---|
| [Veeder-Root, ATG warnings and alarms](https://www.veeder.com/us/blog/warnings-and-alarms-veeder-root-automatic-tank-gauges) | A | Fuel Out: product below 10 inches, the tank cannot be pumped while active. Gross Test Fail and Periodic Test Fail halt dispensing if programmed. PLLD Shutdown: the line shuts down after a failed leak test or an alarm assigned to disable it. Sensor Open, High Liquid (8 inches in the dispenser pan, 10 in the sump), High Water, Overfill, Sudden Loss, Sensor Fuel, Continuous Handle, Delivery Needed. Which alarms appear depends on installed features. | Tank out (`TANK.FUELOUT`), the line shutdown signature, and the Continuous Handle idea behind the no-pour timer. Whether dispensing stops is a setting at the gauge, so the simulator makes it an explicit interlock rather than an assumption. |
| [Franklin Fueling, flow rate monitoring](https://www.franklinfueling.com/en/landing-pages/flow-rate-monitoring/) | C | The EVO gauge tracks flow per fueling point and alerts when a transaction falls below a set point. | The slow-flow warning threshold. |
| [Titan Cloud, clogged filters and flow rate](https://www.titancloud.com/blog/top-5-risks-of-clogged-filters-on-fuel-flow-rate-and-how-real-time-monitoring-helps/) | C | Clogged filters cut flow; below the meter's accurate range the meter drifts, quoted at 1 to 2 percent. | Why slow flow is a billing problem and not only a customer annoyance. The percentage is a vendor figure. |
| [Titan Cloud, nozzle-down slow flow](https://www.titancloud.com/blog/nozzle-down-slow-flow-dispenser-performance/) | C | Slow flow is dispensing slower than expected while the nozzle still works. | Definition used for the slow-flow fault. |

## Meter, pulser, and flow switch

| Source | Tier | What it says | Where the simulator uses it |
|---|---|---|---|
| [US 7,028,561, Gilbarco, fuel meter error detection](https://patents.google.com/patent/US7028561) and [EP 1844303 B1](https://data.epo.org/gpi/EP1844303B1) | C | A flow switch downstream of the meter, after the shear valve. Flow switch open with no pulses: meter or pulser fault. Pulses with the flow switch closed: flow switch fault. A no-pulse timer flags a stuck meter. Errors report to the site controller and on to a remote system. | The pulser-dead fault (`PUMP.MTR.NOPULSE`) and its mirror, the existing pulse-spoof fault (`PUMP.MTR.OVERPULSE`). The flow switch is modeled as "hydraulics delivered fuel". |
| [US 4,410,949, controller for fuel dispenser](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/4410949) | C | A pulser fault produces an error code that shuts the dispenser down until acknowledged. | A fault needs an attendant action before the next sale. |
| [US 6,975,964, dispensing efficiency monitoring](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/6975964) | C | Two-stage valves run slow on purpose at the start and end of a prepaid transaction, so slow-flow detection has to excuse those windows. | The slow-flow warning ignores the taper and the first second. |
| [US 8,444,014, fraud detection by low flow rate](https://patents.google.com/patent/US8444014) | C | Low flow with the nozzle active can be a fraud signature. | Why slow flow is also a security event. |
| [US 8,285,506, pulser arrangement](https://patents.google.com/patent/US8285506) and [Measurement Canada approval AV-2372](https://ic.gc.ca/pics/lm/volume/av/2372r11.pdf) | C | Encore registers use one pulser per meter (T18350 G5 or G6, M05839, M09261). | One pulser per hose in the model. |

## Hose, nozzle, and drive-off

| Source | Tier | What it says | Where the simulator uses it |
|---|---|---|---|
| [US 5,222,520, Dover, fuel hose breakaway](https://patents.justia.com/patent/5222520) and [US 5,135,029](https://patents.google.com/patent/US5135029) | C | A drive-off pulls the hose past a set force; shear pins release; poppet valves in both halves seal before separation so little fuel spills. | The breakaway fault (`PUMP.HOSE.BREAKAWAY`): flow stops at once, the sale closes for the fuel delivered, the attendant resets. |
| [US 6,692,034, breakaway device for fueling stations](https://patents.google.com/patent/US6692034) | C | Internal dual valves close during separation, stopping flow from the dispenser and from the separated hose. | Same. |
| [US 6,334,474, breakaway separation detection and alert](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/6334474) | C | A separation sensor raises an alert at the controller. | The breakaway is a sensed event, so the controller can name it rather than infer it from zero flow. |
| [US 5,363,889, fuel dispensing nozzle assembly](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/5363889) | C | A weakened spout section breaks off at low force, downstream of the nozzle's own shutoff valve. | Noted; not modeled. |
| Nozzle automatic shutoff (venturi) | D | Common knowledge in the trade; no primary source read for this project. | The full-tank click. Not modeled; the ceiling and the hang stand in for it. |

## Industry background

| Source | Tier | What it says | Where the simulator uses it |
|---|---|---|---|
| Notebook-Algorithms, `docs/system-design/fuel-dispenser-card-payment.md` | internal | Component map, the sale state machine (section 5.1), the pump-link threat (section 4.2), the 600-gallon Detroit case of 2018. | The repo is that note made runnable. |
| Gem `decide-at-authoritative-boundary`, `E:\dev\MMP.ProgrammingGems` | internal | The party with the context decides; an upstream value is a proposal. | `PumpAuthority.TryArm`: verify, then clamp to hard limits the pump owns. |
| Gem `back-pressure-ring` | internal | A pulse stream must never drop a count; the producer waits. | Bounded mailboxes that block the producer. |
| [Franklin Fueling, E-stop solutions](https://www.franklinfueling.com/en/landing-pages/integrated-electrical-controls/e-stop-solutions/) | C | E-stop operators shut off dispensers and submersible pumps; cashier control stations provide the reset the NEC requires. | The cashier reset after an E-stop. |
| [Mike Holt forums, fuel pump E-stops](https://forums.mikeholt.com/threads/fuel-pump-estops.2566694/post-2783881) | D | Electricians disagree on whether every button must clear 20 ft or only one. | Not used for any rule. Recorded as a known ambiguity. |

## Open questions this register has not settled

1. Whether a Fuel Out alarm stops dispensing at the pump or only at the gauge is a site setting. The simulator makes it an explicit interlock and says so.
2. The nozzle venturi shutoff has no primary source here.
3. The NFPA 30A text was read only through restatements. Before any number from it goes into an outward claim, read the adopted edition.
4. Forecourt controller event codes (Gilbarco Encore, Wayne, Verifone Commander) are vendor-confidential; the diagnostic codes in this simulator are its own and are documented in `docs/diagnostics.md`.
