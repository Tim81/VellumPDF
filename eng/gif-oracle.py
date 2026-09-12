# Copyright © Timothy van der Ham (@Tim81)
# SPDX-License-Identifier: Apache-2.0
#
# Independent-codec oracle for the VellumPdf.Kernel GIF tests. Pillow is a second GIF
# implementation, so it stands in both directions: writing a file this project's decoder
# must read, and reading a file this project's encoder must write. Two subcommands:
#
#   gif-oracle.py encode <raw-rgb-path> <width> <height> <out-gif-path>
#     Reads width*height*3 raw RGB bytes (row-major, top-left origin, matching this
#     project's own convention) and writes them as a GIF with Pillow.
#
#   gif-oracle.py decode <in-gif-path> <out-raw-path>
#     Reads a GIF with Pillow and writes back width*height*3 raw RGB bytes in the same
#     layout, so the caller can compare them byte for byte against a known raster.
#
# Exit codes: 0 on success, 3 when Pillow is not installed (the C# side treats this as
# "tool missing": skip locally, fail on CI), 1 on any other error.

import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

try:
    from PIL import Image
except ImportError:
    print("MISSING_MODULE: Pillow is not installed", file=sys.stderr)
    sys.exit(3)


def cmd_encode(raw_path: str, width: str, height: str, out_path: str) -> int:
    w, h = int(width), int(height)
    with open(raw_path, "rb") as f:
        data = f.read()
    if len(data) != w * h * 3:
        print(f"expected {w * h * 3} bytes of RGB, got {len(data)}", file=sys.stderr)
        return 1

    image = Image.frombytes("RGB", (w, h), data)
    # The fixtures this oracle is used against hold at most 256 distinct colours by
    # construction (see GifSpecificationTests), so an adaptive palette of 256 entries
    # assigns one entry per colour rather than merging any two.
    indexed = image.convert("P", palette=Image.Palette.ADAPTIVE, colors=256)
    indexed.save(out_path, format="GIF")
    return 0


def cmd_decode(in_path: str, out_path: str) -> int:
    image = Image.open(in_path).convert("RGB")
    with open(out_path, "wb") as f:
        f.write(image.tobytes())
    return 0


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: gif-oracle.py <encode|decode> ...", file=sys.stderr)
        return 1

    command, args = sys.argv[1], sys.argv[2:]
    if command == "encode" and len(args) == 4:
        return cmd_encode(*args)
    if command == "decode" and len(args) == 2:
        return cmd_decode(*args)

    print(f"usage error for command '{command}'", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
