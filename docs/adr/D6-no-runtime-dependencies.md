# D6: No runtime dependencies in the engine

Status: accepted 2026-10-12 (the plan's decision table, with the owner).

## Context

The engine's frame has to be bit-exact (D1) and allocation-free (P4). Much of the BCL is neither: formatting and
parsing are culture- and runtime-dependent and allocate, `string` and `List<T>` allocate, `Math.Sin` is the
platform's. Packages add more of the same, plus versions to pin.

## Decision

`Edict.Engine` references nothing but `Edict.Exact`; no NuGet package reaches the engine or the platform at run time
(BenchmarkDotNet, System.IO.Hashing and xunit are test and benchmark dependencies only). In the frame, a BCL member
is used only when the `[NoAlloc]` analyzer accepts it: by its own contract, or by a reviewed entry in
`src/noalloc-allowlist.toml` with the reason; otherwise the port has its own code (the C library's functions in
`Edict.Exact`, C's string handling, the fixed-size buffers).

## Consequences

- The allowlist stays short and reviewable: six members on 2026-10-15 (`double.IsNaN`, `float.IsNaN`, a span
  `double.TryParse` with the invariant culture, `NumberFormatInfo.InvariantInfo`, `Marshal.GetLastPInvokeError` for
  the UDP driver, `Stopwatch.GetTimestamp` for the game's network clock).
- The platform (`src/Edict.Platform`, `src/Edict.Gl`, `src/Edict.Net`) talks to the operating system through
  P/Invoke of its own (Win32, X11, OpenGL, waveOut, ALSA, ws2_32 and libc sockets), no binding package.
- Load paths and console commands may allocate (`[NoAllocUnchecked]` with a reason); the allocation gates
  (`AllocationTests`) measure the frames in between at 0 bytes.
