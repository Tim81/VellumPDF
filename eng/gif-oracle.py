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
# "tool missing": skip locally, fail on CI), 4 when `encode` finds its own Pillow round trip
# lossy (the fixture no longer fits inside Pillow's palette capacity), 1 on any other error.

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
    # construction (see GifSpecificationTests), so an adaptive palette of 256 entries is meant
    # to assign one entry per colour rather than merging any two. That holds only as long as the
    # fixture really has 256 or fewer distinct colours and Pillow's quantizer keeps to its
    # capacity; a future Pillow that merges two colours at this exact capacity would make this
    # oracle compare VellumPdf's output against an already-lossy reference and blame the wrong
    # side. Reading the written file back and comparing against the input catches that here,
    # rather than downstream in a pixel diff that points at GifImageLoader.
    indexed = image.convert("P", palette=Image.Palette.ADAPTIVE, colors=256)
    indexed.save(out_path, format="GIF")

    round_tripped = Image.open(out_path).convert("RGB").tobytes()
    if round_tripped != data:
        differing = sum(1 for a, b in zip(data, round_tripped) if a != b)
        print(
            f"Pillow quantized the input lossily: {differing} of {len(data)} bytes differ "
            "after round-tripping through its own encoder, so this oracle's output is not a "
            "faithful reference for this raster.",
            file=sys.stderr,
        )
        return 4
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
