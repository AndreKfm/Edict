#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Phase-1 gate corpus: per map, the loader dump and COUNT hull traces from the reference (ref/src/edict_traces.c).
# Usage: tools/traces.sh <dir with edict_ref or edict_ref.exe> <out-dir> [count]
# The seed is the map's position in the list; the traces do not depend on the frame that ran before them.
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
COUNT="${3:-100000}"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
cd "$(dirname "$0")/../data/libreq-v0.09-beta"
for entry in start:1 lq_e0m1:2 lq_e0m2:3 lq_e0m3:4 lq_e0m4:5 lq_e0m5:6 lq_e0m6:7 lq_e0m7:8 lq_e0m8:9 dev:10; do
  map=${entry%:*}; seed=${entry#*:}
  "$REF/edict_ref$EXE" -dedicated 1 -edictframes 2 -edicttraces "$OUT/$map.etr" "$COUNT" "$seed" +map "$map" \
    > "$OUT/$map.log" 2>&1 || { echo "$map: exit $?"; tail -2 "$OUT/$map.log"; exit 1; }
  echo "$map: $(tail -1 "$OUT/$map.log")"
done
