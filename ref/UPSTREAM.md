# ref/upstream — pinned id Software sources

| | |
|---|---|
| Source | https://github.com/id-Software/Quake |
| Commit | `bf4ac424ce754894ac8f1dae6a3981954bc9852d` (2012-01-31, last commit on `master`) |
| Copied | 2026-10-12 |
| License | GPL version 2 or later, see `upstream/gnu.txt` and the file headers |

`upstream/` holds the WinQuake tree plus the repository's `gnu.txt` and `readme.txt`, byte for byte
(`.gitattributes` disables line-ending normalization for it). It is never edited; the reference build works on a
patched copy (see `docs/PLAN.md`, section E.3).

## Not copied

| Path / pattern | Why |
|---|---|
| `QW/`, `qw-qc/` | QuakeWorld is out of scope. |
| `WinQuake/dxsdk/` | Microsoft DirectX SDK headers and libraries, not id's code, not needed for the headless reference. |
| `WinQuake/scitech/` | SciTech MGL headers and a prebuilt library, same reason. |
| `WinQuake/kit/` | Prebuilt `GLQUAKE.EXE` and `OPENGL32.DLL`. |
| `WinQuake/data/` | Shareware game documentation and license texts for the game data, not source code. |
| `*.exe`, `*.lib`, `*.ncb`, `*.opt`, `*.plg`, `*.aps`, `*.mdp`, `*.ico`, `*.gif` | Binaries and Visual C++ IDE artifacts. |

## Verify

`upstream.sha256` lists every copied file. To check the copy against GitHub:

```bash
git clone https://github.com/id-Software/Quake.git /tmp/quake && git -C /tmp/quake checkout bf4ac424ce754894ac8f1dae6a3981954bc9852d
(cd /tmp/quake && sha256sum -c "$OLDPWD/ref/upstream.sha256")
(cd ref/upstream && sha256sum -c ../upstream.sha256)
```

Both checks must report every file as `OK`.
