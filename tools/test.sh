#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The test tiers of docs/TESTING.md, with the wall clock of each part.
# Usage: tools/test.sh quick|full|milestone [mutant work dir]
#   quick      before every commit: the build, the engine tests without the milestone tier (trait Tier=long: the
#              ten-minute runs and the KnownBad classes), the Exact tests
#   full       every test project, both tiers
#   milestone  once per step: full, then every R7 group of tools/mutate.py in the work dir (default: a temporary one)
# EDICT_ORACLE must point to the oracle library (ref/build.sh). While working, run single classes or methods instead
# (dotnet run --project tests/Edict.Engine.Tests -c Release -- -class <name> | -method <name>). R9 (tools/r9-*.sh)
# and the Linux leg are milestone work too; they need reference builds and are run on their own.
set -u
TIER="${1:-quick}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO"
[ -n "${EDICT_ORACLE:-}" ] || { echo "EDICT_ORACLE is not set (docs/TESTING.md)"; exit 2; }
PY="python3"; command -v python3 > /dev/null || PY="python"
fail=0
step() {    # name, command...
  name="$1"; shift
  start=$(date +%s)
  "$@" > "${TMPDIR:-/tmp}/edict-test-$$.log" 2>&1
  rc=$?
  printf "%-16s %4d s  %s\n" "$name" $(( $(date +%s) - start )) \
    "$(grep -E 'Total:|all detected|not detected' "${TMPDIR:-/tmp}/edict-test-$$.log" | tail -1 | sed 's/^ *//')"
  if [ $rc -ne 0 ]; then
    grep -E '\[FAIL\]|SURVIVED|ERROR' "${TMPDIR:-/tmp}/edict-test-$$.log" | head -20
    fail=1
  fi
  rm -f "${TMPDIR:-/tmp}/edict-test-$$.log"
}
engine() { dotnet run --project tests/Edict.Engine.Tests -c Release --no-build -- "$@"; }
case "$TIER" in
  quick|full|milestone) ;;
  *) echo "tier: quick|full|milestone"; exit 2 ;;
esac
step build dotnet build Edict.slnx -c Release
if [ "$TIER" = quick ]; then
  step engine engine -trait- "Tier=long"
else
  step engine engine
fi
step exact dotnet run --project tests/Edict.Exact.Tests -c Release --no-build
[ "$TIER" = quick ] || step analyzers dotnet run --project analyzers/Edict.NoAlloc/Edict.NoAlloc.Analyzers.Tests -c Release --no-build
if [ "$TIER" = milestone ]; then
  WORK="${2:-${TMPDIR:-/tmp}/edict-mutants}"
  step mutants $PY -I tools/mutate.py "$WORK"
fi
exit $fail
