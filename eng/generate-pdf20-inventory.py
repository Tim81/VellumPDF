# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# Generates docs/pdf20-conformance.md, the inventory that the PDF 2.0 claim is gated on (#225).
#
# "Supports PDF 2.0" is a claim nobody can check. This produces one anybody can, from three
# machine-readable sources published by the PDF Association:
#
#   * pdf-association/PDF2NormRefs: what ISO 32000-2 CITES, the normative-reference graph.
#   * pdf-association/arlington-pdf-model: what ISO 32000-2 DEFINES, every object and key, with
#     SinceVersion and DeprecatedIn per key.
#   * pdf-association/pdf-extensions: what AMENDS ISO 32000-2, the registry of published extensions.
#
# The first two are Apache-2.0 for software and CC-BY-4.0 for data; the third is CC-BY-4.0 outright.
#
# Only the second maps to features: knowing the standard cites ISO 15444-1 tells you JPEG 2000 is in
# scope, not which dictionary keys a conforming writer must produce. Both axes are emitted, as
# separate tables, because they answer different questions.
#
# Two traps this script exists to avoid, both of which cost time to rediscover:
#
#   * Use the Arlington tsv/2.0/ set (611 files), NOT tsv/latest/ (613). The two extras are
#     ActionNOP and ActionSetState, which are not part of PDF 2.0. Generating from latest silently
#     overstates the standard.
#   * PDF2NormRefs lives on branch "master", not "main". pdf-extensions lives on "main".
#   * The extension table prints pdf-extensions' own "date" field, so any upstream edit makes the
#     checked-in file stale even when no rendered row changed. That is a deliberate trade: the date
#     is what tells a reader how current the table is, and docs-inventory.yml already leaves
#     upstream drift to a human rather than failing the main build.
#
# ISO 32000-2 Annex I is normative and contains NO feature table. The standard dropped the
# per-version table ISO 32000-1 Annex H carried. There is nothing to transcribe, which is why this
# has to be generated. The specification's own list of additions and deprecations is clause 0.3, and
# the FEATURES map below is transcribed from it by hand; it is the one part of this file that is not
# derived, and it is short enough to check against the text.
#
# STATUS is the curated half: the generator supplies structure, a human supplies verdicts. Anything
# absent from STATUS is emitted as "Not assessed" rather than being guessed at, so a gap in the
# curation is visible in the output instead of hiding as a confident-looking default.
#
# Usage:
#   python eng/generate-pdf20-inventory.py           # regenerate docs/pdf20-conformance.md
#   python eng/generate-pdf20-inventory.py --check   # fail if the checked-in file is out of date

import io
import json
import os
import re
import sys
import urllib.request
import zipfile

NORMREFS_URL = "https://raw.githubusercontent.com/pdf-association/PDF2NormRefs/master/data/referencesGraph.json"
ARLINGTON_URL = "https://codeload.github.com/pdf-association/arlington-pdf-model/zip/refs/heads/master"
ARLINGTON_TSV_DIR = "/tsv/2.0/"
EXTENSIONS_URL = "https://raw.githubusercontent.com/pdf-association/pdf-extensions/main/extensions/pdf-extensions.json"
OUTPUT_PATH = "docs/pdf20-conformance.md"

# Verdicts. Kept deliberately few: a reader should be able to hold the whole vocabulary in mind.
IMPL = "Implemented"
PART = "Partial"
NOT = "Not implemented"
OUT = "Out of scope"

