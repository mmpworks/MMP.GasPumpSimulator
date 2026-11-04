"""Re-computes the first entries of a custody file by hand, with only the Python standard library.

    python explain.py <custody.ndjson> <key file> [count]

Each line shows the MAC computed here beside the MAC stored in the file.
"""
import base64
import hashlib
import hmac
import json
import pathlib
import sys

from tamper import hkdf, read_lines, split_entry


def short(raw):
    return base64.b64encode(raw).decode()[:12]


def main():
    source, key_file = sys.argv[1:3]
    count = int(sys.argv[3]) if len(sys.argv) > 3 else 6
    lines = read_lines(source)
    ikm = bytes.fromhex(pathlib.Path(key_file).read_text().strip())
    tenant = json.loads(lines[0])["tenant_id"]
    key = hkdf(ikm, tenant)
    print("session key fingerprint:", base64.b64encode(hashlib.sha256(tenant.encode() + ikm).digest()[:16]).decode())

    prev = bytes(32)
    for line in lines[1:count + 1]:
        block, canonical = split_entry(line)
        stamp = json.loads(block)
        mac = hmac.new(key, prev + canonical.encode(), hashlib.sha256).digest()
        stored = base64.b64decode(stamp["payload_hash"])
        print(f"seq {stamp['seq']}: prev {short(prev)}...  computed {short(mac)}...  stored {short(stored)}...  "
              f"{'match' if hmac.compare_digest(mac, stored) else 'DIFFERENT'}")
        prev = mac


if __name__ == "__main__":
    main()
