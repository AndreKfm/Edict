# Differential testing — rules

Binding for every ported module and for every model candidate in the multi-model benchmark. A change to these
rules needs an ADR. "Reference" means the patched WinQuake C build (`ref/`), "port" the C# code.

## The rules

| # | Rule |
|---|---|
| R1 | **Bits, not tolerances.** Every comparison is on raw bits (`float`/`double` via `BitConverter.SingleToInt32Bits`/`DoubleToInt64Bits`, structs field by field). No epsilon anywhere. Exception (D8, `docs/adr/D8-nan-bits-are-not-state.md`): a NaN produced by float arithmetic only has to be NaN; copies keep NaN bits and are compared by them. |
| R2 | **One input, two consumers.** An input is generated or recorded once, from a seed or a capture, and fed unchanged to both sides. Neither side generates its own inputs. |
| R3 | **One reference configuration.** The oracle library, the reference executable and the golden files are built from the same object files with the same flags (`docs/PLAN.md` D2). A test against a differently built oracle does not count. |
| R4 | **Every ported function with numeric or state semantics gets a differential test before it is merged**, on four input sets: (a) an edge table (±0, smallest denormal, largest finite, ±inf, quiet NaN, 2^31 − 1/2^31 boundaries, x.5 values, negative values that print as `-0.0`); (b) seeded random inputs, ≥ 10^6 per function; (c) inputs captured from real runs (§ Capture); (d) exhaustive for unary 32-bit functions (all 2^32 `float` inputs) where one pass takes less than 10 minutes. |
| R5 | **First divergence, fully reported.** A failure prints function, input (hex and decimal), both outputs, seed and record index. The failing input is reduced to the smallest reproducer and appended to `corpus/regress/`, which runs on every build, forever. |
| R6 | **State is compared before and after.** For stateful code (`rand`, `ED_Alloc`, area linking, VM calls) both sides start from the same state snapshot; the test compares the output and the complete state afterwards. |
| R7 | **The harness must catch known bugs.** A fixed set of deliberately wrong C# variants (one ULP off, reordered sum, `Math.Min` instead of the C macro, saturating cast, missing `rand()` call, `<=` instead of `<` in `SV_ClipToLinks`) must each fail the suite. If one passes, the suite is blind there and is fixed before anything else. |
| R8 | **No ranking without 100 %.** Multi-model candidates run the identical suite; throughput and code quality are compared only among candidates that pass every record. |
| R9 | **The reference is tested too.** The reference must reproduce itself: two runs, `-O0` vs `-O2`, and (once a Windows clang is available) Windows vs Linux give identical outputs. A reference that disagrees with itself invalidates every result built on it. |
| R10 | **Failures are not waived.** No test is skipped or marked "known difference" without an ADR that names the cause; the default answer to a mismatch is that the port is wrong. |

## Three levels

| Level | What is compared | How | When |
|---|---|---|---|
| 1. Function | pure and small stateful functions: `CMath` (sin/cos/atan2), `anglemod`, `Q_atof`/`atof`, `PF_ftos`/`PF_vtos` formatting, `MSG_Write*/Read*`, `DotProduct`, `VectorNormalize`, `BoxOnPlaneSide`, the LCG | **In-process**: the reference C files are compiled into an oracle library (`libedict_ref.so`) with C exports; the C# test calls oracle and port with the same input and compares the bits | from P0 on, before the module exists in C# |
| 2. Module | `SV_Move`/hull traces, `PR_ExecuteProgram`, `SV_LinkEdict`, `ED_Alloc`, message parsing | **Call tapes**: the reference records every call at the module boundary (arguments, relevant state before, result, state after) in record mode; the port replays each call in isolation from the recorded state | as soon as a module is ported, long before the full server runs |
| 3. System | whole server frames | **Lockstep**: oracle and port run in the same process, frame by frame, with the same tape input; after each frame both produce the hash lanes (`docs/PLAN.md` E.1); on the first differing lane both sides dump the full canonical state and the diff names entity and field. Golden `.edh` files from the reference executable serve CI runs without the oracle. | from P2 (frame 0) and P3 (all frames) |