# ── Curated status ────────────────────────────────────────────────────────────────────────────────
# Keyed by the organisation + standard id as it appears in referencesGraph.json. A missing key emits
# "Not assessed", which is honest and visible; do not add a key without checking the tree.
STATUS = {
    "ISO 32000-2": (IMPL, "The standard itself.", ""),
    # Compression and filters
    "IETF RFC 1950": (IMPL, "FlateDecode, both directions.", ""),
    "IETF RFC 1951": (IMPL, "DEFLATE, via the .NET BCL.", ""),
    "ISO 15948/IEC 15948": (IMPL, "PNG predictors for Flate and LZW.", ""),
    "ITU-T Recommendation T.4": (IMPL, "CCITT Group 3.", "#48"),
    "ITU-T Recommendation T.6": (IMPL, "CCITT Group 4.", "#48"),
    # Images
    "ISO 10918 (all parts)/IEC 10918 (all parts)": (IMPL, "DCTDecode; JPEG data passed through verbatim.", ""),
    "ISO 15444-1/IEC 15444-1": (IMPL, "JPXDecode.", "#91"),
    "ISO 14492/IEC 14492": (IMPL, "JBIG2 decoder.", "#44"),
    "Adobe Systems Incorporated Technical Note #5116": (IMPL, "DCT filter support.", ""),
    # Seven references share the bare organisation "Adobe Systems Incorporated" with no standard id,
    # so they are keyed by title instead. Keying them by organisation alone would give all seven one
    # verdict — which is how the Adobe Glyph List first came out labelled "TIFF 6.0 read".
    "Adobe Systems Incorporated | Adobe TIFF Revision 6.0 Final": (IMPL, "TIFF read; used by the image loaders.", "#48"),
    "Adobe Systems Incorporated | PostScript Language Third Edition": (OUT, "PostScript interpretation; not applicable to a PDF generator.", ""),
    "Adobe Systems Incorporated | Adobe Type 1 Font Format Version 1.1": (NOT, "Type 1 font programs cannot be embedded.", "#263"),
    "Adobe Systems Incorporated | Adobe Glyph List": (IMPL, "Used for glyph-name to Unicode mapping.", ""),
    "Adobe Systems Incorporated | Adobe Glyph List for New Fonts": (IMPL, "Used alongside the AGL.", ""),
    "Adobe Systems Incorporated | Adobe PDF Signature Build Dictionary Specification v.1.4": (OUT, "The /Prop_Build dictionary is informational and deliberately not written.", ""),
    "Adobe Systems Incorporated | Adobe XML Architecture XML Forms Architecture (XFA) Specification version 3.3": (OUT, "XFA is deprecated by PDF 2.0 clause 0.3 and forbidden by PDF/UA-2.", ""),
    # Colour
    "ISO 15076-1": (PART, "ICCBased colour spaces and output intents; synthesized sRGB and CMYK profiles.", "#42"),
    "IEC 61966-2-1": (IMPL, "sRGB, via a synthesized profile.", "#42"),
    "ISO 18619": (NOT, "Black point compensation — /UseBlackPtComp.", "#256"),
    "ISO 17972-4": (NOT, "CxF/X-4 spot colour characterisation — /SpectralData.", "#253"),
    # Fonts
    "Adobe Systems Incorporated Technical Note #5176": (IMPL, "CFF, including subsetting.", "#41"),
    "Adobe Systems Incorporated Technical Note #5177": (IMPL, "Type 2 charstrings.", "#41"),
    "ISO 14496-22/IEC 14496-22": (PART, "OpenType embedding via /FontFile3 /Subtype /OpenType. No CFF2, no variable or colour fonts.", "#263"),
    "Apple | TrueType Reference Manual": (IMPL, "TrueType, including glyf subsetting.", ""),
    "Adobe Systems Incorporated Technical Note #5014": (NOT, "CMap and CID font files — predefined CMaps.", "#266"),
    "Adobe Systems Incorporated Technical Note #5015": (NOT, "Type 1 font format supplement.", "#263"),
    "Hewlett-Packard | PANOSE Classification Metrics Guide": (NOT, "PANOSE classification metrics.", ""),
    # Character collections
    "Adobe Systems Incorporated Japan1-7": (NOT, "Predefined CMaps; Identity-H only today.", "#266"),
    "Adobe Systems Incorporated GB1-5,()": (NOT, "Predefined CMaps.", "#266"),
    "Adobe Systems Incorporated CNS1-7": (NOT, "Predefined CMaps.", "#266"),
    "Adobe Systems Incorporated KR-9": (NOT, "Predefined CMaps; introduced by PDF 2.0.", "#266"),
    "Adobe Systems Incorporated Korea1-2": (OUT, "Deprecated by PDF 2.0 (clause 0.4).", ""),
    "Adobe Systems Incorporated Japan2-0": (OUT, "Deprecated by PDF 2.0 (clause 0.4).", ""),
    # Text and encoding
    "ISO 10646/IEC 10646": (IMPL, "Unicode throughout; UTF-8 text strings.", ""),
    "INCITS 4": (IMPL, "ASCII.", ""),
    "IETF RFC 5646 BCP 47": (PART, "/Lang written; not validated as a well-formed tag.", ""),
    "ISO 3166-1": (IMPL, "Via BCP 47 language tags.", ""),
    "JSA JIS X 4051": (OUT, "Japanese formatting rules. Not freely available; CJK line breaking would implement from W3C JLReq, which is based on it, with UAX #14 and UAX #11.", "#321"),
    # Metadata and markup
    "ISO 16684-1": (IMPL, "XMP packet written for every document, including Info.Keywords mirrored into pdf:Keywords.", ""),
    "W3C Recommendation XML 1.0": (IMPL, "XMP serialisation.", ""),
    "W3C Recommendation MathML 3.0": (NOT, "MathML in tagged PDF.", "#293"),
    "W3C Recommendation PLS 1.0": (NOT, "Pronunciation hints.", "#359"),
    "W3C Recommendation WAI-ARIA 1.1": (NOT, "ARIA roles via the ARIA-1.1 attribute owner.", "#272"),
    "W3C Recommendation SMIL 3.0": (OUT, "Multimedia synchronisation.", "#352"),
    # Cryptography and signing
    "NIST FIPS PUB 197": (IMPL, "AES.", ""),
    "NIST FIPS PUB 180-4": (IMPL, "SHA-2 family.", ""),
    "IETF RFC 1321": (IMPL, "MD5, for legacy /R 2-4 decryption only.", ""),
    "IETF RFC 8018": (IMPL, "PKCS#5 key derivation.", ""),
    "IETF RFC 5652 STD 70": (IMPL, "CMS SignedData.", ""),
    "IETF RFC 2315": (IMPL, "PKCS#7, via CMS.", ""),
    "IETF RFC 3161": (IMPL, "Timestamp protocol.", "#69"),
    "IETF RFC 5816": (IMPL, "ESSCertIDv2.", "#168"),
    "IETF RFC 5035": (IMPL, "ESS CertID algorithm agility.", "#168"),
    "IETF RFC 5280": (IMPL, "X.509 path material; full validation is #282.", "#282"),
    "IETF RFC 6960": (PART, "OCSP collected for LTV; verification is #282.", "#104"),
    "IETF RFC 5755": (OUT, "Attribute certificates.", ""),
    "IETF RFC 5480": (IMPL, "EC subject public key info.", ""),
    "ANSI X9.62": (IMPL, "ECDSA; extended curve set is #239.", "#239"),
    "NIST FIPS PUB 186-4": (IMPL, "DSA and ECDSA parameters.", ""),
    "ETSI EN 319 122-1 V1.1.1": (IMPL, "CAdES baseline.", "#170"),
    "ETSI EN 319 122-2 V1.1.1": (IMPL, "CAdES baseline profiles.", "#170"),
    "ETSI EN 319 142-1 V1.1.1": (IMPL, "PAdES B-B through B-LTA.", "#49"),
    "ETSI EN 319 142-2 V1.1.1": (IMPL, "PAdES baseline profiles.", "#49"),
    "IETF RFC 3454": (NOT, "stringprep. Obsolete at IETF, but clause 0.4 requires its continued use.", "#226"),
    "IETF RFC 4013": (NOT, "SASLprep. Same — see #226 for why RFC 8265 is not the target.", "#226"),
    "ITU-T Recommendation X.680/ISO 8824-1/IEC 8824-1": (IMPL, "ASN.1, via System.Formats.Asn1.", ""),
    # Interactive and structural
    "IETF RFC 3986 STD 66": (IMPL, "URI actions.", ""),
    "IETF RFC 7231": (OUT, "HTTP semantics; no network features are written.", ""),
    "IETF RFC 2045": (PART, "MIME types on embedded files.", "#242"),
    "IETF RFC 2046": (PART, "MIME media types.", "#242"),
    "IETF RFC 8118": (NOT, "The application/pdf media type, for embedded file /Subtype.", "#242"),
    "ISO 19444-1": (NOT, "XFDF form data.", "#355"),
    "ISO 21757-1": (OUT, "ECMAScript for PDF. Still in development.", "#354"),
    "Adobe Systems Incorporated Technical Note #5620": (OUT, "Portable Job Ticket Format.", ""),
    "Adobe Systems Incorporated Technical Note #5660": (OUT, "Open Prepress Interface. Deprecated by PDF 2.0.", ""),
    "ISO 19162": (OUT, "Well-known text for coordinate reference systems — geospatial.", "#351"),
    "OGP | EPSG Geospatial Coordinate System Reference Codes": (OUT, "EPSG geodetic parameter registry — geospatial.", "#351"),
    "ECMA 363": (OUT, "U3D 3D artwork.", "#353"),
    "ISO 14739-1": (OUT, "PRC 3D artwork. Blocks PDF/A-4E.", "#353"),
}

