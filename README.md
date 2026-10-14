# Edict
Edict - a frame-exact Quake engine port in pure C#, zero allocations in the game loop

## Playing

The game needs a Quake game directory with an `id1` folder (for example LibreQuake's release). On Windows it opens
a Win32 window, on Linux an X11 one; no packages are involved.

```sh
dotnet build src/Edict.Platform -c Release
dotnet src/Edict.Platform/bin/Release/net10.0/edict.dll -basedir <game directory> +map start
```

The arguments are Quake's (`+map <name>`, `+skill 2`, ...); without a `+map` the game starts `start`. The console
(`` ` ``) takes the commands, the default bindings are the game directory's `default.cfg`. The menu is the one the
reference build uses, a stub: Escape does nothing, `quit` at the console ends the game.
