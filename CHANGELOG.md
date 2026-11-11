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