# ── Clause 0.3, transcribed by hand from ISO 32000-2:2020 ─────────────────────────────────────────
# The specification's own list. The only non-derived data here; short enough to check against the text.
FEATURES = [
    ("7.6.7", "Unencrypted wrapper document", NOT, "#357"),
    ("8.6.5.9", "Use of black point compensation", NOT, "#256"),
    ("12.5.6.24", "Projection annotations", NOT, "#244"),
    ("12.8.3.4", "CAdES signatures as used in PDF", IMPL, "#170"),
    ("12.8.4", "Long term validation of signatures", IMPL, "#49"),
    ("12.8.4.3 / 12.8.5", "Document Security Store and document timestamp", IMPL, "#103"),
    ("12.10", "Geospatial features", OUT, "#351"),
    ("13.6", "Support for PRC 3D artwork", OUT, "#353"),
    ("13.7", "Rich media annotations", OUT, "#352"),
    ("14.7.4", "Namespaces for tagged PDF", NOT, "#271"),
    ("14.9.6", "Pronunciation hints", NOT, "#359"),
    ("14.12", "Document parts", OUT, "#358"),
    ("14.13", "Associated files", NOT, "#243"),
    ("7.9.2", "Support for UTF-8", IMPL, ""),
    # "New capabilities added to existing features"
    ("12.5", "Transparency and blend mode attributes for annotations", NOT, "#244"),
    ("12.5.6.12", "Stamp annotation intent", NOT, "#244"),
    ("12.5.6.9", "Polygon and polyline real paths", NOT, "#244"),
    ("7.6.4", "256-bit AES encryption", IMPL, ""),
    ("12.8.3", "ECC-based certificates", PART, "#239"),
    ("7.6.4.3", "Unicode-based passwords", PART, "#226"),
    ("12.11", "Document requirement extensions", NOT, ""),
    ("12.5.5", "New value for tab order of fields and annotations", NOT, "#362"),
    ("14.11.5", "Page-level output intents", NOT, "#360"),
    ("14.11.5", "Referenced external output intents", NOT, "#360"),
    ("7.11.4", "Thumbnails for embedded files", NOT, "#242"),
    ("10.5", "Halftone origin", NOT, "#256"),
    ("8.9 / 8.10", "Measurement and point data for image and form XObjects", NOT, "#257"),
    ("8.9.7", "Length key for inline image data", NOT, "#258"),
    ("12.2", "Viewer preferences enforcement of print scaling", NOT, "#362"),
    ("13.6", "3D measurements", OUT, "#353"),
    ("12.6.4", "GoToDp action", OUT, "#358"),
    ("13.7", "RichMediaExecute action", OUT, "#352"),
    ("12.3.2.3", "GoTo and GoToR extended to link to a structure element", NOT, "#273"),
    ("12.7.5.5", "Extension to signature field locks and seed values", NOT, "#331"),
    ("13.6", "Extensions to 3D viewing conditions", OUT, "#353"),
    ("14.7.5", "Ref reference structure element property", NOT, "#272"),
    ("14.8.5.8", "PageNum and Bates artifact types", NOT, "#303"),
    ("14.8.5.5", "New list types for structured lists", NOT, "#302"),
    ("14.8.5.7", "Short name attribute for table header cells", NOT, "#296"),
    ("14.11.5", "OutputIntents MixingHints and SpectralData", NOT, "#253"),
]

