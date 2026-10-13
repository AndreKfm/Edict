#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Cut-2 VM corpus: per map, a VM capture (.ecp, ref/src/edict_capture.c) of spawn and FRAMES frames driven by the
# R9 input tape (same map:seed list as tools/r9-linux.sh), single player.
# Usage: tools/captures.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
FRAMES="${FRAMES:-4000}"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
cd "$TOOLS/../data/libreq-v0.09-beta"
for entry in start:1 lq_e0m1:2 lq_e0m2:3 lq_e0m3:4 lq_e0m4:5 lq_e0m5:6 lq_e0m6:7 lq_e0m7:8 lq_e0m8:9 dev:14; do
  map=${entry%:*}; seed=${entry#*:}
  $PY -I "$TOOLS/tapegen.py" "$OUT/$map.etp" "$seed" "$FRAMES"
  "$REF/edict_ref$EXE" -dedicated 1 -edictframes "$FRAMES" -edicttape "$OUT/$map.etp" -edictcapture "$OUT/$map.ecp" \
    +map "$map" > "$OUT/$map.log" 2>&1 || { echo "$map: exit $?"; tail -2 "$OUT/$map.log"; exit 1; }
  echo "$map: $(wc -c < "$OUT/$map.ecp") bytes"
done