Level 1 and 2 are where most errors are found and where they are cheap to localize; level 3 proves the whole.
Lockstep in one process also means both sides see the same MXCSR state and the same CPU.

## Capture (input set c)

The reference has a record mode (`-edictcapture <file>`) that writes, per instrumented function, its arguments and
results as binary records while a tape plays. The capture points are macros in `ref/src/edict_capture.h`, one line
per point in id's code (`ref/patches/0003`); they test one int and do nothing without the option. Captured records
feed level 1 (inputs) and level 2 (call tapes). Version 1 records VM entries, QuakeC calls and builtin calls with
raw argument and return words (the census of `docs/PLAN.md` section C, cut 2); state snapshots follow with the VM.

## Oracle library

- Built from the same object files as the reference executable (R3), as a shared library with `extern "C"`
  exports: per-function entry points for level 1, and `EdictRef_Init`, `EdictRef_LoadMap`, `EdictRef_Frame`,
  `EdictRef_HashLanes`, `EdictRef_DumpState` for level 3.
- WinQuake keeps its state in globals and leaves through `Sys_Error`/`longjmp`: one oracle instance per process.
  Tests that use the oracle run in a non-parallel collection; `Sys_Error` is redirected to a callback that fails
  the test instead of exiting.
- C# calls the oracle through `[LibraryImport]`/function pointers with blittable arguments only, so the call itself
  adds no conversion that could alter a value.

## What a test result says

A green suite says: on these inputs, port and reference produce identical bits. It does not say the port is
equivalent on inputs outside the corpus. That is why the corpus grows (R5), why inputs include exhaustive and
captured sets (R4), and why the harness is checked against known-bad variants (R7).

## Running (state 2026-10-12)

```bash
# Linux (WSL): reference executable and oracle library from the same objects
sh ref/build.sh release ~/edict-ref
# Windows (Git Bash, LLVM clang): same, as .exe/.dll
CC="/c/Program Files/LLVM/bin/clang.exe" LIBS="" EXE=".exe" EXTRA_CFLAGS="-DWINDED -D_CRT_SECURE_NO_WARNINGS" sh ref/build.sh release <dir>
# level-1 differential tests (EDICT_ORACLE: libedict_oracle.so or edict_oracle.dll)
EDICT_ORACLE=<path> dotnet run --project tests/Edict.Exact.Tests -c Release
# system level, R9: tools/r9-linux.sh, then tools/r9-windows.sh <dir with the Linux .edh files>
# phase-1 gate (module level): trace corpus from the reference, then the port against it
sh tools/traces.sh ~/edict-ref data/traces
dotnet run --project tests/Edict.Engine.Tests -c Release
# cut 2 (VM): captures from the reference, then replay and opcode fuzz (the fuzz needs EDICT_ORACLE)
sh tools/captures.sh ~/edict-ref data/captures
EDICT_ORACLE=<path> dotnet run --project tests/Edict.Engine.Tests -c Release
# benchmarks (gate-passing variants only, R8)
dotnet run --project bench/Edict.Benchmarks -c Release -- --filter "*HullTrace*"
```

The engine tests read the corpus from `EDICT_TRACES`, the VM captures from `EDICT_CAPTURES` and the game data from
`EDICT_DATA` (defaults: `data/traces`, `data/captures` and `data/libreq-v0.09-beta` in the checkout); with
`--artifacts-path` outside the checkout (WSL) set all three. Tests that call the oracle share one serial
collection (`OracleCollection`).

In WSL, build the C# side with `--artifacts-path` outside the checkout, so that Windows and Linux builds do not share
`obj/`.

