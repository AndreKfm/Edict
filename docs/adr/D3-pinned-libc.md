# D3: The reference's C library results are pinned and ported

Status: accepted 2026-10-12 (the plan's decision table, with the owner).

## Context

Several of WinQuake's state-changing results come from the C library: `sin`, `cos`, `atan2` and `tan` (angles, the
view's field of view), `rand` (every random draw of server and client), `strcmp` (cvar and command lookup, its sign),
`sprintf` (QuakeC's ftos and vtos, NaN and infinity texts) and `atof` (cvar values, NaN bits). Their results differ
between CRTs and CRT versions; IEEE requires correct rounding only for sqrt and the basic operations.

## Decision

The reference force-includes `ref/src/edict_libc.h` (`-include`), which maps `strcmp`, `rand`, `srand`, `sin`, `cos`,
`tan`, `atan`, `atan2`, `sprintf` and `atof` to the harness's own implementations (`edict_libc.c`). Float-to-int
overflow is defined by `-fno-strict-float-cast-overflow` (saturating, NaN to 0). The port uses the same algorithms,
ported one to one in `src/Edict.Exact` (`CMath`, `CRand`, `CFormat`, `CAtof`, `CCast`). `sqrt`, `floor`, `ceil` and
`fabs` are exact in IEEE and stay native.

## Consequences

- The ground truth does not depend on the machine that built the reference; Windows and Linux builds agree (D2).
- What is proven is "WinQuake with a pinned libm", not "WinQuake as shipped in 1996": a 1996 build with its CRT
  would draw other angles and other random numbers. This is stated where the project describes itself.
- The list grew when a gate found a dependency: `tan` was pinned in P6 (the field of view of r_main.c and
  screen.c), after the first screen corpus had been written with the platform's.
- The oracle library exposes the pinned functions, so the port's twins are fuzzed against them directly
  (`tests/Edict.Exact.Tests`).
