#!/bin/sh
# Builds libreshadefx.dylib (macOS, Apple Silicon and Intel in one file) from ReShade's effect compiler sources plus our C API.
# Needs a Mac with the Xcode command line tools, the GitHub workflow runs it on a macOS runner.
# RESHADE_SRC points at a checkout of https://github.com/crosire/reshade (with deps/spirv submodule).
# windows-paths.patch is applied to a copy, the checkout itself is left alone.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
RESHADE_SRC="${RESHADE_SRC:-$HERE/../../../external/reshade}"
OUT="$HERE/../1.7/Native/osx"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT" "$WORK/source"
cp "$RESHADE_SRC"/source/effect_* "$WORK/source/"
(cd "$WORK" && patch -p1 --quiet < "$HERE/windows-paths.patch")
S="$WORK/source"
clang++ -std=c++20 -O2 -dynamiclib -fvisibility=hidden -DNDEBUG -Wno-deprecated-declarations \
  -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 \
  -I"$S" -I"$RESHADE_SRC/deps/spirv/include/spirv/unified1" -I"$RESHADE_SRC/deps/spirv/include" \
  "$S/effect_lexer.cpp" "$S/effect_preprocessor.cpp" "$S/effect_parser_exp.cpp" "$S/effect_parser_stmt.cpp" \
  "$S/effect_symbol_table.cpp" "$S/effect_expression.cpp" "$S/effect_codegen_spirv.cpp" \
  "$HERE/rfx_capi.cpp" \
  -o "$OUT/libreshadefx.dylib"
strip -x "$OUT/libreshadefx.dylib"
echo "built $OUT/libreshadefx.dylib"