Level 1 so far (`tests/Edict.Exact.Tests`, 34 tests, identical results on Windows and Linux): `CMath` sin, cos,
atan, atan2 (edge tables, 10^6-scale seeded sets, Quake-style float angles and vectors), `CCast` (edge tables and
all 2^32 float bit patterns, saturating semantics), `CRand` (10^6 calls from seed 1, five other seeds),
`CString.Strcmp` (word table, 10^6 random pairs), `CFormat` ftos/vtos (edge table, 8·10^5 exact ties and tenths,
2·10^6 random floats, 2·10^5 vectors), `CAtof` atof and Q_atof (edge table, 5·10^5 random decimal and 2·10^5 random
hex strings, round trips of float values). R7 variants (`KnownBadTests`, 11, all detected): runtime `Math.Sin` and
`Math.Atan2`, one ulp off, a distributed product in the sin kernel, the unpinned hardware cast, `System.Random`, a
skipped `rand()` call, a sign-only strcmp, ftos rounding ties away from zero, `float.Parse` instead of
`(float)strtod`, Q_atof ported as atof.

Three first candidates turned out not to be bad, and were replaced after measuring: the sin kernel's inner polynomial
summed in another order (bit-identical on 10^7 arguments), .NET's `ToString("F1")` (correctly rounded, ties to even,
like the C libraries), and Q_atof with a reordered addition (differences stay below what the float conversion keeps).
Not every reordering changes bits; which optimizations are permitted is measured, not argued.

Level 2 so far (`tests/Edict.Engine.Tests`, hull traces: 70 tests, identical results on Windows and Linux): the hull trace corpus
(10 maps × 10^5 traces, `docs/PLAN.md` section C), the loader dump, R7 with 7 known-bad copies of
`SV_RecursiveHullCheck`. The corpus is a level-2 tape in all but name: it records calls at the module boundary with
their inputs and results, and the port replays each call in isolation. A first corpus of uniform random traces passed
the port but also three known-bad variants; how the generator was changed is in `docs/PLAN.md` section C. A corpus
whose inputs a game would rarely produce at random needs the same check: R7 decides whether it is good enough, not
its size.

VM (cut 2, step 2; `docs/PLAN.md` section C): level 1 is the opcode fuzz against `PR_ExecuteProgram` in the oracle,
level 2 the replay of VM captures, 94 engine tests in all. The captures replayed green on the first run but saw
only 4 of 10 known-bad VM variants: a game run produces the common values, not the edges. The fuzz was added for
that, and found a real difference the replay could not see (which NaN `OP_MUL_V` returns). Both levels are needed.
Builtins (cut 2, step 3a): the replay runs the ported builtins inside the captured game (level 2) and the builtin
fuzz runs them against `pr_builtins[num] ()` in the oracle (level 1). As with the VM, the game alone was not
enough: cvar values in a game are integers, so `atof` instead of `Q_atof` passed the replay.
World and movement (cut 2, step 3c): engine code has no seam to swap a known-bad copy in from a test, so R7 there is
done at source level by `tools/mutate.py`: it copies the tree once, applies one textual change per mutant, builds and
runs the given test classes, and reports each mutant as detected, survived or a known blind spot (listed in the tool
with its reason; one that becomes detected is reported so that it can be moved out). A survived mutant, a mutant
whose text no longer matches or one that does not build makes the run fail.

```sh
EDICT_ORACLE=<path> python3 -I tools/mutate.py <work dir> [link] [move] [check] [spawn] [clients] [physics] [messages]
```

