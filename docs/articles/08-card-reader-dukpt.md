# The card reader: from the reader on, the card is ciphertext

From version 0.2.0 the simulator's card reader encrypts the card number and the PIN. The sale machine, the payment gateway, the journal and the log get ciphertext and masked digits. Only the bank opens them. This article follows one debit sale through the reader, the terminal and the bank, then runs the attacks against it. Every claim names the test or the command that checks it.

The keys are training keys. The reader is a process boundary in a simulator, not a tamper-resistant device. The simulator makes no PCI claim. The limits are in `docs/threat-model.md`, "What the simulator does not defend".

## 1. Before 0.2.0

The card was a `CardData` record in clear. The keypad digits went into the sale machine, onto the payment message, and to the bank. The station journal held the bank's books, with every card number and every PIN on file. The threat model said so: card data was not defended, on purpose.

## 2. The parts

| Part | Project | Holds |
|---|---|---|
| Card reader and PIN pad | `GasPump.Reader` | the card in the slot, the keypad digits, the DUKPT key registers |
| Terminal (sale machine, payment gateway, journal, log) | `GasPump.Core`, `GasPump.Payment`, `GasPump.Dispatch` | a `ProtectedCard`: ciphertext, a PIN block, masked digits |
| Bank | `GasPump.Bank` | the base derivation key (BDK), the accounts, the KSNs it accepted |

The DUKPT engine and PIN block format 4 come from [MMP.NetworkSecurity](https://github.com/mmpworks/MMP.NetworkSecurity) commit `698dd9c`. Only the reader and the bank reference it. `Only_the_reader_and_the_bank_reference_the_security_library` checks the assembly references of the six terminal projects.

## 3. One debit sale

1. **Injection.** At station start, each pump's reader gets an initial key ID: the key set ID `12345678`, the pump number, and 3 random bytes. The bank derives the reader's initial key from its BDK and that ID (`Simulation.InjectReader`). The reader loads its 32 key registers and erases the initial key.
2. **Keypad.** The driver types `1234` in the app, which posts it to the host. `FuellingPoint.EnterCode` hands the digits to `CardReader.Keypad`, which keeps them and returns `****`. The sale machine checks the shape of `****` and asks for authorization.
3. **Read.** `CardReader.Read` takes the next counter. From that one counter it derives two keys: the PIN key (usage `0x1000`) and the data key (usage `0x3000`). It then erases the register they came from.
   - The PIN goes into an ISO 9564-1 format 4 block under the PIN key.
   - The card number is sealed with AES-128-GCM under the data key. The tag also covers the KSN, the PIN block, the masked number, the kind, the expiry and the ZIP (`ProtectedCard.AssociatedData`).
4. **Terminal.** The gateway gets a `ProtectedCard`. It checks the clear expiry and forwards the rest. It holds no key.
5. **Bank.** `BankKeys.Open` checks in this order: the KSN ledger, the GCM tag, the PIN block (`DukptBank.OpenPinSale`). Then the account checks run as before 0.2.0.

The app shows the three views on the station side, in the "Card reader · AES DUKPT" panel: the reader's KSN and counter, what the terminal sees, and what the bank opened (a length, the last 4, the PIN result).

## 4. How you know the keys are right

Two anchors use data the simulator does not make.

- **A1, the published vectors.** `X9VectorTests` checks these values against the ANSI X9.24-3-2017 test-vector supplement:
  - the initial key;
  - the PIN keys and data keys for counters 1 to 8 and 8675309;
  - the reader's PIN blocks for transactions 1 to 8.

  The values sit in the committed file `tests/GasPump.Tests/Anchors/x9-24-3-aes-dukpt.vectors`, so the first A1 test runs on every clone. `tools/extract-x9-vectors.py` wrote the file from the PDF text; nobody typed a value. The PDF is copyright ASC X9 and stays out of git. `pwsh tools/fetch-x9-vectors.ps1` downloads it from x9.org, checks its SHA-256 (`ae9bb01d...c4cc`), and extracts its text. The second A1 test then checks that every value in the committed file equals the PDF text in its place.
- **A2, a second implementation.** `ReferenceAnchorTests` opens the reader's PIN block and sealed card number with the .NET BCL AES and AES-GCM, and the format 4 steps written out in the test. It also builds a message with the BCL alone and checks that the bank opens it.

The run at 0.2.1, with the PDF text fetched:

```
dotnet test tests/GasPump.Tests --filter "FullyQualifiedName~Anchors|FullyQualifiedName~ReaderAttackTests"
Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26
```

## 5. The attacks

| Attack | What the bank says | Test |
|---|---|---|
| Replay a recorded PIN block with its KSN | `KeyReplayed` | `Attack_a_a_recorded_pin_block_sent_again_with_its_ksn_is_refused_as_a_replay` |
| The same replay after the bank restarts from `bank.json` | `KeyReplayed` | `Attack_a_a_replay_after_the_bank_restarts_from_its_books_is_still_refused` |
| Change one byte of the PIN block (each of the 16) | `CardDataUnreadable` | `Attack_b_one_changed_byte_in_the_pin_block_is_refused_at_the_bank` |
| Change one byte of the sealed card number (each byte) | `CardDataUnreadable` | `Attack_c_one_changed_byte_in_the_sealed_card_number_is_refused_at_the_bank` |
| "Fix" a wrong ZIP, or the expiry, in transit | `CardDataUnreadable` | `Attack_c_a_changed_clear_field_is_refused_because_the_tag_covers_it` |
| A reader injected from another base key | `CardDataUnreadable` | `Attack_d_a_reader_injected_from_another_base_key_is_refused_at_the_bank` |
| Read the reader's memory after a sale | the sale key cannot be rebuilt; the next key can, so the probe works | `Attack_e_the_keys_left_in_the_reader_after_a_sale_cannot_give_that_sale_key` |
| Scrape the terminal after a credit and a debit sale | no full card number and no PIN in the log, the ledger, the journal, the custody file, the sale events, the pump screens, or the reader panel | `Attack_f_after_a_credit_sale_and_a_debit_sale_no_terminal_artifact_holds_a_card_number_or_the_pin` |

The scrape test also reads `bank.json` and finds both card numbers there. That shows the search can see a number when one is present.

Each test fails when its control is removed. The revert checks are recorded in the branch verdict.

## 6. What this does not show

- That the keys are safe. They are the published test keys, and they sit on the managed heap.
- That the reader resists a physical attack. It has no tamper boundary.
- A key injection ceremony, EMV, contactless, or any PCI PTS, SRED or P2PE property.
- A PIN pad inside the reader. In the simulator the app is the keypad: the typed PIN goes from the app to the host in clear over `POST /api/pump/{n}/code`, and the host hands it to the reader in the same process. The host logs no request body and journals no keypad entry. From the reader onward the PIN is inside a PIN block.
- The pump-to-host link. TLS 1.3 on that link, and the library's HMAC and HKDF for the tokens, are the next phase.

---

Describes branch `feat/dukpt-reader`, version 0.2.0, MMP.NetworkSecurity `698dd9c`.
