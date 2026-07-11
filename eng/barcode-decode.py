# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# Decode oracle for the VellumPdf.Barcodes test suite. Reads an image path from
# argv[1], decodes every barcode found with zxing-cpp, and prints one line per
# result: format<TAB>content_type<TAB>text<TAB>file_id. Binary content is not
# reliably representable as text, so its "text" column is a hex digest of the
# raw bytes instead. The fourth column is Macro PDF417's decoded file id
# (zxing-cpp's "FileId" extra field) when the symbol carries one, empty
# otherwise; it is an appended column so existing 3-column callers still work
# by reading only the first three fields.
#
# Exit codes: 0 on success (including zero barcodes found, a valid outcome for
# a page with no symbols), 3 when zxing-cpp or Pillow is not installed (the C#
# side treats this as "tool missing": skip locally, fail on CI), 1 otherwise.

import sys

# Decoded text can contain arbitrary Unicode (e.g. a QR symbol carrying an emoji), and the
# default console encoding on Windows cannot represent it. Reconfigure both streams to UTF-8
# up front so printing never raises UnicodeEncodeError.
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

try:
    import zxingcpp
    from PIL import Image
except ImportError:
    print("MISSING_MODULE: zxingcpp and/or Pillow are not installed", file=sys.stderr)
    sys.exit(3)


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: barcode-decode.py <image-path>", file=sys.stderr)
        return 1

    image = Image.open(sys.argv[1])
    results = zxingcpp.read_barcodes(image, ean_add_on_symbol=zxingcpp.EanAddOnSymbol.Read)

    for result in results:
        content_type = result.content_type.name
        text = result.bytes.hex() if content_type == "Binary" else result.text
        file_id = (result.extra or {}).get("FileId", "")
        print(f"{result.format.name}\t{content_type}\t{text}\t{file_id}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