The game alone was again not enough for linking, movement and the PVS: R7 found blind spots where the tapes never go
(missiles against monsters, owners, ties between targets, notarget clients). Level 1 got a world for that: the oracle
loads the map itself (`EdictOracle_LoadWorld`) and `WorldFuzzTests` drives the reference's `SV_LinkEdict`,
`SV_Move`, `Mod_PointInLeaf`, `Mod_LeafPVS`, `PF_checkclient`, `PF_aim` and `SV_NewChaseDir` on it and the port's on
the same edicts. It reports how often it reached the outcomes that matter (traces that hit an entity, checkclient
finding a client, aim turning to a target, a chasing monster moving), so that a green run can be judged.
The frame-0 gate (cut 2, step 4) compares state hashes, so the hash itself is checked first, on the reference's
own state: at each spawn record of a capture the replay hashes its memory with the port's `StateHash` and requires
the reference's spawn record from the `.edh` of the same run, lane by lane and entity by entity.
Then the gate (`SpawnGateTests`): the port spawns each map, and each spawn-only variant of `tools/captures.sh`
(other skills, deathmatch, coop), from the inputs the capture recorded, and must hash as the reference's spawn
record. The tapes are single player on skill 1, so the variants are what reaches the skill and deathmatch filters
and the cvar adjustments; the maps' entity texts are well formed, so `SpawnParseTests` puts the parser on what they
never contain (high bytes, comments, escapes, keys with trailing spaces, runs of spaces in vectors).

