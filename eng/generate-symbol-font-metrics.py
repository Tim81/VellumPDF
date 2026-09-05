# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# Generates src/VellumPdf.Reader/Fonts/SymbolFontMetrics.cs from the Adobe Core 14 AFM files
# (MustRead.html, Adobe Systems, 1997) for Symbol.afm and ZapfDingbats.afm. Those two are the only
# two of the fourteen that are symbolic fonts (ISO 32000-2 Table 121 bit 3): their built-in
# encodings are Annex D.5 and D.6, and the AFM's own C records are this reader's delivery vehicle
# for the same glyph-name/code/width data, not a separate transcription of the Annex D tables.
#
# The AFM files themselves are NOT committed to this repository (their own licence permits
# copying and redistribution provided the copyright notices are retained and this file's own
# paragraph travels with them, but this project ships only the derived table this script
# produces, not the AFM files verbatim). This script re-derives that table from a local copy
# supplied at generation time and pins the source files with a normalised SHA-256 manifest, so a
# substituted or edited AFM file fails loudly instead of silently changing the emitted table.
#
# Usage:
#   python eng/generate-symbol-font-metrics.py --afm-dir <dir> --out <path>   # regenerate
#   python eng/generate-symbol-font-metrics.py --afm-dir <dir> --check        # verify up to date
#
# <dir> holds Symbol.afm and ZapfDingbats.afm (Adobe Core 14 AFM files, MustRead.html, Adobe
# Systems, 1997). <path> defaults to src/VellumPdf.Reader/Fonts/SymbolFontMetrics.cs.

import hashlib
import os
import re
import sys

DEFAULT_OUTPUT = "src/VellumPdf.Reader/Fonts/SymbolFontMetrics.cs"

# Normalised SHA-256 of each AFM file: split on any of CR LF, LF, CR; strip trailing whitespace
# per line; drop empty lines; join with a single LF; append one trailing LF. Guards against a
# substituted or hand-edited input file changing the emitted table without being noticed.
MANIFEST = {
    "Symbol.afm": "a336805b37aa468ba403bcae995652e9b335994462ab3703a572ed5bb87363d7",
    "ZapfDingbats.afm": "b56fbcaebd71b210ba4cfac4bb669764c4f1bd7ab523f37f01b2f04f079bf699",
}

EXPECTED_RECORD_COUNT = {
    "Symbol.afm": 190,
    "ZapfDingbats.afm": 202,
}

# The MustRead.html paragraph, verbatim (the licence text governing use of the AFM files this
# script reads; it requires copyright notices to be retained and this paragraph itself to travel
# unmodified alongside them, which is why it is reproduced in full in the generated file).
MUSTREAD_PARAGRAPH = (
    "This file and the 14 PostScript(R) AFM files it accompanies may be used, copied, and "
    "distributed for any purpose and without charge, with or without modification, provided "
    "that all copyright notices are retained; that the AFM files are not distributed without "
    "this file; that all modifications to this file or any of the AFM files are prominently "
    "noted in the modified file(s); and that this paragraph is not modified. Adobe Systems has "
    "no responsibility or obligation to support the use of the AFM files."
)

C_RECORD = re.compile(r"^C (-?\d+) ; WX (-?\d+) ; N (\S+) ;")


def normalize(raw_bytes):
    text = raw_bytes.decode("latin-1")
    lines = re.split(r"\r\n|\r|\n", text)
    lines = [line.rstrip() for line in lines]
    lines = [line for line in lines if line != ""]
    return "\n".join(lines) + "\n"


def load_afm(afm_dir, filename):
    path = os.path.join(afm_dir, filename)
    with open(path, "rb") as f:
        raw = f.read()
    normalized = normalize(raw)
    actual_hash = hashlib.sha256(normalized.encode("latin-1")).hexdigest()
    expected_hash = MANIFEST[filename]
    if actual_hash != expected_hash:
        print(
            f"{filename}: normalised SHA-256 is {actual_hash}, expected {expected_hash}. "
            "Refusing to generate from an AFM file that does not match the pinned manifest.",
            file=sys.stderr,
        )
        sys.exit(1)
    return normalized


