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
| D7 | (Steve, 2026-11-11) Commit the X9.24-3 values that anchor A1 uses, now. The route is the recommendation of the licence memo by Dave, MMPWorks' in-house legal-documents advisor: commit only the hex inputs and expected outputs, in this repo's own file format, with a source line per group and a line that says ASC X9 has not endorsed this project; keep the PDF, its text and the full internal-state traces (section 2.3) out of git; keep the fetch script as the second check. The memo is guidance from an in-house advisor, not legal advice. It recommends a licensed IP attorney review before the values are committed. Steve accepted the legal risk of committing before that review. If ASC X9 objects, the file is removed and the fetch route stays. |

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

## 10. Design review (Richard, 2026-11-11)

The hypothesis in section 9 holds in shape. Ten changes follow from the real code. Each row
gives the change and the reason.

### 10.1 Library version and build

| # | Change | Reason |
|---|---|---|
| R1 | The library is `MMP.NetworkSecurity` at commit `698dd9c` (origin/main on 2026-11-11). `NetSecRoot` in `Directory.Build.props` defaults to `..\MMP.NetworkSecurity` beside this repo. A machine with the library elsewhere sets it in `Directory.Build.local.props` (git-ignored), or passes `-p:NetSecRoot=<dir>`. | D2 asks for a sibling default and an override. A local props file keeps machine paths out of committed files. The build stops with a named error when the library is missing. |
| R2 | `AsyncProxyRoot` moves to `Directory.Build.props`, beside `NetSecRoot`. With the sibling layout, `GasPump.Dispatch` and the library's `AsyncProxy.Local` compile `MMP.AsyncProxy` from one folder. A move of `MMP.AsyncProxy` goes on the command line (`-p:AsyncProxyRoot=<dir>`), so the library sees it too. | The spike (below) found no clash. Both copies have the same assembly name and version, and the build keeps one. A first try passed the root to the library with `AdditionalProperties`. That built `NetSec.Core` twice in parallel into one `obj` folder (CS2012), so it was dropped. |

Spike, 2026-11-11: a throwaway project referenced `GasPump.Dispatch` (sibling `MMP.AsyncProxy`)
and `NetSec.Core` (its own `AsyncProxy.Local`). `dotnet build` gave 0 warnings and 0 errors.
The compiler saw one `MMP.AsyncProxy.dll`. The program derived the X9.24-3 initial key
`1273671EA26AC29AFA4D1084127652A1`. The spike project was deleted.

### 10.2 Shape of the code