# ── Clause 0.3 deprecations ───────────────────────────────────────────────────────────────────────
# Verified against the Arlington DeprecatedIn column and against this tree.
DEPRECATIONS = [
    ("XFA, including NeedsRendering", "Never written.", "clean"),
    ("Movie, Sound and TrapNet annotations", "Never written.", "clean"),
    ("Movie and Sound actions", "Never written.", "clean"),
    ("Info dictionary", "**Written today** — Title, Author, Subject, Keywords, Creator, Producer.", "#325"),
    ("Assistive technology restrictions via DRM", "Never written.", "clean"),
    ("ProcSet", "**Written today** on every page (PdfPage.cs:107).", "#325"),
    ("OS-specific file specifications", "No file specifications written at all yet; must not emit /DOS /Mac /Unix.", "#242"),
    ("OS-specific additions to Launch actions", "Never written.", "clean"),
    ("Names for XObjects", "Never written.", "clean"),
    ("Names for fonts", "Never written.", "clean"),
    ("Arrays of blend modes", "Never written.", "clean"),
    ("Alternate presentations", "Never written.", "clean"),
    ("Open prepress interface", "Never written.", "clean"),
    ("CharSet for Type 1 fonts", "Never written.", "clean"),
    ("CIDSet for CID fonts", "Never written.", "clean"),
    ("Prepress viewer preferences", "Never written.", "clean"),
    ("NeedAppearances", "**Written today** as false (AcroFormBuilder.cs:101).", "#325"),
    ("adbe.pkcs7.sha1", "Never written.", "clean"),
    ("adbe.x509.rsa_sha1", "Never written.", "clean"),
    ("Encryption of FDF files", "FDF not supported.", "clean"),
    ("Suspects flag in MarkInfo", "Never written.", "clean"),
    ("UR signatures", "Never written.", "clean"),
    ("Transfer functions in the graphics state", "Never written; must stay that way.", "#256"),
]

