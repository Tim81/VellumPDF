#!/usr/bin/env bash
# Build the Ghostscript and MuPDF differential oracles from pinned upstream sources.
#
# Neither comes from apt. noble ships Ghostscript 10.02.1 and MuPDF 1.23.10, and the MuPDF gap is
# not cosmetic: 1.23.10 has no `mutool audit`, no `show -r`, and few of `clean`'s options, so a
# developer's run would test different code from CI's. See docs/differential-oracles.md.
#
# Usage: install-oracles.sh [prefix]     (default prefix: $HOME/tools)
set -euo pipefail

GS_VER="${GHOSTSCRIPT_VERSION:-10.07.1}"
GS_TAG="gs${GS_VER//./}"
MU_VER="${MUPDF_VERSION:-1.28.0}"
# Published beside the MuPDF source tarball; Ghostscript ships its own SHA512SUMS with the release.
MU_SHA256="${MUPDF_SHA256:-21c7f064903154f1c3a7458bee81f130fc36f9b5147ea13328f9980e02d2dea2}"

PREFIX="${1:-$HOME/tools}"
SRC="$PREFIX/src"
mkdir -p "$SRC"

command -v make >/dev/null || { echo "make not found; install build-essential" >&2; exit 1; }
command -v cc   >/dev/null || { echo "no C compiler; install build-essential" >&2; exit 1; }

echo "=== Ghostscript $GS_VER"
cd "$SRC"
base="https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/$GS_TAG"
curl -fsSL --retry 3 --retry-delay 2 --retry-all-errors -o "ghostscript-$GS_VER.tar.gz" \
  "$base/ghostscript-$GS_VER.tar.gz"
curl -fsSL --retry 3 --retry-delay 2 --retry-all-errors -o "SHA512SUMS-$GS_VER" "$base/SHA512SUMS"
grep "ghostscript-$GS_VER.tar.gz" "SHA512SUMS-$GS_VER" | sha512sum -c -

rm -rf "ghostscript-$GS_VER"
tar xzf "ghostscript-$GS_VER.tar.gz"
cd "ghostscript-$GS_VER"
./configure --prefix="$PREFIX/gs-$GS_VER" --without-x --without-tesseract >/dev/null
make -j"$(nproc)" >/dev/null
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
make -j"$(nproc)" HAVE_X11=no HAVE_GLUT=no prefix="$PREFIX/mupdf-$MU_VER" install >/dev/null

# Assert each tool answers as itself at the pinned version. A silently different version is the
# failure this whole arrangement exists to prevent: `mutool audit` exists in 1.28 and not in
# noble's 1.23.10, so a version mismatch changes what every downstream assertion compares against.
gs_actual="$("$PREFIX/gs-$GS_VER/bin/gs" --version)"
[ "$gs_actual" = "$GS_VER" ] || { echo "expected gs $GS_VER, got $gs_actual" >&2; exit 1; }
mu_actual="$("$PREFIX/mupdf-$MU_VER/bin/mutool" -v 2>&1 | head -1)"
[ "$mu_actual" = "mutool version $MU_VER" ] || { echo "expected mutool $MU_VER, got $mu_actual" >&2; exit 1; }

echo
echo "GHOSTSCRIPT_HOME=$PREFIX/gs-$GS_VER"
echo "MUPDF_HOME=$PREFIX/mupdf-$MU_VER"
