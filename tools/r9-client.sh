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
# a log's sanitizer reports that are not among the known ones
known() {
  grep "runtime error\|ERROR: AddressSanitizer" "$1" \
    | grep -v -e "draw.c:885" -e "model.c:666" -e "cl_parse.c:548" -e "left shift of" -e "signed integer overflow" \
        -e "model.c:.*applying zero offset to null pointer" -e "r_part.c:.*out of bounds for type 'vec3_t'"
}
for dem in id1/edict/*.dem; do
  run=$(basename "$dem" .dem)
  for m in o0 asan; do
    ASAN_OPTIONS=detect_leaks=0 "$WORK/build-$m/edict_ref" -edictclient -edictframes 50000 -edictperent \
      -edicthash "$WORK/$run.$m.edh" +playdemo "edict/$run" > "$WORK/$run.$m.log" 2>&1 || { echo "$run $m: exit $?"; fail=1; }
    # known and documented (docs/PLAN.md E.3), the kinds tools/r9-screen.sh lets pass: draw.c:885 (draw_disc before
    # Draw_Init), model.c:666 (vecs[0][j], j < 8), cl_parse.c:548 (1<<31), model.c's zero offset to a null visdata,
    # shifts of negative values or into the sign (LittleLong), r_part.c's vec3_t index
    if known "$WORK/$run.$m.log" | grep -q .; then
      echo "$run $m: sanitizer report"
      known "$WORK/$run.$m.log" | head -3
      fail=1
    fi
    printf "%-18s release vs %-5s " "$run" "$m"
    python3 -I "$REPO/tools/edh.py" diff "$REPO/data/client/$run.edh" "$WORK/$run.$m.edh" | head -1 || fail=1
  done
done
exit $fail