Cut 3: the capture replay runs the port's server frames in three widening modes — the frame's start (clients),
the whole frame (with physics and the frame's end), and finally the host loop alone (`LoopGateTests`), where nothing
of the reference is in the loop but the tape and the state at the first spawn. Each mode localizes a difference to
the record where it appears; the last only says in which frame. R7 at source level covers the client code
(`clients`) and the physics (`physics`); the physics fuzz (`PhysicsFuzzTests`) is what makes the second group work:
the tapes never reach speeds beyond sv_maxvelocity, gravity fields, corpses on pushers or a fourth bump.
The messages lane puts the server's output in the same gate: the loop gate compares what the port sent the client
since each record with the reference's, and the `.emg` of the run (`-edictmessages`) turns a different hash into the
message and byte where it differs. Its level 1 is `MessageFuzzTests` (SV_WriteClientdataToMessage and
SV_WriteEntitiesToClient on the oracle's world): a game rarely puts an entity exactly a tenth of a unit off its
baseline, a client on the 8-unit edge of a node plane, 256 entities in view or a datagram at its 16-byte limit. R7
group `messages`; the loop gate is also part of `clients` and `physics` now, whose blind spots were messages.

```sh
sh tools/longruns.sh ~/edict-ref data/long      # the ten-minute runs of the cut-3 gate (hashes and messages)
sh tools/demos.sh ~/edict-ref data/client       # P5: demos of every run's messages and synthetic ones, the client's hashes
sh tools/screens.sh ~/edict-ref data/screen     # P6: the demos and listen runs with the screen, a record per present
sh tools/r9-screen.sh ~/r9-screen               # P6 R9: -O0 and ASan/UBSan against data/screen (a 2nd argument: one build)
SOUND=1 sh ref/build.sh release ~/snd           # P6 step 8: the sound build (snd_dma.c, snd_mix.c, snd_mem.c, edict_snd.c)
EXTRA="-edict2d +showram 0" sh tools/screens.sh ~/snd ~/os    # its corpus (copy ~/os to data/sound: short paths)
CORPUS=sound SOUND=1 EXTRA="-edict2d +showram 0" sh tools/r9-screen.sh ~/r9s   # R9 of the sound build
python3 -I tools/screen.py diff <a.dump> <b.dump> out.png   # -edictscreendump images: where two differ, side by side
```

Run time (Windows, Ryzen 9 3900X, 2026-10-13, wall clock, after cut 3): about 146 s for a full run — build 5 s,
engine tests 129 s (255 tests), exact tests 8 s, analyzer tests 4 s. The largest engine classes: `LoopGateTests` 84 s
(62 s of it the three ten-minute runs), `VmReplayTests` 81 s (three modes on 10 maps), `KnownBadVmTests` 25 s,
`TraceGateTests` 13 s, `BuiltinTests` 12 s; `SpawnGateTests` 6 s, the fuzzes 2 s each. The builtin mutants (`KnownBadBuiltinTests`) fuzz only the builtins
they change (0.2 s; 132 s when each ran the whole fuzz). `tools/mutate.py` is not part of the suite (one build per
mutant; the group `spawn` with its 21 mutants takes 216 s).

Run time after the messages lane and the tiers (same machine, 2026-10-13): `tools/test.sh quick` 59 s — build 7 s,
engine tests 44 s (219 tests; before the split the whole engine suite took 131 s, its critical path the replay modes
and the long runs in one class each), exact tests 8 s. The three long runs, now in parallel, 25 s (62 s one after
another). `tools/mutate.py move check physics messages` (87 mutants, stopping at the first failing test) 631 s.

P5 (the client): the gate is `ClientGateTests`, the port's client on every demo of `data/client` against the
reference's records. The corpus's demos are what a server sends; `tools/demogen.py` adds synthetic ones for what it
does not (R7 group `client` showed which), among them the edges of CL_LerpPoint that only the signon frame can reach.
The listen server's gate is `ListenGateTests`: the port's host with its local client on every run of `data/listen`
(`sh tools/listen.sh ~/edict-ref data/listen`; `ONLY="<run> ..."` for some), the server's spawn and frame records
and the client's records after every frame; a run's console commands are in `<run>.cmd`. R7 group `listen`.

P6, step 3: `ScreenGateTests` starts the port's client as the reference starts (the arguments from the cmdline cvar,
or from a listen run's `<run>.args`) and compares every present of data/screen2d (frame buffer and palette, in
order), every client record and, in the listen runs, every server record. For localizing: `EDICT_SCREENRUN=<run>`
runs one, `EDICT_PORTDUMP=<file>` writes the port's first differing frame in -edictscreendump's format (compare it
with the reference's dump: `tools/screen.py diff`), `EDICT_CLIENTDUMP=<file>` (a reference -edictclientdump) names
the first differing byte of a client lane, `EDICT_PORTSTREAM=<file>` writes the port's entities lane, and
`EDICT_SCREENTRACE=1` prints the view's inputs for the first frames. R7 group `screen`.

P6, step 4: `WorldGateTests` runs the same comparisons with the 3D view drawn on data/screen4, the corpus with the
entities and the gun off (`EXTRA="+r_drawentities 0 +r_drawviewmodel 0" sh tools/screens.sh ~/edict-ref
data/screen4`; `EDICT_SCREEN4` for another directory): the world, the surface cache with its light, the sky, the
water's turbulence and warp, the particles, and the RAM icon, which the surface cache's thrashing shows. The
allocation gate plays this corpus. R7 group `world`; the group `screen` runs both classes.

P6, step 5: `FullFrameGateTests` on data/screen, the whole frame (`sh tools/screens.sh ~/edict-ref data/screen`;
`EDICT_SCREEN` for another directory): the brush entities, the alias models, the sprites, the gun. The reference's
cmdline keeps 255 characters, and the demo runs take their arguments from it: generate the corpora with short
paths to the reference and the output (a long one cuts the cmdline, and the gate reports "may be cut"). The
allocation gate plays this corpus now. `tools/demogen.py` seed 7 is the renderer's profile (models LibreQuake does
not reach, written next to the demos: every sprite type, a sprite frame group, alias skin and frame groups; 250
entities around the view). R7 group `frame`; the group `world` runs both classes.

P6, step 6: no corpus records input, so the platform's side is tested by the port's behaviour, not against the
reference: `GameInputTests` (keys through `Screen.KeyEvent`, a fake `IMouse`, the console, the chat line, quit) and
`AllocationTests.Game_frames_allocate_nothing`. An unattended run of the program itself:
`edict -basedir <dir> -edictframes 300 -edictshot shot.bmp` (Windows; on Linux with an X server, WSLg included).

P6, step 7: `GpuRendererTests` plays four demos of data/screen with both renderers (the exact one, then the GPU one
on a hidden window's OpenGL 3.3 context into an offscreen framebuffer) and requires every world surface and alias
model the exact renderer drew to be drawn by the GPU renderer, and no GL error; skipped without a context.
`EDICT_GPUSHOTS=<dir>` saves every 250th frame of both (`EDICT_GPUFRAME=<n>` one more) for a look.

P6, step 8: `SoundGateTests` on data/sound, the corpus of the reference's sound build (`EDICT_SOUND` for another
directory): the screen gate's comparisons (with the 3D view cleared) and every sound record, the DMA buffer after
each host frame with paintedtime and soundtime. `tools/edh.py diff` compares sound records too. The allocation gate
plays the sound corpus (`Sound_frames_allocate_nothing`), and `Game_frames_allocate_nothing` runs with and without
the sound. The platform's output has no reference: `edict -edictwav <file.wav> -edictframes 600 +map dev` writes
what it would play (dev has sound where the player stands; start has none). `tools/demogen.py` seed 8 (in
`tools/demos.sh`; recorded for data/sound with `ONLY=fuzz_8`) is the sound's profile. R7 group `sound`.

P6, step 9: `MultiGateTests` on data/multi (`sh tools/multis.sh ~/edict-ref data/multi`; `EDICT_MULTI` for another
directory), the dedicated server with four slots and the clients of a version 2 tape, every frame and spawn record
and the messages to each client. The listen gates cover the local client's side (data/listen and data/screen/listen:
start-client-cmds, start-playdemo, lq_e0m1-disconnect, start-argv). `DatagramFuzzTests` is net_dgrm.c's level 1: it needs the
oracle of version 9 (built with `ref/build.sh`: net_dgrm.c and `ref/src/edict_memnet.c` are in it).
`tools/r9-net.sh` is R9 for the multiplayer corpus and the new listen runs (release and ASan by default). R7 groups
`multi`, `listennet`, `datagram`. Outside the proof (step 9.4): `UdpGameTests` runs a dedicated server and a client
over UDP on 127.0.0.1 (quick tier, about 2 s); `InteropTests` needs the reference's network build (EDICT_NETREF,
`ref/build.sh` with NET=1, Linux) and is skipped without it: `sh tools/interop.sh` in WSL builds it, copies the tree
without bin and obj and runs both classes there (about 35 s). R7 group `udp`.

