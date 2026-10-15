#!/bin/sh
# Fetches the free LibreQuake game data the reference and the port run on (never committed).
# Assets: BSD-3-Clause; QuakeC/progs.dat: GPL-2.0 (see data/<release>/id1/docs).
set -eu
RELEASE=v0.09-beta
ASSET=lite.zip
SHA256=428e736b2f01d953e09a08c60bee975bdc4a0ac2219e97fa095c8af41754da83
URL="https://github.com/lavenderdotpet/LibreQuake/releases/download/$RELEASE/$ASSET"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/data/libreq-$RELEASE"
if [ -f "$DEST/id1/pak0.pak" ]; then echo "present: $DEST"; exit 0; fi
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -sSL -o "$TMP/$ASSET" "$URL"
echo "$SHA256  $TMP/$ASSET" | sha256sum -c -
mkdir -p "$DEST"
unzip -q "$TMP/$ASSET" 'lite/id1/pak0.pak' 'lite/id1/pak1.pak' 'lite/id1/docs/*' -d "$TMP/x"
mv "$TMP/x/lite/id1" "$DEST/id1"
echo "fetched: $DEST"
