"""Write the AES DUKPT vector file of anchor A1 from the fetched X9.24-3 text.

Run it after tools/fetch-x9-vectors.ps1:

    python -I tools/extract-x9-vectors.py

It reads tests/GasPump.Tests/x9/X9.24-3-2017-Test-Vectors.txt (git-ignored, copyright ASC X9)
and writes tests/GasPump.Tests/Anchors/x9-24-3-aes-dukpt.vectors (committed). It copies only the
values that anchor A1 uses. Each value is found by its label in the text. A label that is missing,
or a label whose occurrences give two different values, stops the run with exit code 1, and the
tool writes nothing.
"""

import hashlib
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
X9_DIR = ROOT / "tests" / "GasPump.Tests" / "x9"
PDF_NAME = "X9.24-3-2017-Test-Vectors-20180129-1.pdf"
PDF = X9_DIR / PDF_NAME
TEXT = X9_DIR / "X9.24-3-2017-Test-Vectors.txt"
OUT = ROOT / "tests" / "GasPump.Tests" / "Anchors" / "x9-24-3-aes-dukpt.vectors"

# The hash that tools/fetch-x9-vectors.ps1 checks. The source lines name it.
PDF_SHA256 = "AE9BB01D76EDA32018332F1202A9C2010097ABF0D17D753612B7E612DDD9C4CC"
PDF_DATE = "2018-01-29"
SOURCE = f"{PDF_NAME} (ASC X9, {PDF_DATE}, SHA-256 {PDF_SHA256})"

# The transactions that anchor A1 checks. Keys for each one; PIN blocks for the reader's first eight reads.
KEY_COUNTERS = [1, 2, 3, 4, 5, 6, 7, 8, 8675309]
PIN_BLOCK_COUNTERS = [1, 2, 3, 4, 5, 6, 7, 8]

WORD = "[0-9A-F]{8}"
SECTION_HEADER = re.compile(r"^All Key Usages for Transaction (\d+)\b")
SECTION_END = "2.3 Full internal state"


class NotFound(Exception):
    """A value that the tool cannot find by its label without a guess."""


def is_page_furniture(line):
    """The page footer, the running header and the page number, which pdftotext puts between lines."""
    return (
        "ASC X9, Inc. 2018 - All rights reserved" in line
        or line.startswith("Supplement to ANSI X9.24-3-2017")
        or re.fullmatch(r"\d{1,3}", line) is not None
    )


def content_lines(text):
    lines = (line.strip() for line in text.splitlines())
    return [line for line in lines if line and not is_page_furniture(line)]


def labelled_value(flat, label, word_count, where):
    """The value after `label`, as `word_count` hex words. Every occurrence must give the same value."""
    words = r"\s+".join([f"({WORD})"] * word_count)
    pattern = re.escape(label) + r"\s+" + words + r"(?![0-9A-F])"
    found = {"".join(m.groups()) for m in re.finditer(pattern, flat)}
    if len(found) != 1:
        raise NotFound(f"{where}: '{label}' followed by {word_count} hex words gives {len(found)} distinct values {sorted(found)}")
    return found.pop()


def transaction_sections(lines):
    """Counter -> the content lines of 'All Key Usages for Transaction <counter>'."""
    starts = [(i, int(m.group(1))) for i, line in enumerate(lines) if (m := SECTION_HEADER.match(line))]
    end_of_all = next((i for i, line in enumerate(lines) if line.startswith(SECTION_END)), len(lines))
    sections = {}
    for k, (start, counter) in enumerate(starts):
        end = starts[k + 1][0] if k + 1 < len(starts) else end_of_all
        if counter in sections:
            raise NotFound(f"transaction {counter}: the section appears twice")
        sections[counter] = lines[start:end]
    return sections


def pin_block(section_lines, where):
    """The first three words follow 'Encrypted PIN Block:'. The fourth is the last word of the next line,
    where pdftotext puts the second column. The same rule as the position check in X9VectorTests."""
    head = [i for i, line in enumerate(section_lines) if "Encrypted PIN Block:" in line]
    if len(head) != 1:
        raise NotFound(f"{where}: {len(head)} lines hold 'Encrypted PIN Block:'")
    i = head[0]
    first = re.search(rf"Encrypted PIN Block: ({WORD}) ({WORD}) ({WORD})$", section_lines[i])
    second = re.fullmatch(rf"{WORD} {WORD} ({WORD})", section_lines[i + 1]) if i + 1 < len(section_lines) else None
    if first is None or second is None:
        raise NotFound(f"{where}: the PIN block lines do not have the expected layout")
    return "".join(first.groups()) + second.group(1)


