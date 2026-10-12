#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds the headless reference: copies ref/upstream/WinQuake, applies ref/patches/*.patch in order,
# adds ref/src/*.c and compiles with the reference flags (docs/PLAN.md D2, E.6).
# Usage: ref/build.sh <release|o0|asan|msan> <out-dir>
set -eu
MODE="$1"; OUT="$2"
REF="$(cd "$(dirname "$0")" && pwd)"
rm -rf "$OUT" && mkdir -p "$OUT/src" "$OUT/obj"
cp -r "$REF/upstream/WinQuake/." "$OUT/src/"
for p in "$REF"/patches/*.patch; do
  [ -e "$p" ] || continue
  patch -s -p1 -d "$OUT/src" < "$p" || { echo "patch failed: $p"; exit 1; }
done
cp "$REF"/src/*.c "$REF"/src/*.h "$OUT/src/"
FILES="edict_sys edict_hash edict_xxh64 edict_libc cl_demo cl_input cl_main cl_parse cl_tent chase cmd common console crc cvar draw host host_cmd keys
 mathlib menu model nonintel net_loop net_main edict_tape net_vcr pr_cmds pr_edict pr_exec
 r_aclip r_alias r_bsp r_draw r_edge r_efrag r_light r_main r_misc r_part r_sky r_sprite r_surf r_vars
 d_edge d_fill d_init d_modech d_part d_polyse d_scan d_sky d_sprite d_surf d_vars d_zpoint
 sbar screen snd_null cd_null in_null vid_null sv_main sv_move sv_phys sv_user view wad world zone"
# C89 code: gnu89 (implicit declarations), -fcommon (tentative definitions in headers).
# edict_libc.h pins implementation-defined C library results that reach the state (docs/PLAN.md D3).
BASE="-include edict_libc.h -std=gnu89 -fcommon -ffp-contract=off -fno-strict-aliasing -march=x86-64-v2 -Wabsolute-value -Wpointer-to-int-cast"
case "$MODE" in
  release) CFLAGS="$BASE -O2"; LDFLAGS="" ;;
  o0)      CFLAGS="$BASE -O0"; LDFLAGS="" ;;
  asan)    CFLAGS="$BASE -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined"; LDFLAGS="-fsanitize=address,undefined" ;;
  msan)    CFLAGS="$BASE -O1 -g -fno-omit-frame-pointer -fsanitize=memory -fsanitize-memory-track-origins=2"; LDFLAGS="-fsanitize=memory" ;;
  *) echo "mode: release|o0|asan|msan"; exit 2 ;;
esac
cd "$OUT/src"
for f in $FILES; do
  clang $CFLAGS -c "$f.c" -o "../obj/$f.o" 2>> ../build.log || { echo "compile failed: $f (see $OUT/build.log)"; exit 1; }
done
clang $LDFLAGS -o ../edict_ref ../obj/*.o -lm
echo "built: $OUT/edict_ref ($MODE)"
