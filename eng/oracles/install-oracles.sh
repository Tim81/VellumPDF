#!/usr/bin/env bash
# Build the Ghostscript and MuPDF differential oracles from pinned upstream sources.
#
# Neither comes from apt. noble ships Ghostscript 10.02.1 and MuPDF 1.23.10, and the MuPDF gap is
# not cosmetic: 1.23.10 has no `mutool audit`, no `show -r`, and only 21 of `clean`'s roughly 33
# options, so a developer's run would test different code from CI's. See
# docs/differential-oracles.md.
#
# Usage: install-oracles.sh [prefix]     (default prefix: $HOME/tools, must be an absolute path)
set -euo pipefail

GS_VER="${GHOSTSCRIPT_VERSION:-10.07.1}"
GS_TAG="gs${GS_VER//./}"
MU_VER="${MUPDF_VERSION:-1.28.0}"

# Both checksums below are committed literals, not values fetched at run time. Ghostscript's
# SHA512SUMS and MuPDF's published SHA-256 are both served from the same host as the tarball they
# describe, so verifying against a same-run fetch of either only catches a truncated download, not
# a replaced one. Each literal was produced by hashing the downloaded tarball directly and
# cross-checking that hash against the upstream sums file, then pinning the result here; an
# environment override is still honoured for testing a newer release, but it is loud about
# bypassing the reviewed value rather than silent.
GS_SHA512_PINNED="480aef1284dbe4d059dd0f9cc60dd5772d5093239df68b838f7f4f7d2efd77de8f03a7baeffbdbe65126a660c1c169137dd7d8cc28dd3bab59cd286a24ca1491"
if [ -n "${GHOSTSCRIPT_SHA512:-}" ]; then
  echo "warning: GHOSTSCRIPT_SHA512 overrides the reviewed pin; the checksum check no longer verifies the reviewed release" >&2
fi
GS_SHA512="${GHOSTSCRIPT_SHA512:-$GS_SHA512_PINNED}"

MU_SHA256_PINNED="21c7f064903154f1c3a7458bee81f130fc36f9b5147ea13328f9980e02d2dea2"
if [ -n "${MUPDF_SHA256:-}" ]; then
  echo "warning: MUPDF_SHA256 overrides the reviewed pin; the checksum check no longer verifies the reviewed release" >&2
fi
MU_SHA256="${MUPDF_SHA256:-$MU_SHA256_PINNED}"

PREFIX="${1:-$HOME/tools}"
case "$PREFIX" in
  /*) ;;
  *) echo "prefix must be an absolute path, got '$PREFIX'" >&2; exit 1 ;;
esac
SRC="$PREFIX/src"
mkdir -p "$SRC"

for tool in make cc curl tar sha256sum sha512sum nproc; do
  command -v "$tool" >/dev/null || { echo "$tool not found; install build-essential and curl" >&2; exit 1; }
done
JOBS="$(nproc 2>/dev/null || echo 1)"

echo "=== Ghostscript $GS_VER"
cd "$SRC"
base="https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/$GS_TAG"
curl -fsSL --retry 3 --retry-delay 2 --retry-all-errors -o "ghostscript-$GS_VER.tar.gz" \
  "$base/ghostscript-$GS_VER.tar.gz"
echo "$GS_SHA512  ghostscript-$GS_VER.tar.gz" | sha512sum -c -

rm -rf "ghostscript-$GS_VER"
tar xzf "ghostscript-$GS_VER.tar.gz"
cd "ghostscript-$GS_VER"
./configure --prefix="$PREFIX/gs-$GS_VER" --without-x --without-tesseract >/dev/null
make -j"$JOBS" >/dev/null
make install >/dev/null

echo "=== MuPDF $MU_VER"
cd "$SRC"
curl -fsSL --retry 3 --retry-delay 2 --retry-all-errors -o "mupdf-$MU_VER-source.tar.gz" \
  "https://mupdf.com/downloads/archive/mupdf-$MU_VER-source.tar.gz"
echo "$MU_SHA256  mupdf-$MU_VER-source.tar.gz" | sha256sum -c -

rm -rf "mupdf-$MU_VER-source"
tar xzf "mupdf-$MU_VER-source.tar.gz"
cd "mupdf-$MU_VER-source"
# No viewer is needed, and skipping it drops the X11 and GLUT build dependencies.
make -j"$JOBS" HAVE_X11=no HAVE_GLUT=no prefix="$PREFIX/mupdf-$MU_VER" install >/dev/null

# Assert each tool answers as itself at the pinned version. A silently different version is the
# failure this whole arrangement exists to prevent: `mutool audit` exists in 1.28 and not in
# noble's 1.23.10, so a version mismatch changes what every downstream assertion compares against.
# Each version query is guarded by an `if` so a missing binary reports the message below instead of
# aborting on the assignment itself under `set -e`.
if gs_actual="$("$PREFIX/gs-$GS_VER/bin/gs" --version)"; then
  :
else
  echo "gs did not answer at $PREFIX/gs-$GS_VER/bin/gs (expected version $GS_VER)" >&2
  exit 1
fi
[ "$gs_actual" = "$GS_VER" ] || { echo "expected gs $GS_VER, got $gs_actual" >&2; exit 1; }

if mu_actual="$("$PREFIX/mupdf-$MU_VER/bin/mutool" -v 2>&1 | head -1)"; then
  :
else
  echo "mutool did not answer at $PREFIX/mupdf-$MU_VER/bin/mutool (expected version $MU_VER)" >&2
  exit 1
fi
[ "$mu_actual" = "mutool version $MU_VER" ] || { echo "expected mutool $MU_VER, got $mu_actual" >&2; exit 1; }

echo
echo "GHOSTSCRIPT_HOME=$PREFIX/gs-$GS_VER"
echo "MUPDF_HOME=$PREFIX/mupdf-$MU_VER"