| # | Change | Reason |
|---|---|---|
| R3 | Two new projects, `GasPump.Reader` and `GasPump.Bank`. `IssuerSimulator` and its journal records move from `GasPump.Payment` to `GasPump.Bank`. `ProtectedCard` lives in `GasPump.Payment`. Only the two new projects reference `NetSec.Core`. A test checks the assembly references. | Section 9 put the bank's keys in the payment project. That project also holds the terminal's gateway, so a library type there leaks into the terminal. |
| R4 | The keypad digits go into the reader, not into the sale machine. `FuellingPoint.EnterCode` hands the digits to `CardReader.Keypad`, and the sale machine gets a masked entry (`****`). The machine accepts `*` in place of a digit. | A real encrypting PIN pad takes the PIN at the keys. In section 9 the PIN crossed the sale machine, the mailbox and the payment message in clear. |
| R5 | The reader reads the card by its name (the card in the slot) and runs the Luhn and format check. The gateway runs the expiry check on the clear expiry. `FuellingPoint` no longer touches `CardData`. | The terminal cannot run Luhn on ciphertext. The expiry is not secret. |
| R6 | The card number is sealed with AES-128-GCM (library `Gcm`) under the DUKPT data key (usage `0x3000`). That key and the PIN key come from one counter (`NextTogether`). The tag also covers the KSN, the PIN block and every clear field. The nonce is 12 zero bytes, because each data key seals one message only. | Attack (c) needs a refusal, and a cipher with no tag gives none. With the PIN block in the tag, attack (b) is a refusal at the bank too, with the code `CardDataUnreadable`. |
| R7 | The bank checks in this order: the GCM tag, then the counter (a per-device high-water mark and the library's `KsnLedger`), then the PIN with `DukptBank.OpenPinSale` for debit. A counter at or below the highest accepted one for its initial key ID is `KeyReplayed`; a gap ahead is accepted. (Changed after review, 2026-11-11: the ledger came first, which told a caller which KSNs the bank had seen.) | `CheckPinSale` changes nothing and does no tag check. The library says a caller with more checks makes them before `OpenPinSale`. The high-water mark sits in `GasPump.Bank` on top of the library; it is a library backlog item. |
| R7a | The fixed zero nonce is safe because one data key seals one message: the key comes from one counter of one initial key ID, the reader never reuses a counter, and the bank refuses to inject an initial key ID twice (`BANK.KEY.DUPLICATEID`, kept across restarts in the bank's books). A second reader with the same ID is the one way to repeat a (key, nonce) pair, and the guard closes it. | Review finding 7. |
| R8 | The bank's books leave the station journal. They go to `bank.json` beside `state.json`, with the KSNs the bank has seen. An older `state.json` that still holds the books loads them once. | The station journal held every PAN and PIN on file (`IssuerState`). Acceptance item 2 forbids that outside the bank. |
| R9 | Each station start injects each reader again. The initial key ID is the key set ID `12345678` and a 4-byte device ID: the pump number and 3 random bytes. | A restart starts the counter again at 1. A new device ID keeps every KSN new, so the saved KSN ledger refuses a replay across a restart. |
| R10 | Bank refusals are new `DeclineReason` values: `KeyReplayed` and `CardDataUnreadable`. A reader whose counter ran out declines the sale locally with the diagnostic code `READER.KEY.EXHAUSTED`. | The house pattern: one stable code per failure (`docs/diagnostics.md`). The terminal shows the reason; the code names it. |

### 10.3 Anchor changes

| # | Change | Reason |
|---|---|---|
| A1 | (0.2.1, D7) A1 has two tests. The first runs on every clone: the reader, the bank and the A2 reference derive each key and PIN block, and each one equals the committed file `tests/GasPump.Tests/Anchors/x9-24-3-aes-dukpt.vectors`. `tools/extract-x9-vectors.py` writes that file from the fetched PDF text; nobody types a value. The second test runs when the PDF text is present: every value in the committed file equals the text in its place (a key inside its transaction's section, a PIN block after "Encrypted PIN Block:" in its section). Without the text, the second test shows as skipped with the fetch command. `tools/fetch-x9-vectors.ps1` downloads `X9.24-3-2017-Test-Vectors-20180129-1.pdf` from x9.org, checks its SHA-256 (`ae9bb01d...c4cc`, the hash the library locks), and writes the text to `tests/GasPump.Tests/x9/` (git-ignored). | The PRD asked for the values as hex in the test. Until 0.2.1 the values stayed out of git, and a fresh clone skipped A1. D7 records Steve's decision to commit them on the route of Dave's licence memo. The second test keeps the PDF as the outside source: it proves the committed file is a true copy. |
| A2 | The second implementation in the test also derives the working keys (BCL AES-ECB, the X9.24-3 steps written out) and opens the sealed card number with the BCL `AesGcm`. | A2 then runs on every machine with no download. When the PDF text is present, A1 checks the same reference derivation against the published values. Without the PDF, the derivation half of A2 is a second implementation, not an outside source. |
| A4(e) | The reader erases the injected initial key after it loads the registers. The revert check for attack (e) removes that erase. | The register erase itself is library code (`DukptPinPad.Erase`). This repo cannot remove it (D3). |

### 10.4 Prior art

1. Gem catalog (`MMP.ProgrammingGems/INDEX.md`): took "Decide at the authoritative boundary".
   The reader takes the PIN at the keypad, and the bank alone decides on the card data. The
   `GEM:` marker and its usage-site line wait for Steve: they write to a second repo.
   Rejected "Replaceable seam": the reader has one implementation and no second use case.
2. System-design catalog (`MMP.Library/docs/system-design-catalog.md`): checked, and no entry
   covers card encryption. The shape is end-to-end encryption with a key at each end and a
   blind relay between them (the terminal). The arithmetic: one process, at most a few sales
   per minute per pump, and a KSN ledger that grows by one 24-character entry per card sale
   (about 1 MB after 40,000 sales). So no queue, no cache and no HSM service. The bank keeps
   no per-reader key and derives each key again from the KSN.
