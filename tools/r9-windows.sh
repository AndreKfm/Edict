#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
#
# R9, Windows leg (Git Bash): builds the reference with LLVM clang for Windows (MSVC ABI, UCRT), regenerates the
# tapes, runs every map and compares the .edh files with a Linux run of tools/r9-linux.sh.
# Usage: tools/r9-windows.sh <dir with Linux <map>.release.a.edh files> [work dir]
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
LIN="$1"; WORK="${2:-${TMPDIR:-/tmp}/edict-r9-windows}"
CLANG="${CLANG:-/c/Program Files/LLVM/bin/clang.exe}"
FRAMES=${FRAMES:-4000}
CC="$CLANG" LIBS="" EXE=".exe" EXTRA_CFLAGS="-DWINDED -D_CRT_SECURE_NO_WARNINGS" \
  sh "$REPO/ref/build.sh" release "$WORK/build" > /dev/null || { echo "Windows build failed"; exit 1; }
mkdir -p "$WORK/runs"
cd "$REPO/data/libreq-v0.09-beta"
seed=0; fail=0
# map:seed; dev uses seed 14 because seed 10 walks the player into an exit to a map the lite package lacks
for entry in start:1 lq_e0m1:2 lq_e0m2:3 lq_e0m3:4 lq_e0m4:5 lq_e0m5:6 lq_e0m6:7 lq_e0m7:8 lq_e0m8:9 dev:14; do
  map=${entry%:*}; seed=${entry#*:}
  python -I "$REPO/tools/tapegen.py" "$WORK/runs/$map.etp" $seed $FRAMES
  "$WORK/build/edict_ref.exe" -dedicated 1 -edictframes $FRAMES -edictperent -edicttape "$WORK/runs/$map.etp" \
    -edicthash "$WORK/runs/$map.win.edh" +map $map > "$WORK/runs/$map.log" 2>&1 || echo "$map: exit $?"
  printf "%-8s Windows vs Linux: " $map
  python -I "$REPO/tools/edh.py" diff "$LIN/$map.release.a.edh" "$WORK/runs/$map.win.edh" | head -3 || fail=1
done
exit $fail
