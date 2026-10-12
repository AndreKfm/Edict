#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# R9 self-check on Linux: release twice, release vs -O0, release vs asan; every map, single player (-dedicated 1),
# FRAMES frames driven by a generated input tape (seed = map number), per-entity hashes.
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS="$REPO/tools"
cd "$REPO"
for m in release o0 asan; do sh ref/build.sh $m $HOME/edict-ref-$m > /dev/null || { echo "build $m failed"; exit 1; }; done
cd data/libreq-v0.09-beta
OUT=$HOME/r9; rm -rf $OUT; mkdir -p $OUT
FRAMES=${FRAMES:-4000}
seed=0
for map in start lq_e0m1 lq_e0m2 lq_e0m3 lq_e0m4 lq_e0m5 lq_e0m6 lq_e0m7 lq_e0m8 dev; do
  seed=$((seed + 1)); python3 -I $TOOLS/tapegen.py $OUT/$map.etp $seed $FRAMES
  for run in release:a release:b o0:a asan:a; do
    m=${run%:*}; r=${run#*:}
    ASAN_OPTIONS=detect_leaks=0 $HOME/edict-ref-$m/edict_ref -dedicated 1 -edictframes $FRAMES -edictperent \
      -edicttape $OUT/$map.etp -edicthash $OUT/$map.$m.$r.edh +map $map > $OUT/$map.$m.$r.log 2>&1 || { echo "$map $m.$r: exit $?"; tail -2 $OUT/$map.$m.$r.log; }
  done
  printf "%-8s " $map; python3 -I $TOOLS/edh.py summary $OUT/$map.release.a.edh | sed -n 3,4p | tr '\n' ' ' | cut -c1-150; echo
  for cmp in release.b o0.a asan.a; do
    printf "   release.a vs %-9s " $cmp; python3 -I $TOOLS/edh.py diff $OUT/$map.release.a.edh $OUT/$map.$cmp.edh | head -3
  done
done