def transaction(counter, section_lines, key_id):
    where = f"transaction {counter}"
    flat = " ".join(section_lines)
    if f"Counter: {counter}" not in flat or f"( 0x{counter:x} )" not in flat:
        raise NotFound(f"{where}: the section does not print counter {counter} (0x{counter:x})")
    derivation = labelled_value(flat, "PIN Encryption Derivation Data:", 4, where)
    if derivation[16:24] != key_id[8:] or derivation[24:] != f"{counter:08X}":
        raise NotFound(f"{where}: the PIN key derivation data {derivation} does not end in the key ID and the counter")
    values = {
        "ksn": key_id + f"{counter:08X}",
        "pin-key": labelled_value(flat, "PIN Encryption Key:", 4, where),
        "data-key": labelled_value(flat, "Data Encryption, Encrypt:", 4, where),
    }
    if counter in PIN_BLOCK_COUNTERS:
        values["pin-block"] = pin_block(section_lines, where)
    return values


def pin_block_inputs(sections):
    found = set()
    for counter in PIN_BLOCK_COUNTERS:
        flat = " ".join(sections[counter])
        found |= set(re.findall(r"PAN = (\d+) PIN = (\d+) Random Number = ([0-9A-F]{16})\b", flat))
    if len(found) != 1:
        raise NotFound(f"PIN block inputs: {len(found)} distinct sets {sorted(found)}")
    pan, pin, rnd = found.pop()
    return {"pan": pan, "pin": pin, "random": rnd}


def extract(text):
    lines = content_lines(text)
    flat = " ".join(lines)
    sections = transaction_sections(lines)
    missing = [c for c in KEY_COUNTERS if c not in sections]
    if missing:
        raise NotFound(f"no section for transactions {missing}")

    key_id = labelled_value(flat, "InitialKeyID:", 2, "section 2.2")
    initial_keys = {labelled_value(" ".join(sections[c]), "Initial Key:", 4, f"transaction {c}") for c in KEY_COUNTERS}
    if len(initial_keys) != 1:
        raise NotFound(f"the transaction sections give {len(initial_keys)} distinct initial keys")

    groups = [
        ("setup", '"Test Vectors for Various Transaction Keys": BDK-128, InitialKeyID, and the AES-128 Initial Key of each transaction', {
            "bdk": labelled_value(flat, "BDK-128:", 4, "section 2.2"),
            "initial-key-id": key_id,
            "initial-key": initial_keys.pop(),
        }),
        ("pin-block-inputs", f'"Calculation of AES PIN Block (Format 4)", the same in transactions {PIN_BLOCK_COUNTERS[0]} to {PIN_BLOCK_COUNTERS[-1]}',
            pin_block_inputs(sections)),
    ]
    for counter in KEY_COUNTERS:
        context = f'"All Key Usages for Transaction {counter} (AES-128 under AES-128 BDK)"; ksn is the InitialKeyID and the counter (0x{counter:x})'
        groups.append((f"transaction {counter}", context, transaction(counter, sections[counter], key_id)))
    return groups


HEADER = """\
# AES DUKPT test values for anchor A1 of MMP.GasPumpSimulator (docs/prd-dukpt-reader.md, section 5).
#
# The values come from the supplement to ANSI X9.24-3-2017, the AES DUKPT test vectors published by
# ASC X9, Inc.: {source}.
# ASC X9 has not reviewed or endorsed this project.
# These values are test data from a published file. The keys protect nothing.
# The values are facts from the ASC X9 supplement. The MIT licence of this repo covers MMPWorks' code.
# This file makes no claim of ownership over the values.
#
# tools/extract-x9-vectors.py wrote this file from the fetched PDF text. Run the tool again; do not edit by hand.
# Format: "[group]", then "source:", then one "name = value" per line. Values are hex, except pan and pin.
"""


def render(groups):
    out = [HEADER.format(source=SOURCE)]
    for name, context, values in groups:
        out.append(f"\n[{name}]\n")
        out.append(f"source: {SOURCE}, section 2.2, {context}\n")
        out.extend(f"{key} = {value}\n" for key, value in values.items())
    return "".join(out)


def main():
    for path in (PDF, TEXT):
        if not path.exists():
            print(f"MISSING: {path}. Run: pwsh tools/fetch-x9-vectors.ps1", file=sys.stderr)
            return 1
    actual = hashlib.sha256(PDF.read_bytes()).hexdigest().upper()
    if actual != PDF_SHA256:
        print(f"SOURCE_CHANGED: {PDF} has SHA-256 {actual}, expected {PDF_SHA256}", file=sys.stderr)
        return 1
    try:
        groups = extract(TEXT.read_text(encoding="utf-8", errors="replace"))
    except NotFound as e:
        print(f"CANNOT_LOCATE: {e}. Nothing written.", file=sys.stderr)
        return 1
    OUT.write_text(render(groups), encoding="utf-8", newline="\n")
    count = sum(len(values) for _, _, values in groups)
    print(f"wrote {OUT.relative_to(ROOT)}: {len(groups)} groups, {count} values")
    return 0


if __name__ == "__main__":
    sys.exit(main())