### Test tiers (2026-10-13)

Not every change needs every test. Three tiers, chosen by what a run has to answer:

| Tier | When | What |
|---|---|---|
| targeted | while working | the class, method or map the change touches (`-class`, `-method`, a single corpus map through `EDICT_LONG`) |
| quick | before every commit | `tools/test.sh quick`: build, engine tests without the trait `Tier=long`, Exact tests |
| milestone | after a large step, and only when a narrower run cannot answer the question | `tools/test.sh full` (the long tier: the ten-minute runs, the `KnownBad*` classes; the analyzer tests), `tools/mutate.py`, R9 (`tools/r9-*.sh`), the Linux leg |

For R7 after a gate gained a test, `tools/mutate.py --known` runs only the known blind spots (a mutant a gate detected
stays detected when the gate gains a test); `--only <text>` runs single mutants. A mutant's tests stop at their first
failure, and the tool names the test that failed. The fuzzes take their seeds from XXH64 of the map name: until
2026-10-13 they used `string.GetHashCode`, which .NET randomizes per process, so each run fuzzed other inputs (found
when `CheckStuck: 17 up` was detected in one run and not in the next).

Parallelism: the classes run in parallel, the tests of one class one after another. The three replay modes and the
three long runs are classes of their own for that reason; the oracle collection runs serially in itself, in parallel
with the rest (no other class calls the oracle).
