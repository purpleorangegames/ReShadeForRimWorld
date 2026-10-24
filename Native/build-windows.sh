#!/bin/sh
# Builds reshadefx.dll (Windows, cross compiled with MinGW) from ReShade's effect compiler sources plus our C API.
# RESHADE_SRC points at a checkout of https://github.com/crosire/reshade (with deps/spirv submodule).
# windows-paths.patch is applied to a copy, the checkout itself is left alone.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
RESHADE_SRC="${RESHADE_SRC:-$HERE/../../../external/reshade}"
OUT="$HERE/../1.7/Native/win-x64"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT" "$WORK/source"
cp "$RESHADE_SRC"/source/effect_* "$WORK/source/"
(cd "$WORK" && patch -p1 --quiet < "$HERE/windows-paths.patch")
S="$WORK/source"
# -fno-char8_t and share.h match what ReShade gets from MSVC
x86_64-w64-mingw32-g++ -std=c++20 -fno-char8_t -include share.h -O2 -shared -DNDEBUG -Wno-deprecated-declarations \
  -I"$S" -I"$RESHADE_SRC/deps/spirv/include/spirv/unified1" -I"$RESHADE_SRC/deps/spirv/include" \
  "$S/effect_lexer.cpp" "$S/effect_preprocessor.cpp" "$S/effect_parser_exp.cpp" "$S/effect_parser_stmt.cpp" \
  "$S/effect_symbol_table.cpp" "$S/effect_expression.cpp" "$S/effect_codegen_spirv.cpp" \
  "$HERE/rfx_capi.cpp" \
  -static -static-libstdc++ -static-libgcc -s \
  -o "$OUT/reshadefx.dll"
echo "built $OUT/reshadefx.dll"