def parse_afm(filename, normalized):
    copyright_line = None
    records = []
    seen_names = set()
    for line in normalized.split("\n"):
        if line.startswith("Comment Copyright") and copyright_line is None:
            copyright_line = line
        if not line.startswith("C "):
            continue
        m = C_RECORD.match(line)
        if not m:
            print(f"{filename}: unparsable C record: {line!r}", file=sys.stderr)
            sys.exit(1)
        code, width, name = int(m.group(1)), int(m.group(2)), m.group(3)
        if not -1 <= code <= 255:
            print(f"{filename}: C record code {code} outside -1..255: {line!r}", file=sys.stderr)
            sys.exit(1)
        if name in seen_names:
            print(f"{filename}: duplicate glyph name {name!r}", file=sys.stderr)
            sys.exit(1)
        seen_names.add(name)
        records.append((code, width, name))

    if copyright_line is None:
        print(f"{filename}: no Comment Copyright line found", file=sys.stderr)
        sys.exit(1)

    expected = EXPECTED_RECORD_COUNT[filename]
    if len(records) != expected:
        print(
            f"{filename}: {len(records)} C records, expected exactly {expected}", file=sys.stderr
        )
        sys.exit(1)

    return records, copyright_line


def format_encoding(field_name, records):
    lines = [f"    private static readonly string?[] _{field_name} = BuildEncoding_{field_name}();", ""]
    coded = sorted((code, name) for code, _, name in records if code != -1)
    body = [f"    private static string?[] BuildEncoding_{field_name}()", "    {", "        var t = new string?[256];"]
    for code, name in coded:
        body.append(f'        t[0x{code:02X}] = "{name}";')
    body.append("        return t;")
    body.append("    }")
    return lines, body


def format_widths(field_name, records):
    body = [
        f"    private static readonly Dictionary<string, int> _{field_name} = new()",
        "    {",
    ]
    for _, width, name in records:
        body.append(f'        ["{name}"] = {width},')
    body.append("    };")
    return body


def wrap_comment(text, width=96):
    words = text.split(" ")
    lines = []
    current = "// "
    for word in words:
        candidate = f"{current}{word} " if current != "// " else f"{current}{word} "
        if len(candidate.rstrip()) > width and current != "// ":
            lines.append(current.rstrip())
            current = f"// {word} "
        else:
            current = candidate
    if current.strip() != "//":
        lines.append(current.rstrip())
    return lines


