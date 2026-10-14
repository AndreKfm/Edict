#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The listen-server corpus of P5 (docs/PLAN.md): per map a run without -dedicated, so the server and its local client
# (connected through net_loop.c) share one process, one frame and one rand stream; no key is held, so the player
# stands where it spawned. The reference's state hashes of the run: the server's spawn and frame records, the
# client's records (-edictclient: no screen), the messages the server sent (.emg).
# Usage: tools/listen.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
FRAMES="${FRAMES:-4000}"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
cd "$TOOLS/../data/libreq-v0.09-beta"
for map in start lq_e0m1 lq_e0m2 lq_e0m3 lq_e0m4 lq_e0m5 lq_e0m6 lq_e0m7 lq_e0m8 dev; do
  "$REF/edict_ref$EXE" -edictclient -edictframes "$FRAMES" -edictperent -edicthash "$OUT/$map.edh" \
    -edictmessages "$OUT/$map.emg" +map "$map" > "$OUT/$map.log" 2>&1 || { echo "$map: exit $?"; tail -2 "$OUT/$map.log"; exit 1; }
  echo "$map: $($PY -I "$TOOLS/edh.py" summary "$OUT/$map.edh" | sed -n '1p;4p' | tr '\n' ' ' | cut -c1-160)"
done
