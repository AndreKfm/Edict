# Third-party notices

Edict is licensed under the GNU General Public License, version 2 or later (`LICENSE`). It contains or derives from
the following works of others.

## WinQuake (id Software)

Copyright (C) 1996-1997 Id Software, Inc. Licensed under the GNU General Public License, version 2 or later (id's
source release of 1999; `ref/upstream/gnu.txt`).

- `ref/upstream/` holds WinQuake's source tree as released by id Software (https://github.com/id-Software/Quake,
  commit `bf4ac424ce754894ac8f1dae6a3981954bc9852d`), byte for byte; what is left out and why is listed in
  `ref/UPSTREAM.md` (the DirectX SDK and SciTech MGL files, Microsoft's WINSOCK.H, 3Dfx's notes, the prebuilt
  programs and the game data's documents, none of them id's GPL code).
- `ref/patches/` modifies it; `ref/src/edict_sys.c` and `ref/src/edict_vid.c` are derived from its `sys_null.c` and
  `vid_null.c`.
- The C# port (`src/Edict.Engine`, `src/Edict.Net`, `src/Edict.Platform`, `src/Edict.Gl`, `src/Edict.Exact/CAtof.cs`)
  is a translation of that source and carries id's copyright notice in each file.

## fdlibm (Sun Microsystems)

`ref/src/edict_libm.c` and `src/Edict.Exact/CMath.cs` implement sin, cos, tan, atan and atan2 after fdlibm's
algorithms and constants, as musl carries them:

    Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
    Developed at SunPro, a Sun Microsystems, Inc. business.
    Permission to use, copy, modify, and distribute this software is freely granted, provided that this
    notice is preserved.

## The .NET runtime (release packages only)

The release packages hold the game published self-contained: the .NET runtime is part of the program. It is
Copyright (c) .NET Foundation and Contributors, under the MIT license. It comes from the NuGet runtime pack
`Microsoft.NETCore.App.Runtime.<rid>`, whose license file and third-party notices come with each package
(`dotnet-LICENSE.txt`, `dotnet-THIRD-PARTY-NOTICES.txt`, by `tools/runtime-notices.py`); the release workflow stops if
the pack is not under the MIT license (an SDK installation's own license file can carry other terms, as Microsoft's
Windows installer's does, but a self-contained publish does not take the runtime from there). The source tree
contains no part of it.

## LibreQuake (the data not included; two screenshots)

The game data the tests, the reference and the start scripts use is LibreQuake's
(https://github.com/lavenderdotpet/LibreQuake): the assets under the BSD-3-Clause license, the QuakeC code under
the GPL-2.0. Neither the repository nor the release packages contain it: `tools/fetch-data.sh` and the start
scripts download it, with its license documents, into `data/`.

The two screenshots in `docs/images` (`start-gpu.png`, `start-software.png`) show Edict running LibreQuake's start
map, so they show LibreQuake's textures, models and status bar: Copyright © 2019-2023 Contributors to the
LibreQuake project, under the BSD-3-Clause license, whose text is beside them (`docs/images/LICENSE-LibreQuake.txt`).

## Build and test dependencies (not distributed)

Restored by NuGet for building and testing, never part of the game or the packages: xunit.v3 (Apache-2.0, the
tests), BenchmarkDotNet (MIT, the benchmarks), Microsoft.CodeAnalysis.CSharp (MIT, the analyzer's compile-time API). The `[NoAlloc]` analyzer (`analyzers/Edict.NoAlloc`) is copied from
the author's own Prism repository and licensed by him under GPL-2.0-or-later (`docs/adr/D5-noalloc-analyzer-copied.md`).

## Trademarks

Quake is a registered trademark of id Software LLC. Edict is not affiliated with or endorsed by id Software,
ZeniMax Media or Microsoft; the name is used only to say which engine source Edict ports and which game data it
runs.
