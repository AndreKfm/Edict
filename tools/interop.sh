#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# The network's interop test (P6 step 9.4, outside the proof), on Linux (WSL on Windows): builds the reference's
# network build (ref/build.sh NET=1: net_udp.c, the wall clock), copies the port's tree without bin and obj (the
# Windows build's stay apart), and runs InteropTests (the port's client on the reference's server, the reference's
# client on the port's) and UdpGameTests (the port alone over UDP, here through libc's sockets).
# Usage: tools/interop.sh [work dir]
set -eu
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${1:-$HOME/edict-interop}"
mkdir -p "$WORK"
NET=1 sh "$REPO/ref/build.sh" release "$WORK/netref" > /dev/null
rm -rf "$WORK/tree"
mkdir -p "$WORK/tree"
tar -C "$REPO" --exclude=bin --exclude=obj -cf - src tests analyzers Directory.Build.props Directory.Packages.props \
  global.json nuget.config Edict.slnx | tar -C "$WORK/tree" -xf -
EDICT_DATA="$REPO/data/libreq-v0.09-beta" EDICT_NETREF="$WORK/netref/edict_ref" \
  dotnet run --project "$WORK/tree/tests/Edict.Engine.Tests" -c Release -- \
  -class Edict.Engine.Tests.InteropTests -class Edict.Engine.Tests.UdpGameTests
