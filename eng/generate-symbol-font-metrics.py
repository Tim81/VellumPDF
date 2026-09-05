# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# Generates src/VellumPdf.Reader/Fonts/SymbolFontMetrics.cs from all fourteen Adobe Core-14 AFM
# files (MustRead.html, Adobe Systems, 1997).
#
# Symbol.afm and ZapfDingbats.afm are the two symbolic standard 14 fonts (ISO 32000-2 Table 121
# bit 3): their built-in encodings are Annex D.5 and D.6, and the AFM's own C records are this
# reader's delivery vehicle for the same glyph-name/code/width data, not a separate transcription
# of the Annex D tables. This script also emits a glyph-name-keyed width table for each of the
# twelve nonsymbolic text fonts, keyed by name rather than by Unicode code point, so a name the
# text font's own encoding assigns (by /Differences or otherwise) measures at its own AFM width
# regardless of whether that name's Unicode value happens to fall inside WinAnsiEncoding.
#
# The AFM files themselves are NOT committed to this repository (their own licence permits
# copying and redistribution provided the copyright notices are retained and this file's own
# paragraph travels with them, but this project ships only the derived table this script
# produces, not the AFM files verbatim). This script re-derives that table from a local copy
# supplied at generation time and pins every source file with a normalised SHA-256 manifest, so a
# substituted or edited AFM file fails loudly instead of silently changing the emitted table.
#
# Usage:
#   python eng/generate-symbol-font-metrics.py --afm-dir <dir> --out <path>   # regenerate
#   python eng/generate-symbol-font-metrics.py --afm-dir <dir> --check        # verify up to date
#
# <dir> holds all fourteen Adobe Core-14 AFM files (MustRead.html, Adobe Systems, 1997). <path>
# defaults to src/VellumPdf.Reader/Fonts/SymbolFontMetrics.cs.

import hashlib
import os
import re
import sys

DEFAULT_OUTPUT = "src/VellumPdf.Reader/Fonts/SymbolFontMetrics.cs"

# (AFM filename, the name this reader resolves it to (Standard14Names' own strings for the
# twelve text fonts), the generated field-name fragment, whether it carries a built-in encoding).
FONT_TABLE = [
    ("Symbol.afm", "Symbol", "symbol", True),
    ("ZapfDingbats.afm", "ZapfDingbats", "zapfDingbats", True),
    ("Helvetica.afm", "Helvetica", "helvetica", False),
    ("Helvetica-Bold.afm", "Helvetica-Bold", "helveticaBold", False),
    ("Helvetica-Oblique.afm", "Helvetica-Oblique", "helveticaOblique", False),
    ("Helvetica-BoldOblique.afm", "Helvetica-BoldOblique", "helveticaBoldOblique", False),
    ("Times-Roman.afm", "Times-Roman", "timesRoman", False),
    ("Times-Bold.afm", "Times-Bold", "timesBold", False),
    ("Times-Italic.afm", "Times-Italic", "timesItalic", False),
    ("Times-BoldItalic.afm", "Times-BoldItalic", "timesBoldItalic", False),
    ("Courier.afm", "Courier", "courier", False),
    ("Courier-Bold.afm", "Courier-Bold", "courierBold", False),
    ("Courier-Oblique.afm", "Courier-Oblique", "courierOblique", False),
    ("Courier-BoldOblique.afm", "Courier-BoldOblique", "courierBoldOblique", False),
]

