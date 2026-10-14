#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# R9 for the client (P5, docs/PLAN.md): builds the reference at -O0 and with ASan/UBSan, plays every demo of the
# client corpus (tools/demos.sh: data/libreq-v0.09-beta/id1/edict/*.dem) with each, and compares their client
# records with the release build's (data/client/<run>.edh).
# Usage: tools/r9-client.sh [work dir]
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$HOME/r9-client}"
for m in o0 asan; do sh "$REPO/ref/build.sh" $m "$WORK/build-$m" > /dev/null || { echo "build $m failed"; exit 1; }; done
cd "$REPO/data/libreq-v0.09-beta"
fail=0
for dem in id1/edict/*.dem; do
  run=$(basename "$dem" .dem)
  for m in o0 asan; do
    ASAN_OPTIONS=detect_leaks=0 "$WORK/build-$m/edict_ref" -edictclient -edictframes 50000 -edictperent \
      -edicthash "$WORK/$run.$m.edh" +playdemo "edict/$run" > "$WORK/$run.$m.log" 2>&1 || { echo "$run $m: exit $?"; fail=1; }
    # known and documented (docs/PLAN.md E.3): draw.c:885 (draw_disc before Draw_Init), model.c:666 (vecs[0][j], j < 8)
    if grep "runtime error\|ERROR: AddressSanitizer" "$WORK/$run.$m.log" | grep -v "draw.c:885\|model.c:666" | grep -q .; then
      echo "$run $m: sanitizer report"
      grep "runtime error\|ERROR: AddressSanitizer" "$WORK/$run.$m.log" | grep -v "draw.c:885\|model.c:666" | head -3
      fail=1
    fi
    printf "%-18s release vs %-5s " "$run" "$m"
    python3 -I "$REPO/tools/edh.py" diff "$REPO/data/client/$run.edh" "$WORK/$run.$m.edh" | head -1 || fail=1
  done
done
exit $fail