# ── The extension series ──────────────────────────────────────────────────────────────────────────
# Derived, not transcribed. This table was hand-written once and went stale: it claimed four
# Technical Specifications while ISO/TS 24064 and ISO/TS 32007 were already published, and it knew
# nothing of the PDF Association's own Brotli extension. The registry below is the same source the
# PDF Association generates its public extensions page from, so the list tracks upstream rather
# than whatever was true here on the day it was last edited.
#
# Only BaseVersion 2.0 entries are emitted. The registry also carries ADBE, ESIC and ESIX entries
# against BaseVersion 1.7, which are extensions to PDF 1.7 rather than to this standard.
#
# Verdicts are curated, keyed by "<developer prefix>:<ExtensionLevel>". A registry entry with no
# key here emits "Not assessed", the same way STATUS does, so a new extension shows up as an
# unanswered question rather than a confident-looking default.
EXTENSION_STATUS = {
    "ISO_:32001": (NOT, "#238"),
    "ISO_:32002": (NOT, "#239"),
    "ISO_:32003": (NOT, "#236"),
    "ISO_:32004": (NOT, "#237"),
    "ISO_:24064": (NOT, "#427"),
    "ISO_:32007": (NOT, "#427"),
    "PDFa:1": (NOT, "#412"),
}

# ISO/TS 32005 amends PDF 2.0 but defines no developer extension dictionary, so it is absent from
# the registry by design. It is the one row that still has to be stated by hand.
EXTENSIONS_UNREGISTERED = [
    ("ISO/TS 32005:2023", "PDF 1.7 and 2.0 structure namespace inclusion", None, NOT, "#274"),
]


def fetch(url):
    with urllib.request.urlopen(url) as r:
        return r.read()


def level1_references():
    """The 79 documents ISO 32000-2 cites directly, from the normative-reference graph."""
    db = json.loads(fetch(NORMREFS_URL).decode("utf-8"))["ISO32000_2_DB"]
    by_id = {e["id"]: e for e in db}
    rows = []
    for ref_id in by_id[0]["refs"]:
        e = by_id[ref_id]
        key = "/".join(f"{o['org']} {o.get('stid', '')}".strip() for o in e.get("orgs", []))
        title = e.get("title", "").replace("|", "-").strip()
        # Disambiguate the references that carry no standard id — see the note in STATUS.
        if not any(o.get("stid") for o in e.get("orgs", [])):
            key = f"{key} | {title}"
        rows.append({
            "key": key,
            "title": title,
            "date": e.get("date", ""),
            "status": e.get("status", ""),
            "complexity": e.get("__parser", {}).get("complexity", "unassessed"),
        })
    rows.sort(key=lambda r: r["key"])
    return rows


def pdf_extensions():
    """Every published PDF 2.0 extension, from the PDF Association's own registry."""
    data = json.loads(fetch(EXTENSIONS_URL).decode("utf-8"))
    rows = []
    for group in data["extensionsList"]:
        prefix = group["prefix"]
        for e in group["extensions"]:
            if e.get("BaseVersion") != "2.0":
                continue
            level = e["ExtensionLevel"]
            # OfficialName is the full ISO title; everything from "Document management" on is
            # boilerplate shared by every part, so the designation alone is what the table wants.
            # Pipes would silently break the Markdown table's columns; level1_references does the
            # same. OfficialName is required upstream but is empty on at least one 1.7 entry, so
            # fall back rather than render a blank cell.
            spec = (e.get("OfficialName") or "").split(" Document management")[0].strip()
            spec = (spec or f"{prefix} extension level {level}").replace("|", "-")
            # ShortDesc prefixes most entries with "PDF 2.0", which is redundant in a table whose
            # every row is a PDF 2.0 extension.
            adds = e.get("ShortDesc", "").replace("|", "-")
            if adds.startswith("PDF 2.0 "):
                adds = adds[len("PDF 2.0 "):]
            verdict, issue = EXTENSION_STATUS.get(f"{prefix}:{level}", ("Not assessed", ""))
            rows.append((spec, adds, f"{prefix} {level}", verdict, issue))
    rows.sort(key=lambda r: r[0])
    return rows + EXTENSIONS_UNREGISTERED, data.get("date", "")