# Normalised SHA-256 of each AFM file: split on any of CR LF, LF, CR; strip trailing whitespace
# per line; drop empty lines; join with a single LF; append one trailing LF. Guards against a
# substituted or hand-edited input file changing the emitted table without being noticed.
MANIFEST = {
    "Symbol.afm": "a336805b37aa468ba403bcae995652e9b335994462ab3703a572ed5bb87363d7",
    "ZapfDingbats.afm": "b56fbcaebd71b210ba4cfac4bb669764c4f1bd7ab523f37f01b2f04f079bf699",
    "Helvetica.afm": "79e23df3e75df921fb8666fceb35b7ca363737dc9b5519cd4c1d9d1d2c23599c",
    "Helvetica-Bold.afm": "5f455fcdbf5583c3b9bc5d884d352eef3aed330396f0467328fac89eb6f4c740",
    "Helvetica-Oblique.afm": "91ed92f7b46b73fe062ad22667c2c462eafc0197288d9d70b989209b83e767f4",
    "Helvetica-BoldOblique.afm": "910c39255d66d14b08e64ced7ecca0511f974940ca5da93c82bb41e61519d9f2",
    "Times-Roman.afm": "41928f144173929c15f8cd4cc19aafeead3bd82b22141e55c538e94af7449ecf",
    "Times-Bold.afm": "c29b638a607a3347fa42a57adbfe98779681fa99b8b7c0ef903ff6ec6075d01c",
    "Times-Italic.afm": "74d5c65a553790bf3844cc515b591fcf9b2cc7b9b8472e114bdc231d5bb551af",
    "Times-BoldItalic.afm": "ea8b71d0a1f8cdb6835bec1f37dcef6ab1eeee1bca3c6f88b07a85eef4d1455e",
    "Courier.afm": "bdeeadc7738eccd3deb220e26d65152e6cbafaffc70d9f6c73a5461a893f16eb",
    "Courier-Bold.afm": "132f204bdeb88061b6180d7e8a9dd1a6fd313c5e8da5a64af0c0464b8155d879",
    "Courier-Oblique.afm": "728a2c018b50f40368dbdaa393bb97937e713c2e0f4841e40e4abafe02fdb09b",
    "Courier-BoldOblique.afm": "780a896a2a331066e8ed39024833ba6864fb4286f775d285186fae5aa348c436",
}

EXPECTED_RECORD_COUNT = {
    "Symbol.afm": 190,
    "ZapfDingbats.afm": 202,
    "Helvetica.afm": 315,
    "Helvetica-Bold.afm": 315,
    "Helvetica-Oblique.afm": 315,
    "Helvetica-BoldOblique.afm": 315,
    "Times-Roman.afm": 315,
    "Times-Bold.afm": 315,
    "Times-Italic.afm": 315,
    "Times-BoldItalic.afm": 315,
    "Courier.afm": 315,
    "Courier-Bold.afm": 315,
    "Courier-Oblique.afm": 315,
    "Courier-BoldOblique.afm": 315,
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
VERSION_LINE = re.compile(r"^Version (\S+)$")

# The AFM's own Notice line carries the copyright sentence (also duplicated onto its own Comment
# Copyright line, read separately below) immediately followed, with no separating space, by a
# trademark sentence where the font has one: Helvetica's four files name Linotype-Hell AG,
# Times' four name it too, and ZapfDingbats names International Typeface Corporation. Symbol and
# the four Courier files carry no trademark sentence at all. This captures whatever follows
# "Reserved." on that line, case-insensitive in "Rights"/"rights" and "Reserved"/"reserved" only
# ("All" itself is capitalised the same way in every one of the fourteen files).
TRADEMARK_AFTER_RESERVED = re.compile(r"All [Rr]ights [Rr]eserved\.(.*)$")


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
    notice_line = None
    version = None
    records = []
    seen_names = set()
    for line in normalized.split("\n"):
        if line.startswith("Comment Copyright") and copyright_line is None:
            copyright_line = line
        if line.startswith("Notice") and notice_line is None:
            notice_line = line
        if version is None:
            m = VERSION_LINE.match(line)
            if m:
                version = m.group(1)
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
    if notice_line is None:
        print(f"{filename}: no Notice line found", file=sys.stderr)
        sys.exit(1)
    if version is None:
        print(f"{filename}: no Version line found", file=sys.stderr)
        sys.exit(1)

    expected = EXPECTED_RECORD_COUNT[filename]
    if len(records) != expected:
        print(
            f"{filename}: {len(records)} C records, expected exactly {expected}", file=sys.stderr
        )
        sys.exit(1)

    m = TRADEMARK_AFTER_RESERVED.search(notice_line)
    trademark = m.group(1).strip() if m else ""
    trademark = trademark if trademark else None

    return records, copyright_line, version, trademark


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
        f"    private static readonly FrozenDictionary<string, int> _{field_name}Widths = "
        "new Dictionary<string, int>",
        "    {",
    ]
    for _, width, name in records:
        body.append(f'        ["{name}"] = {width},')
    body.append("    }.ToFrozenDictionary();")
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


