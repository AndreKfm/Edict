#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# R9 for the screen (P6, docs/PLAN.md): builds the reference at -O0 and with ASan/UBSan, runs the screen corpus
# (tools/screens.sh) with each, and compares every record, the screen's presents included, with the release
# build's (data/screen). With a reference directory as the second argument it runs that build only (the Windows
# leg: tools/screens.sh needs edict_ref.exe there).
# ASan runs without its fake stack (detect_stack_use_after_return=0): with it, locals live on the heap, and
# R_RenderView's check that the stack is within 10,000 bytes of where R_Init found it stops the run.
# Sanitizer reports: an AddressSanitizer error fails; of UBSan's, the kinds docs/PLAN.md E.3 records for the
# renderer and the client pass (misaligned surfcache_t, signed overflow and shifts, a float out of int's range,
# the null-pointer offsetof idiom, the known sites), and any other fails. BUILDS="o0" (or "asan") runs one build.
# Usage: [BUILDS="o0 asan"] tools/r9-screen.sh [work dir] [reference dir]
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$HOME/r9-screen}"
PY="python3"; command -v python3 > /dev/null || PY="python"
if [ $# -ge 2 ]; then
  builds="given"; mkdir -p "$WORK"; ln -sfn "$(cd "$2" && pwd)" "$WORK/build-given" 2> /dev/null || cp -r "$2" "$WORK/build-given"
else
  builds="${BUILDS:-o0 asan}"
  for m in $builds; do sh "$REPO/ref/build.sh" $m "$WORK/build-$m" > /dev/null || { echo "build $m failed"; exit 1; }; done
fi
fail=0
for m in $builds; do
  ASAN_OPTIONS=detect_leaks=0:detect_stack_use_after_return=0 sh "$REPO/tools/screens.sh" "$WORK/build-$m" \
    "$WORK/$m" > "$WORK/$m.out" 2>&1 || { echo "$m: screens.sh failed"; tail -3 "$WORK/$m.out"; fail=1; continue; }
  # known and documented (docs/PLAN.md E.3)
  if cat "$WORK/$m"/*.log "$WORK/$m"/listen/*.log | grep "runtime error\|ERROR: AddressSanitizer" \
      | grep -v -e "draw.c:885" -e "model.c:666" -e "cl_parse.c:548" -e "misaligned address" \
          -e "signed integer overflow" -e "left shift of" -e "outside the range of representable values of type 'int'" \
          -e "d_surf.c:141:.*null pointer" -e "model.c:.*index 4 out of bounds for type 'float\[4\]'" \
          -e "model.c:.*applying zero offset to null pointer" -e "draw.c:.*null pointer of type 'qpic_t'" \
          -e "d_part.c:.*outside the range of representable values of type 'unsigned char'" \
          -e "r_part.c:.*out of bounds for type 'vec3_t'" -e "world.c:.*null pointer of type 'edict_t'" \
      | sort | uniq -c | grep .; then
    echo "$m: sanitizer reports (above)"
    fail=1
  fi
  for edh in "$REPO"/data/screen/*.edh "$REPO"/data/screen/listen/*.edh; do
    rel="${edh#"$REPO/data/screen/"}"
    out="$($PY -I "$REPO/tools/edh.py" diff "$edh" "$WORK/$m/$rel" 2>&1)" || { fail=1; echo "$rel $m: $out" | head -2; continue; }
    printf "%-28s release vs %-5s %s\n" "$rel" "$m" "$(echo "$out" | tr '\n' ';' | cut -c1-110)"
  done
done
exit $fail
