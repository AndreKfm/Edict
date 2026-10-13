#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Cut-2 VM corpus: per map, a VM capture (.ecp, ref/src/edict_capture.c) of spawn and FRAMES frames driven by the
# R9 input tape (same map:seed list as tools/r9-linux.sh), single player, and the hashes of the same run (.edh,
# ref/src/edict_hash.c), whose spawn records are the frame-0 gate.
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
    -edicthash "$OUT/$map.edh" \
    +map "$map" > "$OUT/$map.log" 2>&1 || { echo "$map: exit $?"; tail -2 "$OUT/$map.log"; exit 1; }
  echo "$map: $(wc -c < "$OUT/$map.ecp") bytes"
done

# Spawn variants for the frame-0 gate: the tapes are single player on skill 1, so the other skills (and values that
# SV_SpawnServer rounds or clamps; Cmd_StuffCmds_f ends a +command at any '-', so no negative ones), deathmatch and
# coop (four client slots) are spawned once each, without clients, for one frame (spawn/<map>.<variant>.ecp/.edh).
mkdir -p "$OUT/spawn"
for map in start lq_e0m1 lq_e0m2 lq_e0m3 lq_e0m4 lq_e0m5 lq_e0m6 lq_e0m7 lq_e0m8 dev; do
  for v in skill0 skill2 skill3 skill06 skill26 skill5 dm coop; do
    case $v in
      skill0) n=1; set -- +skill 0 ;;
      skill2) n=1; set -- +skill 2 ;;
      skill3) n=1; set -- +skill 3 ;;
      skill26) n=1; set -- +skill 2.6 ;;
      skill06) n=1; set -- +skill 0.6 ;;
      skill5) n=1; set -- +skill 5 ;;
      dm) n=4; set -- +deathmatch 2 ;;
      coop) n=4; set -- +coop 1 ;;
    esac
    "$REF/edict_ref$EXE" -dedicated $n -edictframes 1 -edictcapture "$OUT/spawn/$map.$v.ecp" \
      -edicthash "$OUT/spawn/$map.$v.edh" "$@" +map "$map" > "$OUT/spawn/$map.$v.log" 2>&1 \
      || { echo "$map $v: exit $?"; tail -2 "$OUT/spawn/$map.$v.log"; exit 1; }
  done
done
echo "spawn variants: $(ls "$OUT/spawn"/*.edh | wc -l)"
