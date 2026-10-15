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
i=0
while [ $i -lt 100 ]; do echo wait; i=$((i + 1)); done > id1/edict/wait100.cfg

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
  # the arguments, one per line (<run>.args): the cmdline cvar keeps only 255 characters of them, too few here
  # shellcheck disable=SC2086 # the commands split into words, as Quake's command line does
  printf '%s\n' $CLIENT -edictframes "$FRAMES" -edictperent -edicthash "$OUT/$name.edh" -edictmessages "$OUT/$name.emg" \
    $args > "$OUT/$name.args"
  # shellcheck disable=SC2086
  "$REF/edict_ref$EXE" $CLIENT -edictframes "$FRAMES" -edictperent -edicthash "$OUT/$name.edh" \
    -edictmessages "$OUT/$name.emg" $args > "$OUT/$name.log" 2>&1 || { echo "$name: exit $?"; tail -2 "$OUT/$name.log"; exit 1; }
  echo "$name: $($PY -I "$TOOLS/edh.py" summary "$OUT/$name.edh" | sed -n '1p;4p' | tr '\n' ' ' | cut -c1-160)"
}

for map in start lq_e0m1 lq_e0m2 lq_e0m3 lq_e0m4 lq_e0m5 lq_e0m6 lq_e0m7 lq_e0m8 dev; do
  run "$map" "map $map"
done
run start-changelevel "map start" "exec edict/wait1000.cfg" "changelevel lq_e0m1"
run lq_e0m2-restart "map lq_e0m2" "exec edict/wait1000.cfg" "restart"
# P6 step 9: the local client's commands that go to the server (Cmd_ForwardToServer: say, tell, the cheats, ping,
# pause, name and color while connected, "cmd"), status and kick, which a listen server runs itself, a disconnect
# (CL_Disconnect, Host_ShutdownServer), a map with no server, a map while connected. Their commands are in .cfg files:
# COM_InitArgv keeps 50 arguments (MAX_NUM_ARGVS), and a "+" command ends with the space before the next one.
# (A kick of the local client from its own console: host_client is past the clients at the frame's start, so the
# kick goes through, and the client's next send ends the reference's run with Host_Error.)
W="exec edict/wait100.cfg"
printf '%s\n' "say hello" "say_team for the team" "tell player a word" "tell \"LQ Player\" a word" "god" "notarget" \
  "$W" "fly" "$W" "noclip" "$W" "noclip" "give s 50" "give h 150" "give 6" "ping" "status" "$W" "pause" "$W" "pause" \
  "name renamed" "$W" "color 4 5" "cmd say through cmd" "say \"quoted text\"" "status" "kick nobody" \
  "$W" "prespawn" "spawn" "begin" "$W" "fly" "fly" "god" "kill" "$W" "status" > id1/edict/clientcmds.cfg
printf '%s\n' "$W" "$W" "disconnect" "$W" "say not connected" "map lq_e0m3" "$W" "$W" "map lq_e0m4" \
  > id1/edict/disconnect.cfg
run start-client-cmds "map start" "exec edict/clientcmds.cfg"
# a demo while playing (CL_Disconnect alone shuts the listen server down), then a map while the demo plays
printf '%s\n' "$W" "playdemo demo1" "$W" "$W" "map lq_e0m1" "$W" > id1/edict/playdemo.cfg
run start-playdemo "map start" "exec edict/playdemo.cfg"
run lq_e0m1-disconnect "map lq_e0m1" "exec edict/disconnect.cfg"
# more than 50 arguments: COM_InitArgv keeps the first 50, so the last says never run (one is cut from its word)
run start-argv "map start" "$W" "say a1" "say a2" "say a3" "say a4" "say a5" "say a6" "say a7" "say a8" "say a9" "say a10" \
  "say a11" "say a12" "say a13" "say a14" "say a15" "say a16" "say a17" "say a18" "say a19" "say a20" "say a21" "say a22"
