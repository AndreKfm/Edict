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
for tool in curl sha256sum; do
  command -v "$tool" > /dev/null || { echo "fetch-data.sh needs $tool"; exit 1; }
done
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -sSL -o "$TMP/$ASSET" "$URL"
echo "$SHA256  $TMP/$ASSET" | sha256sum -c -
# unzip, or Python's zipfile where unzip is missing (a minimal Ubuntu, WSL)
if unzip -v > /dev/null 2>&1; then
  unzip -q "$TMP/$ASSET" 'lite/id1/pak0.pak' 'lite/id1/pak1.pak' 'lite/id1/docs/*' -d "$TMP/x"
elif command -v python3 > /dev/null; then
  python3 -I -m zipfile -e "$TMP/$ASSET" "$TMP/x"
else
  echo "fetch-data.sh needs unzip (or python3)"; exit 1
fi
mkdir -p "$DEST/id1"
mv "$TMP/x/lite/id1/pak0.pak" "$TMP/x/lite/id1/pak1.pak" "$TMP/x/lite/id1/docs" "$DEST/id1/"
echo "fetched: $DEST"
