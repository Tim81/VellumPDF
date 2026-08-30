# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# One-off generator for
# tests/VellumPdf.Reader.Tests/Fixtures/ThirdParty/hybrid-samesection-undefined.pdf.
#
# Neither qpdf nor poppler can produce a hybrid-reference file at all (see the fixtures README), so
# this one is hand-built like its four siblings in that corpus. Hand-editing 20-byte classic xref
# rows, a binary cross-reference-stream body, /Length, and startxref by hand invites exactly the
# kind of silent mismatch #206 warns about — a fixture that looks right but no longer exercises the
# construct it exists to pin — so this script derives every offset and length from the object
# bytes it just wrote, rather than having them typed in twice.
#
# The file pins two things in one revision's classic table + /XRefStm pair (ISO 32000-2 §7.5.8.4):
#   - object 4 is freed by the classic table AND defined by the /XRefStm. VellumPdf.Reader (#206)
#     reads the free entry as winning, matching qpdf and the reading pdf-association/pdf-issues#237
#     (open) leans toward — see the fixtures README for the argument and why the clause itself does
#     not settle this.
#   - object 7 is defined ONLY by the /XRefStm — the classic table never mentions it. This is what
#     stops the object-4 assertion from being vacuous: a reader that skipped the /XRefStm entirely,
#     rejected it on an offset guard, or swallowed a parse failure would also fail to resolve
#     object 7, not just correctly return null for object 4.
#
# Usage: python eng/generate-hybrid-samesection-fixture.py
# Writes tests/VellumPdf.Reader.Tests/Fixtures/ThirdParty/hybrid-samesection-undefined.pdf.

import pathlib

# Anchored to this script's own location, not the current working directory: run from anywhere but
# the repo root, a cwd-relative path either misses the target entirely or writes the fixture into
# the wrong tree.
OUTPUT_PATH = (
    pathlib.Path(__file__).resolve().parent.parent
    / "tests/VellumPdf.Reader.Tests/Fixtures/ThirdParty/hybrid-samesection-undefined.pdf"
)

HEADER = b"%PDF-1.5\n%\xe2\xe3\xcf\xd3\n"

CONTENT_STREAM_BODY = b"BT /F1 24 Tf 40 100 Td (HYBRIDXREFSTM) Tj ET"


def indirect(num: int, body: bytes) -> bytes:
    return f"{num} 0 obj\n".encode() + body + b"\nendobj\n"


def xref_row(offset: int, generation: int) -> bytes:
    # /W [1 4 2]: 1-byte type (always 1, an uncompressed object), 4-byte big-endian offset,
    # 2-byte big-endian generation.
    return bytes([1]) + offset.to_bytes(4, "big") + generation.to_bytes(2, "big")


def classic_entry(offset_or_next_free: int, generation: int, kind: str) -> bytes:
    # ISO 32000-2 §7.5.4: exactly 20 bytes — 10-digit field, space, 5-digit field, space,
    # keyword, space, then the line terminator ("f " / "n " already account for the trailing
    # space before "\n" below).
    return f"{offset_or_next_free:010d} {generation:05d} {kind} \n".encode()


def build() -> bytes:
    obj1 = indirect(1, b"<< /Type /Catalog /Pages 2 0 R >>")
    obj2 = indirect(2, b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    obj3 = indirect(
        3,
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] "
        b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    )
    obj4 = indirect(
        4,
        f"<< /Length {len(CONTENT_STREAM_BODY)} >>\nstream\n".encode()
        + CONTENT_STREAM_BODY
        + b"\nendstream",
    )
    obj5 = indirect(5, b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    # The object this fixture's second half pins: defined only by the /XRefStm below, absent from
    # the classic table entirely. A pre-1.5 reader following only the classic table would never
    # learn this object exists at all — a stronger hiding than a free entry.
    #
    # Whether that also makes the file non-conforming is less clear-cut than it looks. ISO 32000-2
    # §7.5.4 says "the cross-reference table (comprising the original cross-reference section and
    # all update sections) shall contain one entry for each object number from 0 to the maximum
    # object number defined in the PDF file" — that sentence never says "classic table only", and
    # §7.5.8.1 calls a cross-reference stream "equivalent to the cross-reference table". Read that
    # way, the /XRefStm's own entry for object 7 already satisfies §7.5.4, and this file conforms;
    # scoping the requirement to the classic table specifically is an inference this script is
    # making, not something the clause states. It isn't a groundless inference — §7.5.8.4's own
    # EXAMPLE gives every object its /XRefStm hides a classic free entry too, marking it free
    # rather than omitting it — but this file departs from that convention on purpose: the classic
    # table has no entry for object 7 at all, not even a free one. That absence is what makes the
    # object-7 assertion discriminating — a reader that skipped the /XRefStm outright, or that
    # this fixture shape happened to fail open for some unrelated reason, would fail to resolve
    # object 7 too, not just correctly null object 4.
    obj7 = indirect(7, b"<< /Type /ExData /Note (SAMESECTIONSTREAM) >>")

    body = HEADER
    offsets: dict[int, int] = {}
    for num, obj in [(1, obj1), (2, obj2), (3, obj3), (4, obj4), (5, obj5), (7, obj7)]:
        offsets[num] = len(body)
        body += obj

    xref_stream_body = xref_row(offsets[4], 0) + xref_row(offsets[7], 0)
    xref_stream_dict = (
        f"<< /Type /XRef /Size 8 /Index [4 1 7 1] /W [1 4 2] /Root 1 0 R "
        f"/Length {len(xref_stream_body)} >>\nstream\n"
    ).encode()
    obj6 = b"6 0 obj\n" + xref_stream_dict + xref_stream_body + b"\nendstream\nendobj\n"
    offsets[6] = len(body)
    body += obj6

    classic_table_offset = len(body)
    classic = b"xref\n0 7\n"
    # Object 0: free-list head, next free object 4, generation 65535 (ISO 32000-2 §7.5.4).
    classic += classic_entry(4, 65535, "f")
    classic += classic_entry(offsets[1], 0, "n")
    classic += classic_entry(offsets[2], 0, "n")
    classic += classic_entry(offsets[3], 0, "n")
    # Object 4: freed here, next-generation-if-reused 1. This is the free entry the classic table
    # and the /XRefStm disagree over. VellumPdf.Reader reads the classic table's free entry as
    # already satisfying the search (ISO 32000-2 §7.5.8.4's "not found" clause never says whether a
    # free entry counts as found — see the fixtures README), so this wins and object 4 resolves to
    # null.
    classic += classic_entry(0, 1, "f")
    classic += classic_entry(offsets[5], 0, "n")
    # Object 6 is the cross-reference stream itself; marking it free here (not linked into the
    # object-0 free chain — nothing points to it as "next free") keeps it unreachable through the
    # ordinary object graph, matching how hybrid-spec-convention.pdf treats its own xref stream.
    classic += classic_entry(0, 0, "f")
    body += classic

    body += (
        f"trailer\n<< /Size 8 /Root 1 0 R /XRefStm {offsets[6]} >>\n"
        f"startxref\n{classic_table_offset}\n%%EOF\n"
    ).encode()

    return body


def main() -> None:
    data = build()
    out = pathlib.Path(OUTPUT_PATH)
    out.write_bytes(data)
    print(f"Wrote {len(data)} bytes to {out}")


if __name__ == "__main__":
    main()
