# Changelog

Each version names what changed for a person who runs or reads the simulator. The version lives in `Directory.Build.props`. A tag `v<version>` on `main` marks each release.

## 0.2.0 (unreleased, branch `feat/dukpt-reader`)

The card reader path: phase 1 of the security-library work (`docs/prd-dukpt-reader.md`).

### Card reader

- A secure card reader, `GasPump.Reader`, on AES DUKPT (ANSI X9.24-3-2017) from [MMP.NetworkSecurity](https://github.com/mmpworks/MMP.NetworkSecurity) commit `698dd9c`. Each pump gets its own reader, injected with a new initial key at every station start.
- The keypad is the reader's PIN pad. The digits stay in the reader; the sale machine gets a star per digit.
- The card number leaves the reader sealed with AES-128-GCM under the sale's data key. The debit PIN leaves as an ISO 9564 format 4 PIN block under the sale's PIN key. The terminal sees the first 6 and last 4 digits.
- The bank, `GasPump.Bank`, opens the card: the KSN ledger refuses a replay (`KeyReplayed`), the GCM tag refuses a changed byte or a reader with another base key (`CardDataUnreadable`).
- The bank keeps its own books in `bank.json`, with the KSNs it accepted. The station journal no longer holds card numbers or PINs. An older `state.json` that holds the books loads them once.
- New diagnostic code `READER.KEY.EXHAUSTED` and service action `KeyInjected`.
- A reader panel in the app: the KSN and counter, what the terminal sees, what the bank opens, and five attack buttons (replay, a changed PIN block byte, a changed card number byte, a reader with another base key, read the reader memory).
- Threat model attacks 47 to 53, each with a named test. The limits stay in the threat model: training keys, no tamper hardware, no PCI claim.
- Anchor tests: A1 compares the derived keys and PIN blocks with the published X9.24-3 test-vector PDF (`tools/fetch-x9-vectors.ps1`); A2 opens what the reader made with the .NET BCL AES and AES-GCM.

### Verification on the build machine (2026-11-11)

With the Herald sources and the X9.24-3 vectors present. Without the vectors, the A1 tests show as skipped with the fetch command:

```
dotnet build MMP.GasPumpSimulator.slnx      0 Warning(s), 0 Error(s)
dotnet test MMP.GasPumpSimulator.slnx       Passed: 474, Skipped: 0, Total: 474
cd app && npm run e2e                       38 passed
```

Revert check: each control was removed, its tests failed, and the control was restored. Rows b to f ran at `2bff432`; the other rows ran at the review commits (`e166b51`, `160f436`), after which the bank's checks changed order.

| Attack | Control removed | Tests with the control removed | Restored |
|---|---|---|---|
| a | KSN ledger check in `BankKeys.Open` (at `2bff432`) | 2 of 2 fail | 2 of 2 pass |
| a | per-device counter high-water mark | 1 of 1 fails | passes |
| a | GCM tag before the ledger lookup (lookup moved first) | 1 of 1 fails | passes |
| nonce | refusal of a second injection of one initial key ID | 1 of 1 fails | passes |
| keypad | `Reader.Forget` at sale end | 3 of 3 fail | 3 of 3 pass |
| b | PIN block in the GCM associated data | 1 of 1 fails | passes |
| c | GCM tag check (ciphertext used without the tag) | 1 of 1 fails | passes |
| c | expiry and ZIP in the associated data | 1 of 1 fails | passes |
| d | GCM tag check | 1 of 1 fails | passes |
| e | erase of the injected initial key (also at `160f436`, with the array check) | 1 of 1 fails | passes |
| f | masking of the card number | 1 of 1 fails | passes |
| f | keypad digits kept in the reader | 1 of 1 fails | passes |

The register erase of attack (e) is library code. Its library test is `The_reader_cannot_rebuild_a_used_key_from_its_registers`.

### After review (2026-11-11)

- Records that hold a card number or a PIN (`CardData`, `CardOpening`, the bank's account and hold records) print them masked.
- The bank checks the GCM tag before the KSN ledger, so a refusal never tells a caller which KSNs it has seen. A counter at or below a device's highest accepted counter is `KeyReplayed`; a gap ahead is accepted.
- The bank refuses to inject one initial key ID twice (`BANK.KEY.DUPLICATEID`), across restarts. One data key seals one message, which keeps the fixed GCM nonce safe.
- The reader drops keypad digits at every sale end. The memory probe lost a flag that proved nothing; attack (e)'s test checks the injected array.
- The app's "another base key" attack reads its test card inside `GasPump.Reader`.
- Anchor A1 shows as skipped, with the fetch command, when the vectors are absent, and checks each PIN block in its own transaction's section. Anchor A2 writes the associated data, the nonce and the mask by hand.
- Scrape test A3 runs under all three dispatch strategies and includes every lab attack's text. A Playwright spec scrapes the host's HTTP and WebSocket bodies through a credit sale, a debit sale and the five attacks.
- Named limits: the app is the keypad, so a typed PIN reaches the host in clear before the reader seals it; the bank's PIN compare is not constant time.

### Build

- `NetSecRoot` and `AsyncProxyRoot` in `Directory.Build.props` name the sibling checkouts. A git-ignored `Directory.Build.local.props` overrides them on one machine.

### Also on main since 0.1.0

- The wire between the station and the island carries an envelope; commands are answered; island transactions reconcile; prices are a write the island keeps.
- One forecourt controller drives two fuelling points, one dispenser process each. A station carries its own name, theme and ports.
- A clone of the public repo builds and tests without the Herald.Compliance sources.
- A Release build ships a Release MMP.AsyncProxy. The AsyncProxy worker tries the mailbox before it awaits.
- The administrator panel lockout is 15 seconds.
- Docs: the Windows CE thread and memory budget, the CE build and test plan, the six articles in the repo, the chain-of-custody article.

## 0.1.0 (2026-11-02, tag `v0.1.0`)

The first release, built from the commits of 2026-10-31 to 2026-11-02.

- The sale, the payment gateway, the in-memory bank, the pump controller and the signed single-use token, with tests.
- A host with a WebSocket hub, and an Electron and Vue 3 front end on Vuetify 3.
- Three dispatch strategies: threads, Tasks and channels, and MMP.AsyncProxy state machines.
- Saga failover, driver notices, self-cancel timers and nozzle interlocks.
- Tender choice (credit, debit, cash), an administrator panel with a PIN, and a cash drawer.
- The log on Herald.OSS 0.14.0, a one-second journal with restart close-out, and a TesseraSeal custody file.
- Forecourt faults with diagnostic codes, a station E-stop, and a custody stream to the station.
- The dispenser and the station as two processes over a TCP socket.
- Itemized money end to end, and capabilities beside the sale on their own workers.
