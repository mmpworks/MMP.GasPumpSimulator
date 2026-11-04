"""Writes attacked copies of a custody file, one file per attack, to show what the verifier catches.

    python tamper.py <custody.ndjson> <out-dir> [--ikm custody.ikm] [--other-run f] [--other-tenant f]

Each attack works on the raw bytes and leaves the source file alone. The attacks that need a
key make a new random key, except "rewrite-with-the-real-key", which needs --ikm. The two
--other options name another run's file for the lift attacks.
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import pathlib

SALT = b"ffiec.chain-of-custody.v1.salt"
INFO_BASE = b"ffiec.chain-of-custody.v1.info"
ZERO = base64.b64encode(bytes(32)).decode()


def read_lines(path):
    return pathlib.Path(path).read_bytes().decode("utf-8").split("\r\n")[:-1]


def hkdf(ikm, tenant):
    prk = hmac.new(SALT, ikm, hashlib.sha256).digest()
    info = INFO_BASE + b"|" + tenant.encode()
    return hmac.new(prk, info + b"\x01", hashlib.sha256).digest()


def split_entry(line):
    """Returns the audit_chain block as text and the canonical event bytes the MAC covers."""
    end = line.index("},")
    return line[len('{"audit_chain":'):end + 1], "{" + line[end + 2:]


def rebuild_chain(lines, ikm, fingerprint=None):
    """Re-seals every entry under ikm, as anyone who holds that key can."""
    tenant = json.loads(lines[0])["tenant_id"]
    key = hkdf(ikm, tenant)
    real_print = base64.b64encode(hashlib.sha256(tenant.encode() + ikm).digest()[:16]).decode()
    prev = bytes(32)
    out = [lines[0]]
    for line in lines[1:]:
        block, canonical = split_entry(line)
        stamp = json.loads(block)
        mac = hmac.new(key, prev + canonical.encode(), hashlib.sha256).digest()
        stamp["prev_hash"] = base64.b64encode(prev).decode()
        stamp["payload_hash"] = base64.b64encode(mac).decode()
        stamp["key_fingerprint"] = fingerprint or real_print
        sealed = json.dumps(stamp, separators=(",", ":"), sort_keys=True)
        out.append('{"audit_chain":' + sealed + "," + canonical[1:])
        prev = mac
    return out


def edit_field(line, old, new):
    assert old in line, old
    return line.replace(old, new)


def attacks(lines, args):
    real_print = json.loads(split_entry(lines[1])[0])["key_fingerprint"]
    random_key = os.urandom(32)
    e = lines  # lines[n] is the entry with seq n, because line 0 is the header.

    yield "clean", list(lines), True
    yield "edit-the-ceiling", lines[:6] + [edit_field(e[6], "$30.00", "$3.00")] + lines[7:], True
    block, canonical = split_entry(e[6])
    stamp = json.loads(block)
    stamp.update(seq=7, prev_hash=stamp["payload_hash"], payload_hash=base64.b64encode(os.urandom(32)).decode())
    forged = '{"audit_chain":' + json.dumps(stamp, separators=(",", ":"), sort_keys=True) + "," + canonical[1:].replace("arm.ok", "refund.ok")
    yield "insert-a-forged-entry", lines[:7] + [forged] + lines[7:], True
    yield "delete-an-entry", lines[:4] + lines[5:], True
    yield "swap-two-entries", lines[:4] + [e[5], e[4]] + lines[6:], True
    yield "new-chain-own-key-own-fingerprint", rebuild_chain(lines, random_key), True
    yield "new-chain-own-key-stolen-fingerprint", rebuild_chain(lines, random_key, real_print), True
    yield "edit-entry-tenant", lines[:3] + [edit_field(e[3], '"tenant_id":"PUMP-01"', '"tenant_id":"PUMP-02"')] + lines[4:], True
    yield "header-format-v2", [edit_field(e[0], '"format_version":"v1"', '"format_version":"v2"')] + lines[1:], True
    yield "header-other-tenant", [edit_field(e[0], '"tenant_id":"PUMP-01"', '"tenant_id":"PUMP-09"')] + lines[1:], True
    yield "header-nonzero-genesis", [edit_field(e[0], ZERO, base64.b64encode(b"\x01" * 32).decode())] + lines[1:], True
    yield "cut-the-last-newline", list(lines), False
    yield "edit-a-timestamp-in-the-stamp", lines[:6] + [edit_field(e[6], '"mac_computed_at_utc":"2026-11-04T02:31', '"mac_computed_at_utc":"2020-01-01T02:31')] + lines[7:], True
    yield "cut-the-last-5-entries", lines[:-5], True
    if args.other_run:
        other = read_lines(args.other_run)
        yield "lift-from-another-run-same-tenant", lines[:7] + [other[7]] + lines[8:], True
    if args.other_tenant:
        other = read_lines(args.other_tenant)
        yield "lift-from-another-tenant", lines[:2] + [other[2]] + lines[3:], True
    if args.ikm:
        ikm = bytes.fromhex(pathlib.Path(args.ikm).read_text().strip())
        changed = lines[:6] + [edit_field(e[6], "$30.00", "$3.00")] + lines[7:]
        yield "rewrite-with-the-real-key", rebuild_chain(changed, ikm), True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("out")
    parser.add_argument("--ikm")
    parser.add_argument("--other-run")
    parser.add_argument("--other-tenant")
    args = parser.parse_args()

    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    for name, lines, final_newline in attacks(read_lines(args.source), args):
        text = "\r\n".join(lines) + ("\r\n" if final_newline else "")
        (out / (name + ".ndjson")).write_bytes(text.encode("utf-8"))
        print(name)


if __name__ == "__main__":
    main()
