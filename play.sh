#!/bin/sh
# Copyright (C) 2026 Andre Kaufmann
# SPDX-License-Identifier: GPL-2.0-or-later
# Starts Edict on Linux in one step: fetches LibreQuake's free game data on the first run (tools/fetch-data.sh:
# curl, unzip, sha256sum), builds the game (incrementally: a few seconds once it is built), and starts it with that
# data. In a release package (the program edict beside this script) there is nothing to build. The arguments go to
# the game as they are, e.g.
#   ./play.sh                     the start map
#   ./play.sh +map lq_e0m1        another map
#   ./play.sh -listen 4           a listen server others can join
#   ./play.sh +connect 127.0.0.1  join a server
#   ./play.sh -software           the exact software renderer instead of the GPU's
# Needs an X server (Wayland desktops bring XWayland; WSLg works too) and, except in a release package, the .NET 10
# SDK (https://dot.net).
set -eu
ROOT="$(cd "$(dirname "$0")" && pwd)"
DATA="$ROOT/data/libreq-v0.09-beta"
if [ -x "$ROOT/edict" ] && [ ! -d "$ROOT/src" ]; then
  sh "$ROOT/tools/fetch-data.sh"
  exec "$ROOT/edict" -basedir "$DATA" "$@"
fi
command -v dotnet > /dev/null || { echo "Edict needs the .NET 10 SDK: https://dot.net (then run this again)."; exit 1; }
sh "$ROOT/tools/fetch-data.sh"
dotnet build "$ROOT/src/Edict.Platform" -c Release -v q -nologo
exec dotnet "$ROOT/src/Edict.Platform/bin/Release/net10.0/edict.dll" -basedir "$DATA" "$@"
