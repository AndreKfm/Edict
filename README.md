# Edict
Edict - a frame-exact Quake engine port in pure C#, zero allocations in the game loop

## Playing

The game needs a Quake game directory with an `id1` folder (for example LibreQuake's release). On Windows it opens
a Win32 window, on Linux an X11 one; no packages are involved.

```sh
dotnet build src/Edict.Platform -c Release
dotnet src/Edict.Platform/bin/Release/net10.0/edict.dll -basedir <game directory> +map start
```

The view is drawn by the GPU renderer (OpenGL 3.3 core, GLQuake's look) by default; `-software`, or `r_gpu 0` at
the console, shows the exact software renderer, the one the port is proven against. The arguments are Quake's
(`+map <name>`, `+skill 2`, ...); without a `+map` the game starts `start`. The console
(`` ` ``) takes the commands, the default bindings are the game directory's `default.cfg`. The menu is the one the
reference build uses, a stub: Escape does nothing, `quit` at the console ends the game. The sound is Quake's mixer,
ported as exactly as the renderer, played through waveOut on Windows and ALSA on Linux; `-nosound` turns it off.

The free game data the tests and the reference run on is LibreQuake's: `sh tools/fetch-data.sh` puts it in
`data/libreq-v0.09-beta`, which then serves as the game directory.

## Multiplayer

Quake's network over UDP (port 26000 by default). A listen server, where you play and others join:

```sh
dotnet src/Edict.Platform/bin/Release/net10.0/edict.dll -basedir <game directory> -listen 4 +map start
```

A dedicated server, without a window: its console reads stdin and prints to stdout (`quit` ends it):

```sh
dotnet src/Edict.Platform/bin/Release/net10.0/edict.dll -basedir <game directory> -dedicated 4 +map start
```

To join, `connect <address>` at the console or `+connect <address>` on the command line. As in WinQuake, Quake's
parser splits a command at a colon, so a port other than the default goes in quotes (`connect "192.168.1.5:26001"`)
or with `-port <n>`. `-port <n>` also sets the port a server listens on; `-nolan` leaves the network out.

## Tests

`sh tools/test.sh quick` (before a commit) and `sh tools/test.sh full`, with `EDICT_ORACLE` pointing to the oracle
library `ref/build.sh` builds from the C reference; docs/TESTING.md has the tiers, the corpora and the checks
around them.
