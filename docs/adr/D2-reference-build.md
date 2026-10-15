# D2: The reference is a 64-bit clang build of the C paths

Status: accepted 2026-10-12 (owner: 64-bit, the string_t fix as hunk copies).

## Context

The ground truth is the pinned WinQuake source tree (`ref/upstream`), built by the project itself. Its results depend
on the compiler and the target: x87 versus SSE arithmetic, FP contraction, the `id386` assembly paths, the width of
`long` and of pointers. WinQuake was a 32-bit program; its `string_t` is an `int` offset from `pr_strings`, which a
64-bit build breaks for strings outside the progs' string table (`docs/PLAN.md` A.1).

## Decision

The reference is built by `ref/build.sh` with clang for x86-64 on Windows (MSVC ABI) and Linux: `id386=0` (the C
paths only), `-O2 -ffp-contract=off -fno-strict-aliasing -fno-strict-float-cast-overflow -march=x86-64-v2` (no fast
math: clang's default), SSE scalar math, gnu89 with `-fcommon`. The patches in `ref/patches` are minimal and each is
categorized; patch 0002 gives the five sites that stored the offset of a string in static storage (PF_ftos,
PF_vtos, PF_etos, SV_SpawnServer's two) copies on the hunk, so every string_t offset fits an int.

## Consequences

- One compiler with documented FP behaviour on both platforms; Windows and Linux builds write the same records
  (R9: `tools/r9-windows.sh` against `tools/r9-linux.sh`, 10 of 10 maps identical on 2026-10-15).
- 64-bit matches the C# process and keeps a current toolchain; the cost is the patches that 64-bit needs: 0002
  (string_t), 0007 (the renderer's pointers through long on LLP64 Windows), 0012 (the ban test's address on LP64).
- The build's other choices that reach the state are pinned where they matter: the build time on the screen
  (SOURCE_DATE_EPOCH), the C library's results (D3), float-to-int overflow (`-fno-strict-float-cast-overflow`).
- The sanitizer builds (`ref/build.sh asan`) run the same code; their reports and how each is handled are listed in
  `docs/PLAN.md` E.3.
