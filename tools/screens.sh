#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The screen corpus of P6 (docs/PLAN.md): the client corpus of P5 again, now with the screen (-edictscreen): every
# demo of tools/demos.sh (id1/edict/*.dem, which that script writes) and every listen run of tools/listen.sh. The
# hashes get a screen record at every present of the frame buffer (VID_Update, ref/src/edict_vid.c) besides the
# client records, which the screen changes too (R_DrawParticles moves and expires the particles).
# Usage: tools/screens.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
cd "$TOOLS/../data/libreq-v0.09-beta"
for dem in id1/edict/*.dem; do
  run="$(basename "$dem" .dem)"
  case " ${ONLY:-$run} " in *" $run "*) ;; *) continue ;; esac
  # the run ends with its demo; the frame count only bounds it
  "$REF/edict_ref$EXE" -edictscreen -edictframes 100000 -edictperent -edicthash "$OUT/$run.edh" \
    +playdemo "edict/$run" > "$OUT/$run.log" 2>&1 || { echo "$run: exit $?"; tail -2 "$OUT/$run.log"; exit 1; }
  echo "$run: $($PY -I "$TOOLS/edh.py" summary "$OUT/$run.edh" | head -1)"
done
SCREEN=1 sh "$TOOLS/listen.sh" "$REF" "$OUT/listen"
