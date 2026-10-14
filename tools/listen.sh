#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The listen-server corpus of P5 (docs/PLAN.md): per map a run without -dedicated, so the server and its local client
# (connected through net_loop.c) share one process, one frame and one rand stream; no key is held, so the player
# stands where it spawned. The reference's state hashes of the run: the server's spawn and frame records, the
# client's records (-edictclient: no screen), the messages the server sent (.emg).
# Two runs spawn a second time while the client is connected: after the waits of id1/edict/wait1000.cfg (written
# here; one wait per frame), a changelevel to the next map and a restart of the same one. Their console commands are
# also written as <run>.cmd, which the port's gate runs in place of "map <run>".
# With SCREEN=1 the client draws its screen (-edictscreen), and the hashes get its presents too (tools/screens.sh).
# Usage: [ONLY="<run> ..."] [SCREEN=1] tools/listen.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
FRAMES="${FRAMES:-4000}"
CLIENT="-edictclient"; [ -n "${SCREEN:-}" ] && CLIENT="-edictscreen ${EXTRA:-}"     # EXTRA: as in tools/screens.sh
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
cd "$TOOLS/../data/libreq-v0.09-beta"
mkdir -p id1/edict
i=0
while [ $i -lt 1000 ]; do echo wait; i=$((i + 1)); done > id1/edict/wait1000.cfg

# run <name> <console commands, one per argument>: the reference with +<command> for each
run() {
  name="$1"; shift
  case " ${ONLY:-$name} " in *" $name "*) ;; *) return 0 ;; esac
  args=""
  : > "$OUT/$name.cmd"
  for c in "$@"; do
    args="$args +$c"
    echo "$c" >> "$OUT/$name.cmd"
  done
  # shellcheck disable=SC2086 # the commands split into words, as Quake's command line does
  "$REF/edict_ref$EXE" $CLIENT -edictframes "$FRAMES" -edictperent -edicthash "$OUT/$name.edh" \
    -edictmessages "$OUT/$name.emg" $args > "$OUT/$name.log" 2>&1 || { echo "$name: exit $?"; tail -2 "$OUT/$name.log"; exit 1; }
  echo "$name: $($PY -I "$TOOLS/edh.py" summary "$OUT/$name.edh" | sed -n '1p;4p' | tr '\n' ' ' | cut -c1-160)"
}

for map in start lq_e0m1 lq_e0m2 lq_e0m3 lq_e0m4 lq_e0m5 lq_e0m6 lq_e0m7 lq_e0m8 dev; do
  run "$map" "map $map"
done
run start-changelevel "map start" "exec edict/wait1000.cfg" "changelevel lq_e0m1"
run lq_e0m2-restart "map lq_e0m2" "exec edict/wait1000.cfg" "restart"
