#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The census of P6, step 2 (docs/PLAN.md): which code of the renderer, the view and the 2D layer the screen corpus
# reaches. Builds the reference with clang's source coverage (ref/build.sh cov), runs tools/screens.sh with it,
# and reports per file the lines and functions run, and every function of those files that never ran.
# Usage: tools/census-screen.sh [work dir]      (LLVM_COV, LLVM_PROFDATA: the tools, default llvm-cov-18 and
#                                                llvm-profdata-18)
set -eu
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$HOME/census-screen}"
COV="${LLVM_COV:-llvm-cov-18}"; PROFDATA="${LLVM_PROFDATA:-llvm-profdata-18}"
sh "$REPO/ref/build.sh" cov "$WORK/build-cov" > /dev/null
rm -rf "$WORK/prof" && mkdir -p "$WORK/prof"
LLVM_PROFILE_FILE="$WORK/prof/%p.profraw" sh "$REPO/tools/screens.sh" "$WORK/build-cov" "$WORK/out" > "$WORK/screens.out"
"$PROFDATA" merge -sparse "$WORK"/prof/*.profraw -o "$WORK/all.profdata"
cd "$WORK/build-cov/src"
FILES="view.c screen.c sbar.c draw.c console.c chase.c r_main.c r_bsp.c r_edge.c r_draw.c r_surf.c r_light.c r_sky.c
 r_alias.c r_sprite.c r_part.c r_efrag.c r_aclip.c r_misc.c r_vars.c d_edge.c d_scan.c d_surf.c d_polyse.c d_sprite.c
 d_part.c d_sky.c d_fill.c d_init.c d_modech.c d_zpoint.c d_vars.c"
# shellcheck disable=SC2086 # the file list splits into words
"$COV" report "$WORK/build-cov/edict_ref" -instr-profile="$WORK/all.profdata" $FILES > "$WORK/files.txt"
# shellcheck disable=SC2086
"$COV" report -show-functions "$WORK/build-cov/edict_ref" -instr-profile="$WORK/all.profdata" $FILES > "$WORK/functions.txt"
cat "$WORK/files.txt"
echo
echo "functions never run:"
# a function row: name, regions, missed regions, cover, lines, missed lines, cover, branches...; 0 regions run
awk '/^File / { f = $2 } $3 == $2 && $2 > 0 && $1 !~ /^(File|Name|---|TOTAL)/ { print "  " f ": " $1 }' "$WORK/functions.txt" \
  | sed "s|$WORK/build-cov/src/||"