def arlington_delta():
    """Objects and keys new in, or deprecated in, PDF 2.0. From tsv/2.0/ — see the header note."""
    import csv
    blob = fetch(ARLINGTON_URL)
    z = zipfile.ZipFile(io.BytesIO(blob))
    names = [n for n in z.namelist() if ARLINGTON_TSV_DIR in n and n.endswith(".tsv")]
    new_objects, new_keys, dep_keys = [], {}, {}
    for n in names:
        obj = os.path.basename(n)[:-4]
        rows = list(csv.DictReader(io.TextIOWrapper(z.open(n), encoding="utf-8"), delimiter="\t"))
        if rows and all(r.get("SinceVersion", "").startswith("2.0") for r in rows):
            new_objects.append(obj)
        for r in rows:
            if r.get("SinceVersion", "").startswith("2.0"):
                new_keys.setdefault(obj, []).append(r["Key"])
            if r.get("DeprecatedIn", "").startswith("2.0"):
                dep_keys.setdefault(obj, []).append(r["Key"])
    return sorted(new_objects), new_keys, dep_keys, len(names)


RULES_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "VellumPdf.Conformance", "Rules")


def rule_class_provenance():
    """Counts behind the PDF/A-2 and PDF/UA-1 disclosure below, read from the rule classes
    themselves rather than typed.

    The 50/101/53 this replaced were still correct when they were checked, which is the point:
    nothing re-derived them, so nothing would have said otherwise. The same family of hand-typed
    figures was wrong the day it was written: "69 rule files" landed in the commit immediately
    before this one, and it both undercounted and named the wrong population; it is 71 rule
    classes. A rule class is a .cs file under Rules/ containing
    ": IConformanceRule"; Rules/ also holds helper files that are not rule classes, and counting
    those instead gives a larger denominator, which is how the acquisition ledger came to report
    105 of a population of 101.
    """
    if not os.path.isdir(RULES_DIR):
        raise SystemExit(f"{RULES_DIR} not found; run this from a full checkout")

    total = citing_19005_2 = citing_19005_2_dated = citing_14289_1 = clean_room = clean_room_verapdf = 0
    for root, _, files in os.walk(RULES_DIR):
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(root, name), "r", encoding="utf-8") as f:
                text = f.read()
            if ": IConformanceRule" not in text:
                continue
            total += 1
            if "19005-2" in text:
                citing_19005_2 += 1
            # The dated form is the one that marks a rule as authored against that standard. The
            # loose count is larger because two PDF/UA-1 rules mention a PDF/A clause in prose,
            # so re-deriving against ISO 19005-2 is 48 rules of work, not 50 (#418).
            if "ISO 19005-2:2011" in text:
                citing_19005_2_dated += 1
            if "14289-1" in text:
                citing_14289_1 += 1
            # Case-insensitively: one rule class writes "Authored clean-room from ISO 14289-1"
            # in lower case, and a case-sensitive match silently dropped it, which is how the
            # first version of this function reported 70 where the answer is 71.
            if re.search("clean-room", text, re.IGNORECASE):
                clean_room += 1
                if re.search("verapdf", text, re.IGNORECASE):
                    clean_room_verapdf += 1
    return (total, citing_19005_2, citing_19005_2_dated, citing_14289_1, clean_room,
            clean_room_verapdf)