def generate_source(parsed):
    # parsed: filename -> (records, copyright_line, version, trademark), in FONT_TABLE order.
    o = []
    w = o.append

    w("// Copyright © Timothy van der Ham (@Tim81)")
    w("// SPDX-License-Identifier: Apache-2.0")
    w("")
    w("using System.Collections.Frozen;")
    w("")
    w("// Generated by eng/generate-symbol-font-metrics.py; do not edit by hand.")
    w("//")
    w("// Inputs (Adobe Core-14 AFM files, MustRead.html, Adobe Systems, 1997), each one's own")
    w("// Version line, and the normalised SHA-256 this generator pinned it against:")
    for filename, _, _, _ in FONT_TABLE:
        _, _, version, _ = parsed[filename]
        w(f"// {filename} (Version {version})")
        w(f"//   {MANIFEST[filename]}")
    w("//")
    for filename, _, _, _ in FONT_TABLE:
        _, copyright_line, _, trademark = parsed[filename]
        for line in wrap_comment(f"{filename}: {copyright_line}"):
            w(line)
        if trademark:
            for line in wrap_comment(f"{filename}: {trademark}"):
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
    w("/// The built-in encodings of the two symbolic standard 14 fonts (Symbol and ZapfDingbats)")
    w("/// and the glyph-name-keyed advance widths of all fourteen. ISO 32000-2 Annex D.1 names")
    w("/// Annex D.5 and D.6 as the symbolic pair's built-in encodings; the Adobe Core-14 AFM")
    w("/// files are this reader's delivery vehicle for that same data, not a separate")
    w("/// transcription of the Annex D tables. The Symbol coding here agrees with Annex D.5 at")
    w("/// all 189 coded glyphs. The ZapfDingbats coding carries 14 codes (0x80 to 0x8D) that")
    w("/// Annex D.6 does not document at all; this reader keeps them, on the view that a font")
    w("/// program carrying those codes draws them regardless of whether the standard's own")
    w("/// table lists them. The twelve nonsymbolic text fonts carry no built-in encoding here")
    w("/// (their glyph names come from <see cref=\"SimpleFontEncodings\"/> and /Differences")
    w("/// instead); only their AFM widths are kept, keyed by glyph name so a name outside")
    w("/// WinAnsiEncoding still measures at its own width rather than a substitute glyph's.")
    w("/// </summary>")
    w("internal static class SymbolFontMetrics")
    w("{")

    symbol_records = parsed["Symbol.afm"][0]
    zapf_records = parsed["ZapfDingbats.afm"][0]
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
    w("    /// <summary>")
    w("    /// The named text font's AFM advance widths, keyed by glyph name (the AFM's own <c>N</c>")
    w("    /// records, including names the font's own StandardEncoding table has no code for).")
    w("    /// <paramref name=\"afmName\"/> is one of the twelve exact names")
    w("    /// <c>Standard14Names.TryResolve</c> can produce (e.g. <c>\"Helvetica-Bold\"</c>).")
    w("    /// Returns <see langword=\"false\"/> for <c>\"Symbol\"</c>, <c>\"ZapfDingbats\"</c>, or")
    w("    /// any other name.")
    w("    /// </summary>")
    w("    public static bool TryGetTextFontWidths(string afmName, out IReadOnlyDictionary<string, int> widths)")
    w("    {")
    w("        if (_textFontWidths.TryGetValue(afmName, out var found))")
    w("        {")
    w("            widths = found;")
    w("            return true;")
    w("        }")
    w("        widths = FrozenDictionary<string, int>.Empty;")
    w("        return false;")
    w("    }")
    w("")
    for line in symbol_enc_decl:
        w(line)
    for line in zapf_enc_decl:
        w(line)

    for filename, _, field_name, has_encoding in FONT_TABLE:
        if has_encoding:
            continue
        records = parsed[filename][0]
        for line in format_widths(field_name, records):
            w(line)
        w("")

    for line in format_widths("symbol", symbol_records):
        w(line)
    w("")
    for line in format_widths("zapfDingbats", zapf_records):
        w(line)
    w("")

    w("    private static readonly FrozenDictionary<string, FrozenDictionary<string, int>> _textFontWidths =")
    w("        new Dictionary<string, FrozenDictionary<string, int>>")
    w("        {")
    for filename, _, field_name, has_encoding in FONT_TABLE:
        if has_encoding:
            continue
        afm_name = next(n for f, n, fld, _ in FONT_TABLE if fld == field_name)
        w(f'            ["{afm_name}"] = _{field_name}Widths,')
    w("        }.ToFrozenDictionary();")
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

    parsed = {}
    for filename, _, _, _ in FONT_TABLE:
        normalized = load_afm(afm_dir, filename)
        parsed[filename] = parse_afm(filename, normalized)

    text = generate_source(parsed)

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