def generate_source(symbol_records, symbol_copyright, zapf_records, zapf_copyright):
    o = []
    w = o.append

    w("// Copyright © Timothy van der Ham (@Tim81)")
    w("// SPDX-License-Identifier: Apache-2.0")
    w("")
    w("// Generated by eng/generate-symbol-font-metrics.py; do not edit by hand.")
    w("//")
    for line in wrap_comment(f"Symbol.afm: {symbol_copyright}"):
        w(line)
    for line in wrap_comment(f"ZapfDingbats.afm: {zapf_copyright}"):
        w(line)
    w("//")
    for line in wrap_comment(MUSTREAD_PARAGRAPH):
        w(line)
    w("//")
    w("// This file is a derived table of glyph names, codes and advance widths, not a copy of")
    w("// the AFM files.")
    w("")
    w("namespace VellumPdf.Reader.Fonts;")
    w("")
    w("/// <summary>")
    w("/// The built-in encodings and AFM advance widths of the two symbolic standard 14 fonts,")
    w("/// Symbol and ZapfDingbats. ISO 32000-2 Annex D.1 names Annex D.5 and D.6 as their")
    w("/// built-in encodings; the Adobe Core 14 AFM files are this reader's delivery vehicle for")
    w("/// that same data, not a separate transcription of the Annex D tables. The Symbol coding")
    w("/// here agrees with Annex D.5 at all 189 coded glyphs. The ZapfDingbats coding carries 14")
    w("/// codes (0x80 to 0x8D) that Annex D.6 does not document at all; this reader keeps them,")
    w("/// on the view that a font program carrying those codes draws them regardless of whether")
    w("/// the standard's own table lists them.")
    w("/// </summary>")
    w("internal static class SymbolFontMetrics")
    w("{")

    symbol_enc_decl, symbol_enc_body = format_encoding("symbol", symbol_records)
    zapf_enc_decl, zapf_enc_body = format_encoding("zapfDingbats", zapf_records)

    w("    /// <summary>Symbol's built-in encoding (ISO 32000-2 Annex D.5): char code to glyph")
    w("    /// name, null where the AFM assigns the code no glyph.</summary>")
    w("    public static ReadOnlySpan<string?> SymbolEncoding => _symbol;")
    w("")
    w("    /// <summary>ZapfDingbats' built-in encoding (ISO 32000-2 Annex D.6, plus the 14 codes")
    w("    /// the class doc above names): char code to glyph name.</summary>")
    w("    public static ReadOnlySpan<string?> ZapfDingbatsEncoding => _zapfDingbats;")
    w("")
    w("    /// <summary>Symbol's AFM advance widths, name-keyed (includes \"apple\", which the")
    w("    /// AFM assigns no code (<c>C -1</c>), so it is absent from")
    w("    /// <see cref=\"SymbolEncoding\"/>).</summary>")
    w("    public static IReadOnlyDictionary<string, int> SymbolWidths => _symbolWidths;")
    w("")
    w("    /// <summary>ZapfDingbats' AFM advance widths, name-keyed.</summary>")
    w("    public static IReadOnlyDictionary<string, int> ZapfDingbatsWidths => _zapfDingbatsWidths;")
    w("")
    for line in symbol_enc_decl:
        w(line)
    for line in zapf_enc_decl:
        w(line)
    w("")
    for line in format_widths("symbolWidths", symbol_records):
        w(line)
    w("")
    for line in format_widths("zapfDingbatsWidths", zapf_records):
        w(line)
    w("")
    for line in symbol_enc_body:
        w(line)
    w("")
    for line in zapf_enc_body:
        w(line)
    w("}")
    return "\n".join(o) + "\n"


def main():
    args = sys.argv[1:]
    afm_dir = None
    output = DEFAULT_OUTPUT
    check = False
    i = 0
    while i < len(args):
        if args[i] == "--afm-dir" and i + 1 < len(args):
            afm_dir = args[i + 1]
            i += 2
        elif args[i] == "--out" and i + 1 < len(args):
            output = args[i + 1]
            i += 2
        elif args[i] == "--check":
            check = True
            i += 1
        else:
            print(f"unrecognised argument: {args[i]}", file=sys.stderr)
            return 1

    if afm_dir is None:
        print("usage: generate-symbol-font-metrics.py --afm-dir <dir> [--out <path> | --check]", file=sys.stderr)
        return 1

    symbol_normalized = load_afm(afm_dir, "Symbol.afm")
    zapf_normalized = load_afm(afm_dir, "ZapfDingbats.afm")
    symbol_records, symbol_copyright = parse_afm("Symbol.afm", symbol_normalized)
    zapf_records, zapf_copyright = parse_afm("ZapfDingbats.afm", zapf_normalized)

    text = generate_source(symbol_records, symbol_copyright, zapf_records, zapf_copyright)

    if check:
        if not os.path.exists(output):
            print(f"{output} does not exist; run without --check to generate it.", file=sys.stderr)
            return 1
        with open(output, encoding="utf-8") as f:
            existing = f.read()
        if existing == text:
            print(f"{output} is up to date.")
            return 0
        import difflib

        diff = difflib.unified_diff(
            existing.splitlines(keepends=True), text.splitlines(keepends=True),
            fromfile=output, tofile=f"{output} (generated)",
        )
        sys.stdout.writelines(diff)
        print(f"{output} is out of date; run without --check to regenerate.", file=sys.stderr)
        return 1

    os.makedirs(os.path.dirname(output), exist_ok=True) if os.path.dirname(output) else None
    with open(output, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"Wrote {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
