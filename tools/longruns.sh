#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The long runs of the cut-3 gate (docs/PLAN.md section C): new tapes of FRAMES frames (default 43,200: ten minutes at
# 72 frames per second) on three maps, and the reference's state hashes of each run (.edh, one spawn record per spawn
# and one frame record per frame). No capture: the port takes the cvars and the rand state at the first spawn from the
# corpus capture of the same map (tools/captures.sh), which the spawn record of the long run confirms.
# Usage: tools/longruns.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
FRAMES="${FRAMES:-43200}"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
cd "$TOOLS/../data/libreq-v0.09-beta"
# map:seed; seeds other than the corpus's (tools/captures.sh), so that the tapes are new
for entry in lq_e0m1:101 lq_e0m3:103 lq_e0m6:106; do
  map=${entry%:*}; seed=${entry#*:}
  $PY -I "$TOOLS/tapegen.py" "$OUT/$map.etp" "$seed" "$FRAMES"
  "$REF/edict_ref$EXE" -dedicated 1 -edictframes "$FRAMES" -edicttape "$OUT/$map.etp" -edicthash "$OUT/$map.edh" \
    +map "$map" > "$OUT/$map.log" 2>&1 || { echo "$map: exit $?"; tail -2 "$OUT/$map.log"; exit 1; }
  echo "$map: $(wc -c < "$OUT/$map.edh") bytes of hashes"
done
