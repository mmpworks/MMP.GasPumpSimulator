"""Builds docs/samples/custody-chain-sample.ndjson from a real custody file.

    python make_sample.py <real custody file> <its log folder> <sample out> <key out>

Two changes, nothing else. The local log folder becomes one constant path, so the sample names no
one's machine. Then every entry is re-sealed under a demo key that is published beside the sample,
so a reader can run the full verifier. The events, their order, and their times stay as written.
"""
import hashlib
import json
import pathlib
import sys

from tamper import read_lines, rebuild_chain

DEMO_LOG_DIR = "C:\\GasPumpDemo\\logs\\"
DEMO_KEY = hashlib.sha256(b"MMP.GasPumpSimulator public demo key. It protects nothing.").digest()


def as_json_text(text):
    return json.dumps(text)[1:-1]


def main():
    source, log_dir, sample_out, key_out = sys.argv[1:5]
    local = as_json_text(log_dir.rstrip("\\/") + "\\")
    lines = [line.replace(local, as_json_text(DEMO_LOG_DIR)) for line in read_lines(source)]
    sealed = rebuild_chain(lines, DEMO_KEY)
    pathlib.Path(sample_out).write_bytes(("\r\n".join(sealed) + "\r\n").encode("utf-8"))
    pathlib.Path(key_out).write_text(DEMO_KEY.hex() + "\n", encoding="ascii", newline="\n")


if __name__ == "__main__":
    main()
