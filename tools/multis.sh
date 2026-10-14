#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The multiplayer corpus of P6 step 9 (docs/PLAN.md): version 2 tapes of tools/tapegen.py with four clients (who
# join, leave, reconnect, lose their connection, send host commands), run by the dedicated reference with four
# client slots: deathmatch, and coop, where the cheats and kick are allowed. Per run the capture (.ecp: the inputs at
# the first spawn), the hashes (.edh) and the messages (.emg), whose lane names each message's client.
# Usage: tools/multis.sh <dir with edict_ref or edict_ref.exe> <out-dir>
set -eu
REF="$(cd "$1" && pwd)"
mkdir -p "$2"
OUT="$(cd "$2" && pwd)"
FRAMES="${FRAMES:-6000}"
EXE=""; [ -x "$REF/edict_ref.exe" ] && EXE=".exe"
PY="python3"; command -v python3 > /dev/null || PY="python"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
cd "$TOOLS/../data/libreq-v0.09-beta"
# run:map:seed:mode
for entry in dm_lq_e0m1:lq_e0m1:201:dm coop_lq_e0m2:lq_e0m2:202:coop dm_dev:dev:203:dm coop_start:start:204:coop \
             dm_lq_e0m6:lq_e0m6:205:dm coop_lq_e0m3:lq_e0m3:206:coop; do
  run=${entry%%:*}; rest=${entry#*:}; map=${rest%%:*}; rest=${rest#*:}; seed=${rest%%:*}; mode=${rest#*:}
  case " ${ONLY:-$run} " in *" $run "*) ;; *) continue ;; esac
  if [ "$mode" = coop ]; then
    $PY -I "$TOOLS/tapegen.py" "$OUT/$run.etp" "$seed" "$FRAMES" --clients 4 --coop
    set -- +coop 1
  else
    $PY -I "$TOOLS/tapegen.py" "$OUT/$run.etp" "$seed" "$FRAMES" --clients 4
    set --
  fi
  "$REF/edict_ref$EXE" -dedicated 4 -edictframes "$FRAMES" -edicttape "$OUT/$run.etp" -edictcapture "$OUT/$run.ecp" \
    -edicthash "$OUT/$run.edh" -edictmessages "$OUT/$run.emg" "$@" +map "$map" > "$OUT/$run.log" 2>&1 \
    || { echo "$run: exit $?"; tail -2 "$OUT/$run.log"; exit 1; }
  echo "$run: $(grep -c "entered the game" "$OUT/$run.log") entered, $(grep -c "removed" "$OUT/$run.log") removed"
done
