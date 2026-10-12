# Differential testing — rules

Binding for every ported module and for every model candidate in the multi-model benchmark. A change to these
rules needs an ADR. "Reference" means the patched WinQuake C build (`ref/`), "port" the C# code.

## The rules

| # | Rule |
|---|---|
| R1 | **Bits, not tolerances.** Every comparison is on raw bits (`float`/`double` via `BitConverter.SingleToInt32Bits`/`DoubleToInt64Bits`, structs field by field). No epsilon anywhere. NaN results compare by payload too. |
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

The reference has a record mode (`-edictcapture <dir>`, under `#ifdef EDICT_HASH`) that writes, per instrumented
function, its arguments and results as fixed-size binary records while a tape plays. The capture points are a list
in `ref/edict_capture.h`, one macro line per function; adding a point does not touch id's code beyond that line.
Captured records feed level 1 (inputs) and level 2 (call tapes).

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
# benchmarks (gate-passing variants only, R8)
dotnet run --project bench/Edict.Benchmarks -c Release -- --filter "*HullTrace*"
```

The engine tests read the corpus from `EDICT_TRACES` and the game data from `EDICT_DATA` (defaults: `data/traces` and
`data/libreq-v0.09-beta` in the checkout); with `--artifacts-path` outside the checkout (WSL) set both.

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

Level 2 so far (`tests/Edict.Engine.Tests`, 70 tests, identical results on Windows and Linux): the hull trace corpus
(10 maps × 10^5 traces, `docs/PLAN.md` section C), the loader dump, R7 with 7 known-bad copies of
`SV_RecursiveHullCheck`. The corpus is a level-2 tape in all but name: it records calls at the module boundary with
their inputs and results, and the port replays each call in isolation. A first corpus of uniform random traces passed
the port but also three known-bad variants; how the generator was changed is in `docs/PLAN.md` section C. A corpus
whose inputs a game would rarely produce at random needs the same check: R7 decides whether it is good enough, not
its size.
