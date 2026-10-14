#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The client corpus of P5 (docs/PLAN.md): per run of tools/captures.sh and tools/longruns.sh a demo of what its
# server sent the client (tools/emg.py dem, into the game directory, where playdemo looks: id1/edict/<run>.dem), and
# the reference's client hashes of its playback (.edh with client records, ref/src/edict_hash.c), no screen
# (-edictclient), one hash per entity; then the synthetic demos of tools/demogen.py.
# Usage: tools/demos.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
DATA="$TOOLS/../data"
mkdir -p "$DATA/libreq-v0.09-beta/id1/edict"
cd "$DATA/libreq-v0.09-beta"
for emg in "$DATA"/captures/*.emg "$DATA"/long/*.emg; do
  [ -e "$emg" ] || continue
  run="$(basename "$(dirname "$emg")")_$(basename "$emg" .emg)"      # captures_start, long_lq_e0m3, ...
  $PY -I "$TOOLS/emg.py" dem "$emg" "${emg%.emg}.etp" "id1/edict/$run.dem" > /dev/null
  frames=$(( $(od -An -tu4 -j12 -N4 "${emg%.emg}.etp") + 100 ))      # the tape's frames and some to spare
  "$REF/edict_ref$EXE" -edictclient -edictframes "$frames" -edictperent -edicthash "$OUT/$run.edh" \
    +playdemo "edict/$run" > "$OUT/$run.log" 2>&1 || { echo "$run: exit $?"; tail -2 "$OUT/$run.log"; exit 1; }
  echo "$run: $($PY -I "$TOOLS/edh.py" summary "$OUT/$run.edh" | head -1)"
done

# synthetic demos (tools/demogen.py): what the corpus rarely or never sends
for seed in 1 2 3 4 5 6; do
  run="fuzz_$seed"
  $PY -I "$TOOLS/demogen.py" . "$seed" 3000 "id1/edict/$run.dem" > /dev/null
  "$REF/edict_ref$EXE" -edictclient -edictframes 20000 -edictperent -edicthash "$OUT/$run.edh" \
    +playdemo "edict/$run" > "$OUT/$run.log" 2>&1 || { echo "$run: exit $?"; tail -2 "$OUT/$run.log"; exit 1; }
  echo "$run: $($PY -I "$TOOLS/edh.py" summary "$OUT/$run.edh" | head -1)"
done
