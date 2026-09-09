#!/usr/bin/env python3
"""Report ISO 19005-2 clause coverage: what the standard requires, and what checks it.

Coverage here has been measured against veraPDF's test ids, which quietly makes the profile the
population. A requirement that neither veraPDF nor this library checks cannot appear in a
profile diff, because both sides agree by omission. This script uses the standard's own clause
list instead, so "nothing checks this" is a value the report can print.

The clause list lives in eng/data/iso19005-2-clauses.yml and carries clause numbers and headings
only: no normative text, since the standard is not held as a file. See docs/iso/ACQUISITION.md.

Two populations are compared against it:

  ours  - clause numbers appearing in a rule class that cites ISO 19005-2:2011. Deliberately
          generous: any 6.x token in such a file counts, so a rule citing a parent clause is
          credited with it. Erring generous keeps the gap list conservative.
  vera  - clause attributes in veraPDF's PDFA-2A/2B/2U profiles, read from the CLI jar.

Clauses scoped `reader` are excluded. Those bind a conforming reader rather than a conforming
file, so a file validator correctly has no rule for them: 6.5.3, 6.3.4, 6.1.5 and 6.2.8.2.

Usage:  python eng/clause-coverage.py [--json]
        VERAPDF_HOME or a verapdf in the home directory supplies the profile jar; without it
        column is reported as unavailable rather than as empty.

Refs #418.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
INVENTORY = ROOT / "eng" / "data" / "iso19005-2-clauses.yml"
RULES = ROOT / "src" / "VellumPdf.Conformance" / "Rules"
PROFILES = ("PDFA-2A.xml", "PDFA-2B.xml", "PDFA-2U.xml")


def load_inventory() -> dict:
    try:
        import yaml
    except ImportError:
        sys.exit("PyYAML is required: pip install pyyaml")
    if not INVENTORY.exists():
        sys.exit(f"clause inventory not found at {INVENTORY}")
    return yaml.safe_load(INVENTORY.read_text(encoding="utf-8"))


def clauses_cited_by_rules(known: set[str]) -> set[str]:
    """Clause ids named in any rule class that cites ISO 19005-2:2011."""
    if not RULES.is_dir():
        sys.exit(f"rules directory not found at {RULES}")
    found: set[str] = set()
    for path in RULES.rglob("*.cs"):
        text = path.read_text(encoding="utf-8-sig")
        if ": IConformanceRule" not in text or "ISO 19005-2:2011" not in text:
            continue
        found |= {m for m in re.findall(r"\b(6(?:\.\d+)+)\b", text) if m in known}
    return found


def find_profile_jar() -> Path | None:
    """The veraPDF CLI jar, via VERAPDF_HOME or the conventional home directory.

    Deliberately not a PATH lookup: CI resolves veraPDF by bare name through a Docker shim that
    keeps the jar inside the container, so finding the executable there would say nothing about
    whether its profiles can be read.
    """
    candidates = []
    home = os.environ.get("VERAPDF_HOME")
    if home:
        candidates.append(Path(home))
    candidates.append(Path.home() / "verapdf")
    for base in candidates:
        if not base.is_dir():
            continue
        for jar in base.rglob("*.jar"):
            if "cli" in jar.name.lower():
                return jar
    return None


def clauses_in_profiles(jar: Path) -> set[str]:
    found: set[str] = set()
    with zipfile.ZipFile(jar) as z:
        names = {Path(n).name: n for n in z.namelist()}
        for profile in PROFILES:
            entry = names.get(profile)
            if entry is None:
                continue
            xml = z.read(entry).decode("utf-8", "replace")
            found |= set(re.findall(r'clause="(\d+(?:\.\d+)*)"', xml))
    return found


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--json", action="store_true", help="emit machine-readable output")
    args = ap.parse_args()

    inv = load_inventory()
    entries = inv["clauses"]
    known = {c["id"] for c in entries}
    checkable = [c for c in entries if c["scope"] == "file"]

    ours = clauses_cited_by_rules(known)
    jar = find_profile_jar()
    vera = clauses_in_profiles(jar) if jar else None

    def status(c: dict) -> str:
        mine = c["id"] in ours
        theirs = None if vera is None else c["id"] in vera
        if mine and theirs:
            return "both"
        if mine:
            return "ours-only" if theirs is not None else "ours"
        if theirs:
            return "vera-only"
        return "unknown" if theirs is None else "neither"

    rows = [dict(c, status=status(c)) for c in checkable]

    if args.json:
        print(json.dumps({
            "standard": inv["standard"],
            "read_on": inv["read_on"],
            "verapdf_jar": str(jar) if jar else None,
            "clauses": rows,
        }, indent=1))
        return 0

    counts: dict[str, int] = {}
    for r in rows:
        counts[r["status"]] = counts.get(r["status"], 0) + 1

    print(f"{inv['standard']} clause coverage")
    print(f"  clause-6 entries      {len(entries)}")
    print(f"  file-scoped           {len(checkable)}")
    print(f"  reader-scoped         {sum(1 for c in entries if c['scope'] == 'reader')}  (out of scope for a file validator)")
    print(f"  containers            {sum(1 for c in entries if c['scope'] == 'container')}")
    print()
    if vera is None:
        print("  veraPDF profiles unavailable; set VERAPDF_HOME to compare against them.")
    else:
        print(f"  veraPDF profiles read from {jar}")
    print()
    for label, key in (("checked by neither", "neither"),
                       ("veraPDF checks it, we do not", "vera-only"),
                       ("we check it, veraPDF does not", "ours-only"),
                       ("not cited by any rule", "unknown")):
        hits = [r for r in rows if r["status"] == key]
        if not hits:
            continue
        print(f"{label}: {len(hits)}")
        for r in hits:
            level = "   [Level A only]" if r.get("level") == "A" else ""
            print(f"   {r['id']:<12} {r['title']}{level}")
        print()
    print("counts: " + ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
