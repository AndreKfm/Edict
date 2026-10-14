#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the headless reference executable and the oracle library from the same objects: copies ref/upstream/WinQuake, applies ref/patches/*.patch in order,
# adds ref/src/*.c and compiles with the reference flags (docs/PLAN.md D2, E.6).
# Usage: ref/build.sh <release|o0|asan|msan|cov> <out-dir>   (cov: -O0 with clang's source coverage, for a census)
# Environment: CC (default clang), LIBS (default -lm), EXE (default empty; .exe on Windows), SOUND (1: the sound build),
#              EXTRA_CFLAGS (Windows: -DWINDED, id's switch for its Windows dedicated server)
set -eu
MODE="$1"; OUT="$2"
CC="${CC:-clang}"; LIBS="${LIBS--lm}"; EXE="${EXE:-}"; EXTRA_CFLAGS="${EXTRA_CFLAGS:-}"
# net_dgrm.c's ban (inet_ntoa, inet_addr) is Winsock's on Windows
if [ "$EXE" = ".exe" ]; then LIBS="$LIBS -lws2_32"; fi
REF="$(cd "$(dirname "$0")" && pwd)"
rm -rf "$OUT" && mkdir -p "$OUT/src" "$OUT/obj"
cp -r "$REF/upstream/WinQuake/." "$OUT/src/"
for p in "$REF"/patches/*.patch; do
  [ -e "$p" ] || continue
  patch -s -p1 -d "$OUT/src" < "$p" || { echo "patch failed: $p"; exit 1; }
done
cp "$REF"/src/*.c "$REF"/src/*.h "$OUT/src/"
FILES="edict_sys edict_hash edict_traces edict_capture edict_xxh64 edict_libc edict_libm edict_oracle cl_demo cl_input cl_main cl_parse cl_tent chase cmd common console crc cvar draw host host_cmd keys
 mathlib edict_menu model nonintel net_loop net_main net_dgrm edict_memnet edict_tape net_vcr pr_cmds pr_edict pr_exec
 r_aclip r_alias r_bsp r_draw r_edge r_efrag r_light r_main r_misc r_part r_sky r_sprite r_surf r_vars
 d_edge d_fill d_init d_modech d_part d_polyse d_scan d_sky d_sprite d_surf d_vars d_zpoint
 sbar screen snd_null cd_null in_null edict_vid sv_main sv_move sv_phys sv_user view wad world zone"
# SOUND=1: the sound build (docs/PLAN.md P6 step 8): snd_dma.c, snd_mix.c and snd_mem.c with the harness's DMA
# driver (edict_snd.c) instead of snd_null.c; its rand draws (S_StartSound) make it a corpus of its own
if [ -n "${SOUND:-}" ]; then
  FILES="$(echo $FILES | sed 's/snd_null/snd_dma snd_mix snd_mem edict_snd/')"
  EXTRA_CFLAGS="$EXTRA_CFLAGS -DEDICT_SOUND"
fi
# C89 code: gnu89 (implicit declarations), -fcommon (tentative definitions in headers).
# edict_libc.h pins implementation-defined C library results that reach the state (docs/PLAN.md D3).
# -fno-strict-float-cast-overflow: out-of-range float-to-int conversions saturate to the target range and NaN
# converts to 0 (measured through the oracle), instead of undefined behaviour the optimizer may exploit: clang 23
# folded PF_ftos's "v == (int)v" into an integrality test, clang 18 did not (docs/PLAN.md, section A.2).
BASE="-DEDICT -include edict_libc.h -std=gnu89 -fcommon -ffp-contract=off -fno-strict-float-cast-overflow -fno-strict-aliasing -march=x86-64-v2 -Wabsolute-value -Wpointer-to-int-cast"
case "$MODE" in
  release) CFLAGS="$BASE -O2"; LDFLAGS="" ;;
  o0)      CFLAGS="$BASE -O0"; LDFLAGS="" ;;
  asan)    CFLAGS="$BASE -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined"; LDFLAGS="-fsanitize=address,undefined" ;;
  msan)    CFLAGS="$BASE -O1 -g -fno-omit-frame-pointer -fsanitize=memory -fsanitize-memory-track-origins=2"; LDFLAGS="-fsanitize=memory" ;;
  cov)     CFLAGS="$BASE -O0 -fprofile-instr-generate -fcoverage-mapping"; LDFLAGS="-fprofile-instr-generate" ;;
  *) echo "mode: release|o0|asan|msan|cov"; exit 2 ;;
esac
# Position-independent code on ELF so that the same objects also form the oracle library (docs/TESTING.md, R3).
[ -z "$EXE" ] && CFLAGS="$CFLAGS -fPIC"
CFLAGS="$CFLAGS $EXTRA_CFLAGS"
# The build time in the "Exe:" line of Host_Init (__TIME__, __DATE__) is on the screen: pinned to WinQuake's
# source release, 00:00:00 Dec 21 1999 UTC, so that every build draws the same frames (patch 0008 for the rest).
export SOURCE_DATE_EPOCH=945734400
cd "$OUT/src"
for f in $FILES; do
  # screen.c's one call of V_RenderView reaches the harness's wrapper (edict_vid.c: -edict2d), without a patch line
  FILEFLAGS=""; [ "$f" = screen ] && FILEFLAGS="-DV_RenderView=EdictV_RenderView"
  "$CC" $CFLAGS $FILEFLAGS -c "$f.c" -o "../obj/$f.o" 2>> ../build.log || { echo "compile failed: $f (see $OUT/build.log)"; exit 1; }
done
"$CC" $LDFLAGS -o "../edict_ref$EXE" ../obj/*.o $LIBS
if [ -z "$EXE" ]; then ORACLE="libedict_oracle.so"; else ORACLE="edict_oracle.dll"; fi
"$CC" $LDFLAGS -shared -o "../$ORACLE" ../obj/*.o $LIBS
echo "built: $OUT/edict_ref$EXE and $OUT/$ORACLE ($MODE)"