def render():
    refs = level1_references()
    new_objects, new_keys, dep_keys, tsv_count = arlington_delta()
    extensions, extensions_date = pdf_extensions()
    (rule_total, rule_19005_2, rule_19005_2_dated, rule_14289_1, clean_room,
     clean_room_verapdf) = rule_class_provenance()
    counts = {}
    for r in refs:
        verdict = STATUS.get(r["key"], (None,))[0]
        counts[verdict or "Not assessed"] = counts.get(verdict or "Not assessed", 0) + 1

    o = []
    w = o.append
    w("<!-- Generated by eng/generate-pdf20-inventory.py. Do not edit by hand. -->")
    w("")
    w("# What VellumPdf implements of PDF 2.0")
    w("")
    w("ISO 32000-2 is large, and \"supports PDF 2.0\" is a claim nobody can check. This table is one")
    w("anybody can: every document the standard cites, every feature it says it added, and every key it")
    w("deprecated, each with what this library does about it.")
    w("")
    w("It is generated from three datasets the PDF Association publishes, plus the specification's own")
    w("clause 0.3. Regenerate with `python eng/generate-pdf20-inventory.py`.")
    w("")
    w("> **This is a coverage inventory, not a conformance test.** Whether output actually conforms is")
    w("> settled by the test suite, not by this page.")
    w("")
    w("> **The PDF/A-2 clause citations are unverified.** ISO 19005-2 is not among the specifications")
    w(f"> held locally. {rule_19005_2} of the {rule_total} rule classes in `VellumPdf.Conformance` cite")
    w("> it, and they are validated against veraPDF's bundled profiles, which encode the standard as")
    w("> test cases rather than reproducing its text, so those clause numbers have no locally")
    w("> checkable source. For PDF/A-2, and only there, veraPDF is what CI fails the build on rather")
    w("> than a cross-check, since no held text is there to decide a disagreement. Of those")
    w(f"> {rule_19005_2}, {rule_19005_2_dated} carry a dated `ISO 19005-2:2011` citation and are the")
    w("> re-derivation work; the other two are PDF/UA-1 rules mentioning a PDF/A clause in prose.")
    w("> ISO 14289-1")
    w("> was in the same position until it was acquired on 2026-09-07; the")
    w(f"> {rule_14289_1} rule classes citing it are now checkable against the text but have not yet")
    w(f"> been re-derived. The word \"clean-room\" appears somewhere in {clean_room} of the")
    w(f"> {rule_total} rule classes, and {clean_room_verapdf} of those also name veraPDF somewhere in")
    w("> the file. Neither is the same as having checked the rule against a clause, which is what")
    w("> #418 tracks for ISO 19005-2 and #428 for ISO 14289-1.")
    w("")
    w("## Normative references")
    w("")
    w(f"ISO 32000-2 cites **{len(refs)}** documents directly. What this library does with each:")
    w("")
    w("| Status | Count |")
    w("| --- | --- |")
    for k in (IMPL, PART, NOT, OUT, "Not assessed"):
        if counts.get(k):
            w(f"| {k} | {counts[k]} |")
    w("")
    w("| Reference | Title | Status | Notes |")
    w("| --- | --- | --- | --- |")
    for r in refs:
        verdict, note, issue = STATUS.get(r["key"], ("Not assessed", "", ""))
        title = r["title"][:78]
        if issue and issue not in note:
            note = f"{note} {issue}".strip()
        display_key = r["key"].split(" | ")[0]
        w(f"| {display_key} | {title} | {verdict} | {note} |")
    w("")
    w("## What PDF 2.0 added")
    w("")
    w("From ISO 32000-2:2020 clause 0.3 — the specification's own list. Note **Annex I is normative and")
    w("contains no feature table**; the standard dropped the per-version table ISO 32000-1 carried, so")
    w("clause 0.3 is the authoritative enumeration.")
    w("")
    w("| Clause | Feature | Status | Issue |")
    w("| --- | --- | --- | --- |")
    for clause, name, verdict, issue in FEATURES:
        w(f"| {clause} | {name} | {verdict} | {issue} |")
    w("")
    w("## What PDF 2.0 deprecated")
    w("")
    w("Also clause 0.3. A file declaring `%PDF-2.0` should not carry these — which makes this table a")
    w("statement about correctness, not coverage.")
    w("")
    w("| Deprecated | This library | Issue |")
    w("| --- | --- | --- |")
    for name, state, issue in DEPRECATIONS:
        w(f"| {name} | {state} | {'' if issue == 'clean' else issue} |")
    w("")
    w("## The extension series")
    w("")
    w("\"PDF 2.0\" as deployed is not only ISO 32000-2:2020. Published extensions amend it, most declared")
    w("in a document through a developer extensions dictionary (ISO 32000-2 7.12.3). The rows below come")
    w("from the PDF Association's extension registry rather than from a list kept here, because a list")
    w(f"kept here went stale. Registry dated {extensions_date}.")
    w("")
    w("| Specification | Adds | Prefix and level | Status | Issue |")
    w("| --- | --- | --- | --- | --- |")
    for spec, adds, level, verdict, issue in extensions:
        w(f"| {spec} | {adds} | {level if level is not None else '-'} | {verdict} | {issue} |")
    w("")
    w("ISO/TS 32005 is the exception: it amends PDF 2.0 but defines no extension dictionary, so it")
    w("appears in no registry and is stated by hand.")
    w("")
    w("## The key-level delta")
    w("")
    w("Derived from the Arlington PDF Model, which records `SinceVersion` and `DeprecatedIn` for every")
    w(f"key in the standard. Generated from its `tsv/2.0/` set ({tsv_count} objects) — **not** `tsv/latest/`,")
    w("which carries two objects that are not part of PDF 2.0.")
    w("")
    w("| | Count |")
    w("| --- | --- |")
    w(f"| Objects wholly new in PDF 2.0 | {len(new_objects)} |")
    w(f"| Objects gaining new keys | {len(new_keys)} |")
    w(f"| Objects with keys deprecated | {len(dep_keys)} |")
    w("")
    w("<details><summary>Objects wholly new in PDF 2.0</summary>")
    w("")
    w(", ".join(f"`{n}`" for n in new_objects))
    w("")
    w("</details>")
    w("")
    w("## Sources and attribution")
    w("")
    w("ISO 32000-2:2020 including Errata Collection 3, clauses 0.3 and 0.4, for the feature and")
    w("deprecation tables.")
    w("")
    w("The reference and key-level tables are built from two datasets published by the")
    w("**PDF Association, Inc.** (<https://www.pdfa.org>), Copyright 2020:")
    w("")
    w("- [pdf-association/PDF2NormRefs](https://github.com/pdf-association/PDF2NormRefs), branch")
    w("  `master`, `data/referencesGraph.json`.")
    w("- [pdf-association/arlington-pdf-model](https://github.com/pdf-association/arlington-pdf-model),")
    w("  `tsv/2.0/`.")
    w("")
    w("Both carry a NOTICE file dual-licensing their contents: Apache-2.0 for software, and the")
    w("**Creative Commons Attribution 4.0 International License** (<https://creativecommons.org/licenses/by/4.0/>)")
    w("for other documentation. These are data files rather than software, so the CC-BY-4.0 terms are")
    w("the ones followed here.")
    w("")
    w("**This page is a modified use of that material.** Rows were filtered, re-ordered, merged across")
    w("the two datasets, and annotated with implementation status that is this project’s own assessment")
    w("and not the PDF Association’s. The datasets themselves are unmodified upstream; only this")
    w("derived table is new.")
    w("")
    w("Both NOTICE files carry this acknowledgement, reproduced as supplied:")
    w("")
    w("> This material is based upon work supported by the Defense Advanced Research Projects Agency")
    w("> (DARPA) under Contract No. HR001119C0079. Any opinions, findings and conclusions or")
    w("> recommendations expressed in this material are those of the author(s) and do not necessarily")
    w("> reflect the views of the Defense Advanced Research Projects Agency (DARPA). Approved for")
    w("> public release.")
    w("")
    w("The material is provided as-is, without warranties or conditions of any kind; see the licences")
    w("above for the governing disclaimers.")
    w("")
    w("The extension table comes from a third PDF Association dataset,")
    w("[pdf-association/pdf-extensions](https://github.com/pdf-association/pdf-extensions),")
    w("`extensions/pdf-extensions.json`. It is licensed **CC-BY-4.0** outright rather than dually, and")
    w("carries no DARPA acknowledgement. Use here is likewise modified: rows are filtered to")
    w("BaseVersion 2.0, the shared title boilerplate is trimmed, and the status and issue columns are")
    w("this project's own.")
    w("")
    w("Two caveats about the data rather than its licensing. PDF2NormRefs was last updated 2021-10-27,")
    w("so its `status` field has drifted — treat it as a hint and re-check anything load-bearing. And")
    w("the PDF Association keeps freely downloadable copies of the unrestricted normative references at")
    w("<https://www.pdfa.org/iso-32000-normative-references/>.")
    w("")
    return "\n".join(o) + "\n"


def main():
    check = "--check" in sys.argv
    text = render()
    if check:
        if not os.path.exists(OUTPUT_PATH):
            print(f"{OUTPUT_PATH} does not exist; run without --check to generate it.", file=sys.stderr)
            return 1
        with open(OUTPUT_PATH, encoding="utf-8") as f:
            if f.read() == text:
                print(f"{OUTPUT_PATH} is up to date.")
                return 0
        print(f"{OUTPUT_PATH} is out of date; run python {sys.argv[0]} to regenerate.", file=sys.stderr)
        return 1
    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"Wrote {OUTPUT_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
