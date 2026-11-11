# PRD: the card reader path with DUKPT (phase 1 of the security-library work)

Status: draft for build, 2026-11-11. Decision owner: Steve.
Target version: 0.2.0. Branch: `feat/dukpt-reader`.

## 1. What

The simulator gets a secure card reader. The reader encrypts the card number and the debit
PIN with AES DUKPT from `MMP.NetworkSecurity` before they leave the reader. The pump, the
terminal and the station never see the digits. Only the bank can open them.

## 2. Why

1. Today `CardData` crosses the simulator as plain text, and `docs/threat-model.md` lists
   card data under "does not defend, on purpose".
2. `MMP.NetworkSecurity` has a working AES DUKPT engine and PIN block format 4
   (`src/NetSec.Core/Dukpt/`). The simulator is the real-world use case that shows it at work.
3. Steve's decision (2026-11-11): option A (the card reader path) is phase 1. Option B (the
   pump-to-host link on the library's TLS 1.3 engine, and the library's HMAC and HKDF for
   the tokens) is the next sprint. The work is iterative, and each phase gets a version.

## 3. Decisions

| # | Decision |
|---|---|
| D1 | Phase 1 is the card reader path only. No TLS and no change to the token HMAC in this phase. |
| D2 | The library is used as a sibling checkout with a `ProjectReference`, the same way `MMP.AsyncProxy` is used today (`GasPump.Dispatch.csproj`). An MSBuild property overrides the path. The README names the library commit that this version was built and tested with. |
| D3 | The library code is not copied and not changed here. A defect in the library is fixed in `MMP.NetworkSecurity`. |
| D4 | The simulator version goes to 0.2.0 (`Directory.Build.props`). A new `CHANGELOG.md` records 0.1.0 and 0.2.0. The tag and the GitHub release need Steve's approval. |
| D5 | AES-128 DUKPT (ANSI X9.24-3-2017), PIN block format 4 (ISO 9564-1). One key per use: a PIN key for the PIN block, a data key for the card number. |
| D6 | All keys are training keys, written in the repo. The docs say so in the same place as each key. |

## 4. Acceptance

1. A credit sale and a debit sale complete end to end, in the tests and in the app, with
   the card number and the PIN encrypted at the reader.
2. Outside the reader and the bank, no message, journal, ledger entry, log line or receipt
   holds a full card number or a PIN. They hold the first 6 and last 4 digits at most.
3. Each attack below has a control and a named test that fails when the control is removed:
   a. Replay a recorded PIN block with its key serial number: the bank refuses it.
   b. Change one byte of the PIN block: the bank declines.
   c. Change one byte of the encrypted card number: the bank refuses it.
   d. A reader with the wrong base key (not injected for this bank): the bank refuses it.
   e. Read the reader's memory after a sale: the key of that sale cannot be derived from
      the keys the reader still holds.
   f. Scrape the terminal: acceptance item 2, as a test over every artifact of a full sale.
4. The app shows the reader: the key serial number and its counter, what the terminal
   sees (ciphertext and masked digits), what the bank opens, and a button for each attack
   that can run from the app.
5. `docs/threat-model.md` moves card data from "does not defend" to the control table,
   with the limits that remain. The README, the glossary and one article cover the reader.
6. The full test suite passes, and the count is reported as N of M. The existing tests
   keep passing with no test weakened.

## 5. Anchor (written before the code)

Each check uses data that the simulator code does not make.

| # | Check | Outside source |
|---|---|---|
| A1 | The working keys the reader and the bank derive for a published base key and key serial number equal the published values, written as hex in the test | The ANSI X9.24-3-2017 AES DUKPT test vectors (the source that `MMP.NetworkSecurity` cites in `acceptance/vectors/SOURCES.md`). If the vectors cannot be read, stop and report |
| A2 | A PIN block made by the reader opens with a second implementation written in the test (AES from the .NET BCL, the format 4 steps written out by hand from ISO 9564-1) | .NET BCL `System.Security.Cryptography.Aes` |
| A3 | Acceptance item 2: the test runs a credit sale and a debit sale, then searches every artifact for each test card number and PIN | Exhaustive search of the bytes on disk and on the wire |
| A4 | Each attack test of acceptance item 3 fails when its control is removed (revert check, recorded in the verdict) | The test run |

A test that compares the library with itself is not an anchor and is labelled as such.

## 6. Non-goals

1. Option B: TLS on the pump-to-host link, and the library's HMAC and HKDF for tokens.
2. EMV, contactless, and any claim of PCI PTS, SRED or P2PE compliance.
3. A hardware tamper boundary. The reader is a process boundary in a simulator.
4. A Compact Framework build of the reader.
5. Key injection ceremonies beyond one "inject the initial key" step.

## 7. Boundary for the builder

1. May do without asking: design inside this PRD, add a project for the reader, change
   `GasPump.Payment` and the bank simulation, add tests, change the app, change docs,
   commit on `feat/dukpt-reader`, push that branch, open a pull request.
2. Needs Steve's approval: merge to `main`, the tag, the GitHub release, any change in
   `MMP.NetworkSecurity`, any new package.
3. When a named step fails: the X9.24-3 vectors cannot be read (stop and report); the
   library has a defect (write it down with a failing test, stop and report); an existing
   test fails for a reason outside this work (report it, do not change the test).

## 8. Kill condition

Phase 1 ends when 6 of 6 acceptance items hold on the branch and Steve has run a credit
sale, a debit sale and the replay attack in the app. Date: 2026-11-18.

## 9. Program design (hypothesis; the builder confirms or changes it and records why)

1. `GasPump.Reader` (new): `CardReader` holds a `DukptPinPad`. `Present(card, pin?)`
   returns a `ProtectedCard`: key serial numbers, the encrypted card number, the PIN block
   for debit, the masked digits, the kind, the expiry, the ZIP for credit.
2. `GasPump.Payment`: the gateway and the terminal carry `ProtectedCard`. `CardData`
   stays inside the reader and the bank only.
3. The bank simulation holds the base key and a `KsnLedger`. It opens the card number,
   checks the PIN with `DukptBank.CheckPinSale`, and returns a decline code for each
   failure.
4. Build order: (1) anchor tests A1 and A2; (2) reader and bank, with the debit PIN only;
   (3) the card number; (4) the terminal scrape test A3; (5) the attacks; (6) the app;
   (7) docs, changelog, version.
