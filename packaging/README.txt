Edict - a frame-exact C# port of the WinQuake engine source
=============================================================

This package holds the game as one program; it needs no .NET installed.

Start
-----

Windows: double-click play.cmd (or run .\play.ps1 in PowerShell).
Linux:   ./play.sh   (needs an X server; Wayland desktops have XWayland)

On the first start the script fetches LibreQuake's free game data (about 50 MB,
its SHA-256 checked) into the folder "data" beside it; then the game starts on the
map "start". Arguments go to the game, for example

  play.cmd +map lq_e0m1          another map
  play.cmd -software             the exact software renderer instead of the GPU's
  play.cmd -nosound              without sound

In the game, ` (the key left of 1) opens the console; "quit" there ends the game.
The menu is a stub, as in the reference build, so Escape does nothing. The default
key bindings are LibreQuake's: WASD to move, the mouse to look, the left button to
fire, Space or the right button to jump, Shift to run, Tab for the scores.

To play with your own copy of Quake instead, start the program directly with its
game directory (the folder that holds id1):

  edict -basedir <game directory> +map start

Multiplayer
-----------

A listen server (you play, others join), port 26000:   play.cmd -listen 4
A dedicated server, no window (stdin/stdout):          play.cmd -dedicated 4 +map start
Joining:                                                play.cmd +connect 192.168.1.5
Another port: -port <n>, or connect "host:port" in quotes at the console.

What Edict is
-------------

A port of WinQuake's GPL source release to C#, shown to compute exactly what the C
program computes, frame for frame, by state hashes against a C reference build:
the server, the client, the software renderer, the sound and the exact part of the
network. The source, the method and the results: https://github.com/AndreKfm/Edict

License and trademarks
----------------------

Edict is licensed under the GNU General Public License, version 2 or later
(LICENSE), as is the WinQuake source release it ports (Copyright (C) 1996-1997
Id Software, Inc.); its source is at the address above. THIRD-PARTY-NOTICES.md
lists what else it contains or derives from. The program includes the .NET runtime
(Copyright (c) .NET Foundation and Contributors, MIT license); its license and its
third-party notices are dotnet-LICENSE.txt and dotnet-THIRD-PARTY-NOTICES.txt. It contains no game data. LibreQuake's data, which the start script fetches,
is BSD-3-Clause (the assets) and GPL-2.0 (QuakeC); its licenses come with it in
data/libreq-v0.09-beta/id1/docs.

Quake is a registered trademark of id Software LLC. Edict is not affiliated with or
endorsed by id Software, ZeniMax Media or Microsoft; the name is used only to say
which engine source Edict ports and which game data it runs.
