#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The corpora the CI generates with the reference (.github/workflows/ci.yml): the fast ones only, by the owner's
# decision (2026-10-15) — the hull traces (data/traces), the captures and tapes (data/captures), the multiplayer runs
# (data/multi) and the listen runs (data/listen). The screen, sound, client and ten-minute corpora are not made;
# their gates report "no corpus" and skip.
# Usage: tools/ci-corpora.sh <dir with edict_ref>
set -eu
REF="$1"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"
part() {
  name="$1"; shift
  start=$(date +%s)
  "$@" > "data/$name.ci.log" 2>&1 || { echo "$name failed:"; tail -20 "data/$name.ci.log"; exit 1; }
  echo "$name: $(( $(date +%s) - start )) s"
}
part traces sh tools/traces.sh "$REF" data/traces
part captures sh tools/captures.sh "$REF" data/captures
part multi sh tools/multis.sh "$REF" data/multi
part listen sh tools/listen.sh "$REF" data/listen
