#!/usr/bin/env python3
"""Linux/macOS counterpart of tools/make-release.ps1: the same zip, with the same layout and notes.

Builds dist/BattletechCoop-<version>-ModTek-<modtek>.zip from the official ModTek release (unmodified,
hash-checked) plus this mod's Release build. The install notes are read from make-release.ps1, so
there is one copy of them. Build first (tools/build-linux.sh), then:

    python3 tools/make-release.py [--modtek-zip PATH]
"""
import argparse
import hashlib
import json
import os
import re
import sys
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
MODTEK_VERSION = "4.5.1"
MODTEK_SHA256 = "53c038c1142c1552dcf7687bbee2b00d795b4691ff50a8c10901913cfa2339a9"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def notes(version):
    ps1 = open(os.path.join(ROOT, "tools", "make-release.ps1"), encoding="utf-8").read()
    m = re.search(r'^\$notes = @"\r?\n(.*?)\r?\n"@', ps1, re.S | re.M)
    if not m:
        sys.exit("could not find the notes in make-release.ps1")
    text = m.group(1).replace("$version", version).replace("$ModTekVersion", MODTEK_VERSION)
    return (text + "\n").replace("\r\n", "\n").replace("\n", "\r\n")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--modtek-zip", default=os.path.join(ROOT, "local", "downloads", "modtek-4.5.1", "ModTek.zip"))
    args = p.parse_args()

    if sha256(args.modtek_zip) != MODTEK_SHA256:
        sys.exit("ModTek.zip hash mismatch")
    binary = os.path.join(ROOT, "src", "BattletechCoop", "bin", "Release")
    dll = os.path.join(binary, "BattletechCoop.dll")
    mod_json = os.path.join(binary, "mod.json")
    if not (os.path.isfile(dll) and os.path.isfile(mod_json)):
        sys.exit("no Release build; run tools/build-linux.sh first")
    version = json.load(open(mod_json, encoding="utf-8"))["Version"]

    os.makedirs(os.path.join(ROOT, "dist"), exist_ok=True)
    out = os.path.join(ROOT, "dist", f"BattletechCoop-{version}-ModTek-{MODTEK_VERSION}.zip")
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(args.modtek_zip) as src, zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as dst:
        for item in src.infolist():  # ModTek's files as released, folders (empty ones too) included
            dst.writestr(item, src.read(item.filename))
        dst.write(dll, "Mods/BattletechCoop/BattletechCoop.dll")
        dst.write(mod_json, "Mods/BattletechCoop/mod.json")
        dst.writestr("BattletechCoop-README.txt", "﻿" + notes(version))
        dst.write(os.path.join(ROOT, "LICENSE"), "Mods/BattletechCoop/LICENSE")
        commit = os.popen(f'git -C "{ROOT}" rev-parse HEAD').read().strip() or "unknown"
        dirty = os.popen(f'git -C "{ROOT}" status --porcelain -- src').read().strip()
        dst.writestr("BUILD-INFO.txt", (
            f"BattletechCoop {version} with ModTek {MODTEK_VERSION}\n"
            f"Source commit: {commit}{' (with uncommitted changes in src/)' if dirty else ''}\n"
            f"Source: https://github.com/AdmiralRob/battletech-coop/tree/{commit}\n"
            "Both players must install the SAME zip; compare its SHA-256.\n").replace("\n", "\r\n"))

    print(f"Created {out}")
    print(f"Size    {os.path.getsize(out)} bytes")
    print(f"SHA-256 {sha256(out)}")


if __name__ == "__main__":
    main()
