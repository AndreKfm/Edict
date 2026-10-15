#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# R9 for P6 step 9 (docs/PLAN.md): builds the reference (BUILDS: release, o0, asan; default "release asan"), writes the
# multiplayer corpus (tools/multis.sh) and the listen runs of step 9 (tools/listen.sh) with each, and compares every
# record with data/multi and data/listen. Sanitizer reports fail. Keep the work dir's path short (the cmdline).
# Usage: [BUILDS="release asan"] tools/r9-net.sh [work dir]
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$HOME/r9n}"
PY="python3"; command -v python3 > /dev/null || PY="python"
builds="${BUILDS:-release asan}"
RUNS="start-client-cmds start-playdemo lq_e0m1-disconnect start-argv"
fail=0
for m in $builds; do
  sh "$REPO/ref/build.sh" "$m" "$WORK/build-$m" > /dev/null || { echo "build $m failed"; exit 1; }
  ASAN_OPTIONS=detect_leaks=0:detect_stack_use_after_return=0 sh "$REPO/tools/multis.sh" "$WORK/build-$m" "$WORK/$m/multi" \
    > "$WORK/$m.multi.out" 2>&1 || { echo "$m: multis.sh failed"; tail -3 "$WORK/$m.multi.out"; fail=1; }
  ONLY="$RUNS" ASAN_OPTIONS=detect_leaks=0:detect_stack_use_after_return=0 sh "$REPO/tools/listen.sh" "$WORK/build-$m" \
    "$WORK/$m/listen" > "$WORK/$m.listen.out" 2>&1 || { echo "$m: listen.sh failed"; tail -3 "$WORK/$m.listen.out"; fail=1; }
  if cat "$WORK/$m"/multi/*.log "$WORK/$m"/listen/*.log 2> /dev/null | grep "runtime error\|ERROR: AddressSanitizer" \
      | grep -v -e "misaligned address" -e "signed integer overflow" -e "left shift of" \
          -e "outside the range of representable values of type 'int'" -e "world.c:.*null pointer of type 'edict_t'" \
          -e "edict_capture.c:.*null pointer of type 'edict_t'" -e "draw.c:.*null pointer of type 'qpic_t'" \
          -e "model.c:.*index 4 out of bounds for type 'float\[4\]'" -e "model.c:.*applying zero offset to null pointer" \
      | sort | uniq -c | grep .; then
    echo "$m: sanitizer reports (above)"
    fail=1
  fi
  for edh in "$REPO"/data/multi/*.edh; do
    rel="multi/${edh##*/}"
    out="$($PY -I "$REPO/tools/edh.py" diff "$edh" "$WORK/$m/$rel" 2>&1)" || { fail=1; echo "$rel $m: $out" | head -2; continue; }
    printf "%-34s release vs %-7s %s\n" "$rel" "$m" "$(echo "$out" | tr '\n' ';' | cut -c1-100)"
  done
  for r in $RUNS; do
    rel="listen/$r.edh"
    out="$($PY -I "$REPO/tools/edh.py" diff "$REPO/data/$rel" "$WORK/$m/$rel" 2>&1)" || { fail=1; echo "$rel $m: $out" | head -2; continue; }
    printf "%-34s release vs %-7s %s\n" "$rel" "$m" "$(echo "$out" | tr '\n' ';' | cut -c1-100)"
  done
done
exit $fail
