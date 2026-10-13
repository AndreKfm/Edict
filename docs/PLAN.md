# Edict — plan (draft 1, 2026-10-12)

Status: draft for review. The WinQuake tree is pinned in `ref/upstream/` (id-Software/Quake `bf4ac42`); statements
with a file:line refer to it and were checked there. Statements without one are from memory and must be checked
before anything depends on them.

## 0. Decisions this plan rests on (each becomes an ADR)

| # | Decision | Alternative rejected | Why |
|---|---|---|---|
| D1 | Exactness beats performance. A faster variant that is not bit-identical on the corpus is not a candidate. | "Fast mode" with tolerance | The hash proof is the project's thesis; a second mode doubles the test matrix. |
| D2 | Reference build: 64-bit clang, `id386=0` (C paths only), `-O2 -ffp-contract=off -fno-fast-math -fno-strict-aliasing -march=x86-64-v2`, SSE scalar math, plus a patch that moves five static strings to the hunk so string_t offsets fit an int (§A.1). | MSVC; 32-bit SSE2 build | clang's FP contraction and excess-precision behaviour is documented and identical on Windows (clang-cl) and Linux. 64-bit keeps a modern toolchain and matches the C# process; the cost is one small extra patch. |
| D3 | `sin`, `cos`, `atan`, `atan2`, `rand`, `strcmp`, `sprintf` (NaN/infinity output) and `atof` (NaN bits) are pinned in the reference (force-included `edict_libc.h`), and float-to-int overflow by `-fno-strict-float-cast-overflow`; all ported 1:1 to C# (`Edict.Exact`). | Use each platform's libm/CRT | Otherwise ground truth depends on the CRT version of the machine that built the reference. sqrt/floor/ceil/fabs are IEEE-exact and stay native. Consequence to state publicly: the reference is "WinQuake + pinned libm", not "WinQuake as shipped in 1996". |
| D4 | Target `net10.0` (LTS). .NET 11 is evaluated as a benchmark variant, not adopted by default (§G). | `net11.0` from day one | .NET 11 is STS, GA Nov 2026; one of its breaking changes touches float→int casts. |
| D5 | The `[NoAlloc]` analyzer is copied as source from Prism (analyzer + annotations + its tests), renamed to `Edict.NoAlloc.*`, with no "HotPath"/"Archfacts" names left: namespace `Edict.NoAlloc`, diagnostic prefix `NA` instead of `HP`, `noalloc-allowlist.toml`, MSBuild property `EdictNoAllocInjectAttributes`. Prism's transitive verifier and benchmarks are not copied. | NuGet / project reference into Prism | Requested; Edict builds from a single checkout and evolves the analyzer independently. Without the verifier, the per-member rule "every callee must carry a matching marker" (NA008) still composes; what is lost is the report of exemption chains, which is acceptable at this scale. |
| D6 | Zero runtime dependencies in `Edict.Engine`. Anything from the BCL that is in the hot path must be allowlisted for the `[NoAlloc]` analyzer or replaced by own code. | Use BCL collections/formatting | BCL formatting, `string`, `List<T>` and `Math.Sin` are either allocating or not bit-pinned. |
| D7 | The port's edicts use the reference's stride (`pr_edict_size`, with the x64 C header size), so entity offsets and field pointers are numerically identical ([adr/D7](adr/D7-edict-stride-as-in-c.md), 2026-10-13). | Free stride, entities compared by index (B.2 before) | Recorded VM calls compare word for word, untyped temporaries included; no translation layer. |

## A. Ground-truth attack — where "frame-exact" degrades to "approximately exact"

### A.1 Reference build traps

- **x87 / excess precision.** Any build that lets the compiler use x87 (32-bit without `-mfpmath=sse`, or `id386=1` with
  `Sys_SetFPCW`/`Sys_LowFPPrecision`) produces results that depend on register spills. Mitigation: SSE scalar only.
  Self-check: identical hashes at `-O0` and `-O2`.
- **id386.** `quakedef.h:64` sets `id386` from `__i386__`, which `quakedef.h:50-51` also defines on Windows for
  `_M_IX86` (`quakeasm.h:29` likewise). Neither is defined for x64 targets, so an x64 build gets the C paths without
  a patch; the build asserts `id386 == 0` anyway.
- **Bitness.** WinQuake stores `string_t` (`typedef int`, `pr_comp.h:24`) as `char* - pr_strings`. Checked in the
  pinned tree: 10 sites. Five compiled ones point into static storage, so on x64 the difference to the
  hunk-allocated `pr_strings` does not fit in an `int`: `pr_cmds.c:934/947/954` (`pr_string_temp`, a static
  array: `PF_ftos`, `PF_vtos`, `PF_etos`), `sv_main.c:1160` (`sv.worldmodel->name`, in the static `mod_known`),
  `sv_main.c:1170` (`sv.name`); `sv_main.c:1172` (`sv.startspot`) is `#ifdef QUAKE2` and not compiled. The other
  four point into the hunk and stay in range: `host_cmd.c:939/1311` (`host_client->name`), `pr_cmds.c:253`
  (`PF_setmodel`), `pr_edict.c:742` (`ED_NewString`). Observed failure without a fix: SEGV in `strcmp` under
  `OP_EQ_S` during `ED_LoadFromFile`.
  Implemented as `ref/patches/0002`: instead of a QuakeSpasm-style string table (which changes every read site),
  the five strings are moved to the hunk (same contents, same lifetime), so every offset fits an `int` and no read
  site changes. Safety net: a sanitizer run over the corpus; a missed site shows up as a garbage string, which the
  content-based string hash (§A.3) catches.
- **FMA contraction.** `-ffp-contract=off` and a baseline ISA without FMA (`-march=x86-64-v2`). Self-check: a test
  expression whose fused and unfused results differ, compiled into the reference, must give the unfused bits.
- **libm / CRT.** See D3. Functions reached from simulation code: `sin/cos` (`AngleVectors` ← `SV_ClientThink`,
  `PF_makevectors`), `atan2` (`vectoyaw`, `PF_vectoyaw`, `PF_vectoangles`), `sqrt` (`VectorNormalize`, `Length`,
  `SV_UserFriction`, `PF_vlen`, `PF_normalize`). Candidate source: musl (MIT, derived from fdlibm).
  Implemented 2026-10-12: `ref/src/edict_libm.c` (sin, cos, atan, atan2; fdlibm algorithms as carried by musl,
  written from the published algorithms, Sun notice kept), redirected by the force-included `edict_libc.h`.
  `sqrt`, `floor`, `ceil`, `fabs` stay with the C library (exact in IEEE). `tan` and `pow` are only called by
  client/renderer code (`r_main.c`, `screen.c`, `view.c`) and are left for P5. Accuracy against glibc
  (`ref/tests/libm_accuracy.c`): max 1 ulp on 10^7 sin/cos arguments in [−4π, 4π], on Quake-style float angles,
  on [−10^5, 10^5], on 5·10^6 atan/atan2 arguments of any magnitude; 82–99.98 % bit-identical to glibc.
  Deviation: |x| ≥ 2^20·π/2 is reduced with `fmod (x, 2π)` instead of `__rem_pio2_large`; deterministic but
  inaccurate there (sin(1e300) differs from glibc completely); no game angle comes near it.
  `rand`: the MSVC LCG (`seed*214013+2531011`, `(seed>>16)&0x7fff`, seed 1; WinQuake never calls `srand`).
  Redirecting the names also stops the compiler from folding `sin (constant)` with its own math library.
- **`strcmp()` magnitude.** `OP_NE_S` stores the return value of `strcmp` as a QuakeC float
  (`pr_exec.c:523`: `c->_float = strcmp (...)`). The standard fixes only the sign: glibc returns the difference of
  the first differing bytes (e.g. 112), MSVC and the sanitizer interceptors return ±1. Found 2026-10-12 because the
  raw globals lane differed between the release and the ASan build (one compiler temporary, −7 vs −1). A static
  scan of LibreQuake's `progs.dat` found 53 `NE_S` statements, all used as truth tests only (heuristic: linear,
  40-statement window), so the gated state was not affected. Pinned anyway: `ref/src/edict_libc.h` (force-included)
  defines `strcmp` as the byte difference of the first differing bytes as `unsigned char`; the C# port does the
  same. After pinning, release and ASan agree in every lane.
- **`rand()`.** The MSVC LCG differs from glibc. Vendor one LCG on both sides. Server-side consumers in the pinned
  tree, all of which the port must call in the same order and number:
  - `_Host_Frame` (`host.c:644`) calls `rand()` once per host frame "to keep the random time dependent", **before**
    `Host_FilterTime`, so also on frames that are filtered out;
  - `PF_random` (`pr_cmds.c:474`): `(rand() & 0x7fff) / ((float)0x7fff)`;
  - `SV_NewChaseDir` (`sv_move.c:321/341`) and `SV_MoveToGoal` (`sv_move.c:421`): monster movement.
  The client side (`cl_main.c`, `cl_parse.c`, `cl_tent.c`, `r_part.c`, `snd_dma.c`, `menu.c`) consumes the same stream
  in a listen server and perturbs server randomness → reference runs are `-dedicated`; client and server get
  separate streams in the client lane. (`net_dgrm.c` calls `rand()` only in commented-out code.)

### A.2 Porting traps (C semantics that C# does not reproduce by default)

| Trap | Concrete sites | Detection | Rule |
|---|---|---|---|
| Double literals promote float expressions | `OP_STATE` (`time + 0.1`), `anglemod` (all-double), `AngleVectors` (`angles[YAW] * (M_PI*2/360)`, then `sin((double)angle)` → float), `SV_RecursiveHullCheck` (`DIST_EPSILON` = `0.03125` double), `SV_AddGravity`, `MSG_ReadAngle` (`* (360.0/256)`), `MSG_ReadCoord` (`* (1.0/8)`) | hash gate | Port literal types exactly; analyzer ED003 flags float expressions mixed with literals in `[Exact]` code unless suffix matches C. |
| float vs double state | `sv.time`, `host_frametime`, `realtime` are `double`; `pr_global_struct->time`, `nextthink`, `freetime`, `ltime` are `float` | hash gate | Never "upgrade" a type. |
| Out-of-range float→int | `anglemod`, `PF_rint` (`(int)(f+0.5)`), `MSG_WriteCoord` (`(int)(f*8)`), `MSG_WriteAngle` (`((int)f*256/360) & 255`: truncates first, then integer arithmetic), `PF_ftos` (`v == (int)v`) | oracle tests; cross-compiler probe | C: undefined. Observed 2026-10-12: for `PF_ftos(inf)` and `PF_ftos(3e9)` clang 18 (Linux) printed `inf` / `3000000000.0`, clang 23 (Windows) printed `-2147483648`, because clang 23 folded `v == (int)v` into an integrality test. Pinned with `-fno-strict-float-cast-overflow`: out-of-range values saturate, NaN gives 0 (measured through the oracle, all 2^32 floats). The historical x87 result (`0x80000000`) cannot be forced with clang. `CCast` implements the saturating semantics explicitly, independent of the .NET cast (changed in .NET 9 and .NET 11). R9 hashes did not change with the flag: the tapes never overflow a cast. |
| Integer `abs()` on floats | `SV_NewChaseDir`: `abs(deltay)>abs(deltax)` with `float deltax, deltay` (`sv_move.c:286/321`); C converts to `int` first (truncation), found by clang `-Wabsolute-value` | function-level test with fractional and out-of-range deltas | Port as `Math.Abs(CCast.I32(delta))`, never `MathF.Abs`. Compile the reference with `-Wabsolute-value` and review every hit. |
| `min/max` macros vs `Math.Min` | bound clamps | unit test with NaN, ±0 | `CMath.Min/Max` = `(a<b)?a:b`; ban `Math(F).Min/Max` in `[Exact]`. |
| Reduction order | `DotProduct` macro is `a0*b0 + a1*b1 + a2*b2`, left-associative; `Length` loop | hash gate | Own `Vec3`; ban `System.Numerics.Vector3`, `Vector128.Dot/Sum` in `[Exact]` (ED001). |
| `atof` vs `Q_atof` | `ED_ParseEpair` uses C `atof` (double, then stored to float: double rounding); `Cvar_Set` uses Quake's `Q_atof`; there is no `PF_stof` in vanilla | oracle tests (edge table, 5·10^5 random decimal, 2·10^5 random hex strings, round trips) on both platforms | `CAtof.Atof` = strtod grammar (white space, sign, decimal, hex, inf, nan) with .NET's correctly rounded parser, then `(float)`; `CAtof.QAtof` operation for operation (`(val*10 + c) - '0'`, one division per decimal place). glibc and UCRT agree on finite values but return different NaN bits for "nan" (`7fc00000` vs `7fffffff` as float): pinned in `edict_libc.c` (quiet NaN with the parsed sign). R7 case: `float.Parse` (single rounding) differs from `(float)strtod` on `1.000000059604644775390625000001`. |
| `ftos`/`vtos` formatting | `PF_ftos`: integer → `"%d"`, else `"%5.1f"` (space padded); `PF_vtos` `"'%5.1f %5.1f %5.1f'"` | oracle tests: edge table, 8·10^5 exact ties and tenths, 2·10^6 random, 2·10^5 vectors, both platforms | `CFormat`: exact integer arithmetic on the float's binary value, ties to even, no allocation. glibc and UCRT agree on finite values (correctly rounded, ties to even) but print NaN differently (`-nan` vs `-nan(ind)`): pinned by `edict_sprintf` (glibc style for NaN and infinities, everything else passed to the C library one conversion at a time). Reference undefined behaviour left in place: `PF_vtos` overflows `pr_string_temp[128]` for components ≥ ~1e37; tests stay below 1e30. .NET's `ToString("F1")` matches C on the tested ties (but allocates). |
| Edict free-list reuse | `ED_Alloc`: reuse if `free && (freetime < 2 \|\| sv.time - freetime > 0.5)`; scan from `maxclients+1` | hash includes free edicts | Port literally; free edicts stay in the hash (their fields are not cleared until reuse). |
| Area list order | `SV_ClipToLinks` keeps the first entity on equal `fraction`; list insertion order decides `trace.ent` and touch order | diagnostic lane: ordered areanode lists | Intrusive index lists with identical insertion points. |
| Wall clock | `Sys_FloatTime`, `host_frametime` | two identical runs must hash identically | Virtual clock; `host_framerate` fixed so `host_frametime` is the same double every frame. |
| NaN operand order | `OP_MUL_V`: clang compiles `a0*b0 + b1*a1`, then `b2*a2 + sum` (clang 18 and 23); with two NaN operands x86 returns the first, so the order picks the NaN | oracle opcode fuzz with NaN payloads (`VmOpcodeTests`) | Mirror the measured order where it differs; every other opcode keeps C's source order (measured). A compiler that commutes differently shows in R9 and in the fuzz. |
| Denormals | MXCSR FTZ/DAZ | startup probe on both sides (`1e-40f * 1f != 0`) | Neither side sets FTZ/DAZ; assert, don't assume. |

### A.3 Hash-scope traps

- `string_t` values are address-dependent → hash string **content**, never offsets.
- Entity references are byte offsets with layout-dependent stride (`EDICT_TO_PROG`) → hash as **index**. The
  stride is `pr_edict_size = progs->entityfields * 4 + sizeof (edict_t) - sizeof(entvars_t)` (`pr_edict.c:1021`),
  not rounded up: on x64 edicts can be misaligned for the pointer members of `edict_t`. The port uses the same
  stride (D7), so offsets are equal too; the hash keeps hashing entities as indices.
- Untyped globals (`OP_ADDRESS` temporaries) contain layout-dependent offsets → the gate covers only `ddef`-typed
  globals; raw globals are a diagnostic lane.
- `edict_t` has padding; never `memcpy` structs into the hash. Walk `progs->fielddefs` and `globaldefs`.
- `SV_WriteClientdataToClient` clears `dmg_take`/`dmg_save`/`fixangle` → the hash hook sits at the end of
  `Host_ServerFrame`, after `SV_SendClientMessages`. Message bytes (sounds, particles, temp entities) get their own lane.

## B. Performance — what is allowed under bit-exactness

### B.1 SIMD policy

| Allowed anywhere | Allowed only in `[ExactSimd(Reason)]` (reviewed) | Forbidden in `[Exact]` |
|---|---|---|
| integer and byte work: `Mod_DecompressVis`, `SV_FatPVS` OR-ing, PVS bit tests, `MSG_*`, `COM_*`, memcpy/memset (`Span.CopyTo/Fill`) | per-lane float `Add/Subtract/Multiply/Divide/Sqrt` where C is per-component too: `VectorAdd`, `VectorMA`, `VectorScale`, clamps in `SV_CheckVelocity` | horizontal reductions (`Sum`, `Dot`), `FusedMultiplyAdd`, `MultiplyAddEstimate`, `Reciprocal(Sqrt)Estimate`, `System.Numerics.Vector3/4` |

Every `[ExactSimd]` method has a scalar twin; the corpus runs both. The renderer and sound mixer (later) get their own
exactness lanes (framebuffer hash, paintbuffer hash) and the same rule.

### B.2 Memory and calling convention

- **Edicts:** one `NativeMemory.AlignedAlloc(MAX_EDICTS * stride, 64)`. Header struct (`free`, `freetime`, leafs as
  `[InlineArray]`, baseline, area links as int indices) + `EntVars` (`StructLayout(Sequential, Pack = 4)`, mirrors
  `progdefs.h`, checked against `PROGHEADER_CRC`) + extra progs fields; the stride is the reference's (D7): the
  header takes exactly the bytes of the x64 C header. Accessors return `ref`:
  `ref Edict E(int i)`, `ref float F(int i, int word)`, `ref Vec3 V(int i, int word)`.
- **QuakeC globals/stack:** `float*` into native memory; `localstack`, `pr_stack` native.
- **Strings:** `byte*`/`ReadOnlySpan<byte>` into the progs block plus an engine-string table. `System.String` only
  in console and tooling.
- **`sizebuf_t`:** struct over native memory; writes through `Span<byte>`.
- **Vec3 passing:** benchmark `in Vec3` vs by-value vs three scalars on the hot functions (`SV_RecursiveHullCheck`,
  `SV_FlyMove`, `ClipVelocity`). On Windows x64 a 12-byte struct is passed by hidden reference anyway; on SysV it is
  passed in two XMM registers. Pick per function by measurement, not by rule.
- **Stack:** scratch buffers via `stackalloc` or `[InlineArray]` locals; no `ArrayPool` (NA012).
- **Console/printing in the loop:** `Con_Printf`, `SV_BroadcastPrintf`, `PF_bprint` must write bytes via an own
  printf into native buffers. This is the most likely source of steady-state allocations.

### B.3 Libraries rewritten instead of used

| BCL / third-party | Replacement | Reason |
|---|---|---|
| `Math.Sin/Cos/Atan2` | `CMath` (musl port, scalar) | bit-pinned, no FCall/CRT dependency; benchmark against `MathF` to report the cost |
| `float.Parse`, `string.Format` | `CAtof`, `QAtof`, `CFormat` (`Span<byte>`) | C semantics, zero allocation |
| `List<T>`, `Dictionary<,>`, `LinkedList<T>` | intrusive index lists, fixed native tables | layout control, no GC objects |
| `Random` | `CRand` (pinned LCG) | must match the reference |
| `FileStream` for PAK | `MemoryMappedFile` at load, raw pointers afterwards | load path only; zero copies |
| `System.IO.Hashing.XxHash3` | **kept** | harness only, already vectorized and allocation-free |

### B.4 Variants to benchmark per module

Only gate-passing variants are measured. Metric: BenchmarkDotNet ns/op with `MemoryDiagnoser` (0 B required) and
`DisassemblyDiagnoser` (no `vfmadd`, bounds checks visible), plus a hard test via
`GC.GetAllocatedBytesForCurrentThread()` delta over 1,000 frames.

| Module | Variants |
|---|---|
| QuakeC VM | (1) literal `switch` on opcode with `Unsafe.Add` on globals; (2) pre-decoded statements with pre-multiplied offsets; (3) `delegate* managed` table per opcode; (4) per-function IL via `DynamicMethod` at load (JIT only, incompatible with Native AOT; must emit `conv.r4` exactly where C rounds). Expectation to verify: the VM is a minority of frame time, so (4) only if profiles justify it. |
| Hull trace | (1) literal recursive `SV_RecursiveHullCheck`; (2) iterative with explicit stack preserving near/far order and early exits |
| Linking | `SV_LinkEdict`/`SV_FindTouchedLeafs`/`SV_TouchLinks` with index lists vs pointer lists |
| Runtime | JIT + Tiered PGO vs Native AOT; `net10.0` vs `net11.0` (§G) |

## C. First hash-gated module

The QuakeC VM is the wrong first cut: `worldspawn` and spawn functions immediately call `precache_*`, `setmodel`,
`setorigin`, `setsize`, `droptofloor`, `walkmove`. An edict hash after spawn needs most of the ~80 builtins, which
pull in `world.c`, `sv_move.c`, `sv_phys.c`, `sv_main.c`, cvar/cmd — the whole server before the first green gate.

**Cut 1: PAK + BSP loader + hull tracing.** `Mod_LoadBrushModel` (planes, nodes, leafs, clipnodes, submodels,
`Mod_MakeHull0`), `SV_HullForBox`, `SV_HullForEntity`, `SV_RecursiveHullCheck`, `SV_HullPointContents`,
`SV_ClipMoveToEntity` against the world.
Gate: a C driver linked to the reference `model.c` + `world.c` generates per LibreQuake map 100k deterministic traces
(seeded xorshift; start/end in world bounds; hull classes point/player/large; 10 % start==end; 10 % axis-aligned)
and dumps canonical `trace_t` records (flags, `fraction`, `endpos`, plane normal/dist as raw bits, entity index).
C# must match **every record**; the diff reports the first mismatching record.
Why: it exercises `DIST_EPSILON` promotion and `plane->type < 3` fast paths, it is the hottest server path (so
benchmarking starts immediately), and it proves the harness pipeline end to end at small scope.

State 2026-10-12: **gate met** (P1 kill rule not reached: 100 % on the first full run). Implemented:
- Reference: `ref/src/edict_traces.c` (`-edicttraces <file> <count> <seed>`, called from `edict_sys.c` after the
  first frame with a running map; no id file touched), `tools/traces.sh` (10 maps × 100,000 traces, seed = list
  position). The `.etr` file holds the loader dump (planes, nodes, leafs, clipnodes, hull 0, every brush model's
  hulls and bounds) and per trace the inputs, the complete `trace_t` and `SV_HullPointContents` of the world hulls
  at start and end. Inputs are stored, so the port reads them (R2). Generator: splitmix64 instead of xorshift (the
  tape generator's). The 10 corpora are byte-identical from release, `-O0`, ASan/UBSan and Windows (R9).
- Port: `src/Edict.Engine` — `PakFile`/`QFileSystem` (memory-mapped, `COM_FindFile` order and `COM_CheckRegistered`),
  `BrushLoader` (collision lumps into one `NativeArena`), `HullTrace` (`SV_HullPointContents`, literal and
  iterative `SV_RecursiveHullCheck`), `ClipWorld` (box hull, `SV_HullForEntity`, `SV_ClipMoveToEntity<TCheck>`, the
  variant a static-abstract type parameter). `src/Edict.Harness` reads the corpus. The trace path is
  `[NoAlloc(Constraints.All)]`; `SV_HullForEntity`'s two Sys_Errors (game state) throw preallocated exceptions under
  `Allow.Throw`.
- Gate (`tests/Edict.Engine.Tests`, 70 tests, Windows and Linux): loader dump record by record, all 10^6 traces with
  each of the three variants, hull contents.
- Deviations from C, none observable on valid data: hull trees are validated once at load (node range, plane
  index) instead of C's per-visit `bad node number` check, so the trace code needs no error path; out-of-range
  `modelindex` counts as "no model" (C reads out of bounds); lumps and PAK entries outside the file are load errors.
  Model brush data loaded so far: collision only (no textures, faces, lighting); `Mod_SetParent` comes with the
  renderer. Item boxes (`maps/b_*.bsp`) are brush models of their own in `sv.models`.
- Corpus coverage (R7, 8 variants): the first corpus (uniform points, rejection-sampled into open space) missed three
  known-bad variants. The backup loop at the end of `SV_RecursiveHullCheck` ran 5 times in 10^6 traces. The
  generator now chains traces as `SV_FlyMove` does (start at the last `endpos`, slide, push into the plane or the
  corner of the last two planes, drop), starts some traces on sloped planes, and picks 4 % of the records by
  importance sampling with a private copy of `SV_RecursiveHullCheck` that only reports whether the loop moved the
  crossing point (~2,700 such records per map; results still come from id's function). Now detected: `frac` as
  double, unary minus instead of `vec3_origin - normal`, `t1 <= 0` for the side, `frac -= 0.1f`, no backup loop,
  `midf = frac`, reordered dot product.
  Not bad, shown rather than assumed: `DIST_EPSILON` as a float. `t1 ∓ 1/32` moves `t1` toward zero, which is exact
  in float for 1/32 ≤ |t1| < 2^19; for |t1| < 1/32 it can round, but numerator and denominator then have opposite
  signs and `frac` is clamped to 0 either way (measured: 1.5·10^6 crossings, 2 different unclamped values, both
  clamped). It stays as an equivalence test.
- Benchmark (`bench/Edict.Benchmarks`, BenchmarkDotNet 0.15.8, Ryzen 9 3900X, .NET 10.0.9, whole corpus per
  operation set, 0 B allocated in every variant):

  | Variant | `start` | `lq_e0m3` |
  |---|---|---|
  | Recursive (literal port, scalar twin) | 233.8 ns/trace | 272.0 |
  | Iterative (explicit stack) | 1.05× | 0.98× |
  | **Packed** (default): clip node and plane in one 32-byte `PackedNode`, built at load | **0.89×** | **0.88×** |

  Where the time goes (measured): ~11–13 node visits plus ~8 point-contents visits per trace, 71–95 % of planes
  axial (`p[type] - dist`, one subtraction), 1.4–1.5 crossings. A visit is ~45 cycles, dominated by the dependent
  load clipnode → plane and branch mispredicts, not by its 2–12 FP operations; the packed layout removes the
  dependent load (memory 168 vs 65 KiB on `start`, still in L2).
  SIMD, measured and dropped: t1/t2 of non-axial planes in two `Vector128` lanes and `mid` in three, each lane in C
  order (§B.1), bit-identical on all 10^6 traces, but 1.03× on both maps alone and 0.93× instead of 0.89× on top of
  the packed layout: the shuffles cost more than the 6 µops saved on 5–29 % of visits. Batching traces across lanes
  would be exact but the tree paths diverge after a few nodes. Disassembly of every variant (Tier1 and FullOpts):
  no `vfmadd*`; RyuJIT does not contract `a*b+c`. SIMD in the simulation is left to the integer work of §B.1
  (`Mod_DecompressVis`, `SV_FatPVS`).
  The C reference on the same inputs (2026-10-13, `edict_ref -edicttracebench <file> <reps>`: checks every record
  against the file, then times 50 passes; Windows build, clang 23, the reference flags of D2): 256 ns/trace on
  `start`, 303 on `lq_e0m3`. The literal C# port is 0.91×/0.90× of that, the packed default 0.81×/0.79×. Part of
  the difference is C's per-visit node range check (validated once at load in the port); the reference is built
  for exactness (x86-64-v2, no LTO), not for speed.
  Also measured and not taken: a 24-byte `PackedNode` (no pad; nodes straddle cache lines) 1.01×/1.02× of the
  32-byte one; `Vec3` passed by value instead of `in` (§B.2) 1.05×/1.05×: on Windows x64 a 12-byte struct goes
  by hidden reference anyway, by value only adds the copy. Open: `net11.0` (no .NET 11 SDK installed yet), SysV.

**Cut 2:** `PR_LoadProgs`, VM, builtins, `ED_LoadFromFile`, `SV_SpawnServer`, areanodes. Gate: frame-0 hash
(edicts + typed globals after spawn, before `SV_Physics`) per map.
Steps (agreed 2026-10-13), each with its own gate: (1) a census of what spawn and the first frames reach, from a
VM record mode; (2) `PR_LoadProgs` and `PR_ExecuteProgram` with all 66 opcodes, gated by replaying recorded calls in
isolation (level 2, globals and edicts compared before and after); (3) builtins in census order, edicts,
`ED_LoadFromFile`, areanodes and linking, each builtin against its recorded calls; (4) `SV_SpawnServer` and the
frame-0 hash per map.
Step 1 done (2026-10-13): `-edictcapture <file>` (`ref/src/edict_capture.c`, hooks in `ref/patches/0003`), census by
`tools/ecp.py census` over the 10 R9 maps × 4,000 tape frames. 48 of the 79 table entries are reached, 17 during
spawn, 39 during frames; 770 QuakeC functions. Spawn: `precache_sound` (8,047 calls), `precache_model` (3,515),
`setmodel`, `setsize`, `makestatic`/`precache_*2` aliases, `random`, `remove`, `ambientsound`, `fabs`, `setorigin`,
`lightstyle`, `makevectors`, `spawn`, `droptofloor`, `cvar_set`. Frames add above all `checkclient` (287,740),
`cvar` (200,154), `makevectors`, `random`, `movetogoal`, `vlen`, `traceline`, `normalize`, `vectoyaw`, the
`WriteByte`/`WriteCoord` message builtins, `find`, `sound`, `walkmove`, `changeyaw`, and 20 rarer ones. Never
reached on the corpus (30 of entries 1–78; not needed for the gates until a tape reaches them): 15 `PF_Fixme` slots,
`break`, `error`, `objerror`, `coredump`, `traceon`, `traceoff`, `eprint`, `nextent`, `WriteChar`, `WriteShort`,
`WriteLong`, `WriteAngle`, `WriteString`, `WriteEntity`, `precache_file` (#68, #77), `setspawnparms`. LibreQuake's
progs.dat carries no names for its builtins; the tool names them from `pr_builtin[]` as compiled (without QUAKE2).
Step 2 done (2026-10-13): `src/Edict.Engine/Progs` — `Progs` (`PR_LoadProgs`, CRC-16 as `crc.c`), `QcVm`
(`PR_ExecuteProgram`, `PR_EnterFunction`, `PR_LeaveFunction`, all 66 opcodes; builtins, strings and `sv.state`
through a struct host type parameter; `[NoAlloc]`, errors as preallocated exceptions), `ProgDefs` (offsets
generated from `progdefs.q1`). Edicts use the reference's stride (D7; the header is 108 bytes on x64, the same with
the MSVC and the System V ABI — `EdictOracle_EdictHeaderBytes` against the captures).
Two gates, both on Windows and Linux:
- Level 2, capture replay (`VmReplayTests`): capture version 2 records at every engine entry, builtin call,
  builtin return and return to the engine what changed in the VM's memory (globals and fields of the edicts in
  use), against a shadow copy. The replay applies what the engine and the builtins changed and requires the port's
  VM to change exactly what the reference's VM changed, word for word; builtins do not run, nested QuakeC (touch,
  think from builtins) is replayed too. `tools/captures.sh`: 10 maps × spawn + 4,000 tape frames, 105 MB; replay
  6.7 s for all maps, 15,000–91,000 engine entries and 28,000–89,000 builtin calls per map, 4.6·10^10 words
  compared. Green on the first run. The enter hook moved in front of `PR_EnterFunction`, which already writes
  globals (parameter copies); hashes unchanged.
- Level 1, opcode fuzz (`VmOpcodeTests`): `EdictOracle_VmRun` runs `PR_ExecuteProgram` itself on statements, globals,
  strings and edicts the test provides; every opcode except the calls, on all pairs of 32 edge values (signed
  zeros, NaN payloads, infinities, denormals, the int boundaries), on random words and game-like coordinates, on
  empty strings at nonzero offsets, and `OP_STATE` on edge, random and frame times; memory compared byte for byte.
  Found one difference: the NaN choice of `OP_MUL_V` (row "NaN operand order" in A.2), fixed.
- R7 (`KnownBadVmTests`, 10 mutants of a copy of the VM): the replay alone detected 4 (no local save, one-word
  return, `<` for `<=`, sign-only `NE_S`); the game never produces -0.0 in a condition, NaN in a comparison, an empty
  string at a nonzero offset, nor a frame equal to its new value with the other sign of zero. The fuzz detects all 10.
  `nextthink` in float instead of double differs only at tiny times (2.9·10^-7 s): on 4,000 frame times from a run
  0 differences, because within one binade the rounding window lies at the same place relative to the float grid.
**Cut 3:** `SV_Physics*`, `sv_user.c`, `SV_ReadClientMessage`, tape replay. Gate: per-frame hash, ≥ 3 maps × 10 min.

## D. Effort at 7 h/week (calendar weeks)

Includes ~25 % overhead for the multi-model workflow (generation, gating, ranking per module).

| Phase | Weeks | Pre-registered kill / stop rule |
|---|---|---|
| P0 harness (reference build, hash, tapes, diff tool, CI, analyzer vendoring) | 5–8 | Stop at 9 weeks if the reference is not self-deterministic (two runs, `-O0`/`-O2`, Windows/Linux identical). |
| P1 loader + trace gate | 4–7 | Corpus not 100 % within 3 weeks of the first full run → stop and write up why. |
| P2 VM + builtins + spawn (frame-0 gate) | 9–14 | First-divergence entity count must fall every 2 weeks. |
| **P3 physics + tape replay, full frame hash** | 7–12 | **Most likely to kill the project.** Log the first-divergence frame per map weekly; no progress for 3 consecutive weeks, or 8 weeks without all maps clean → stop. No relaxation to "approximately exact". |
| P4 zero-alloc + performance variants + all gates on `Host_Frame` | 4–7 | Time-box only. |
| P5 client (demo replay, `cl_entities` hash) | 9–14 | as P3 |
| P6 renderer, sound, networking | 20–40 | outside the proof |

Server frame-exact and benchmarked (P0–P4): roughly **29–48 weeks**, i.e. 7–11 months.

Why P3 kills: divergences surface late (frame 4,000 of a tape), compound (one ULP in `origin` changes the next
trace), and the localization tools from P0 are only as good as the field-level diff. P0 must therefore deliver the
per-entity, per-field first-divergence report, not only a frame hash.

## E. Phase-0 harness

### E.1 Hash

- Algorithm: XXH64 (seed 0). C side: own implementation from the specification (`ref/src/edict_xxh64.c`, no
  external header); C# side: `System.IO.Hashing.XxHash64`. Checked 2026-10-12: lengths 0–300 (every code path),
  301/301 identical, including the published value for empty input (`ef46db3751d8e999`). XXH3 was the first
  choice; XXH64 avoids vendoring `xxhash.h`, and speed is irrelevant in the harness. Not a cryptographic
  property; only collision resistance across a corpus matters.
- Implemented in `ref/src/edict_hash.c` (format in its header comment). Deviation from the list below: entity
  fields are hashed word by word with a type map built from `fielddefs` (vectors as three floats; untyped words
  as raw bits), so duplicate defs such as `origin` and `origin_x` do not enter twice.
- Canonical little-endian stream per entity: `u32 index, u8 free, u32 freetime bits, u8 num_leafs,
  i16 leafnums[num_leafs], baseline (as bits)`, then every field in `fielddefs` order, typed:
  `ev_float`/`ev_vector` raw bits; `ev_string` content bytes + NUL; `ev_entity` as index; `ev_function`/`ev_field`
  raw int; `ev_pointer` skipped.
- Frame hash = hash over header (`sv.time` bits, `num_edicts`, `sv.state`, `paused`, `serverflags`, lightstyles)
  and the `(index, entity hash)` pairs.
- Lanes per frame: `edicts` (gate), `globals_typed` (gate), `globals_raw`, `arealinks`, `messages` (diagnostic;
  promoted to gate when stable).

### E.2 Dump and diff

- `.edh` binary: header (magic, version, progs CRC, map, flags) + one record per frame (~60 B); `--per-entity` adds
  8 B × `num_edicts`; `--dump N-M` writes full canonical streams for a frame window.
- `edict-diff a.edh b.edh` (C#): first diverging frame → lane → entity → field, both values as hex and decimal
  (.NET 11 hex-float formatting would help here; on .NET 10 format the bits manually).
- Text output is derived from the binary by the C# tool; the C side never formats text (keeps the patch small).

### E.3 Keeping the C patch minimal and auditable

- `ref/upstream/` pristine id-Software/Quake WinQuake tree at a pinned commit (vendored, GPLv2).
- `ref/patches/NNNN-*.patch`: a numbered series, one concern per patch, each with a header stating category
  (undefined behaviour / 64-bit / harness hook), the failure that required it, and why results do not change.
  `ref/build.sh` copies upstream, applies the series, adds `ref/src/*.c`, compiles. No patched tree is committed.
- New files only, no logic in hooks: `edict_hash.c` (lanes, serialization, virtual clock, argv), `net_tape.c`
  (net driver modeled on `net_loop.c` that feeds recorded client messages), `edict_libm.c/.h`, `edict_rand.c`.
- Hook lines under `#ifdef EDICT_HASH`: end of `Host_ServerFrame`, main loop (`Host_Frame(1.0f/72)` until tape end),
  `Sys_FloatTime`, net driver table, `SV_ReadClientMessage` (record mode). Target: ≤ 30 changed code lines in id
  files for harness hooks, checked by CI. Undefined-behaviour and 64-bit patches are outside that budget but each
  needs evidence that results do not change.
- `0003` (2026-10-13, harness hook): five capture macros and two braces in `PR_ExecuteProgram`, 8 of the 30 hook
  lines. All 10 R9 maps × 4,000 frames hash identically with and without the patch and with and without
  `-edictcapture`; release and ASan write byte-identical capture files.
- State 2026-10-12: `0001` (`COM_FileBase` read before its argument; old/new compared on 21 paths, 0 differences),
  `0002` (string_t offsets, see §A.1). With both, the release build loads LibreQuake `start`, runs the QuakeC spawn
  functions and keeps running headless (client + server, `vid_null`).
- Sanitizer findings not patched (yet), with reasons:
  - misaligned `edict_t` accesses (`pr_edict.c`, link functions in `common.c`): `pr_edict_size` is not rounded;
    x86-64 tolerates it, and rounding would change `EDICT_TO_PROG` values; the asan mode will use
    `-fno-sanitize=alignment` once this is recorded in an ADR;
  - `model.c:666` writes `vecs[0][j]` for `j < 8` across a `float[2][4]` (contiguous, works; the port reads 8 floats);
  - `draw.c:885` null `draw_disc` before `Draw_Init`, `cl_parse.c:548` `1<<31`: client side, revisit in P5;
  - `R_RenderView` aborts under ASan with "called without enough stack": its 10,000-byte stack-depth check trips on
    ASan's larger frames; sanitizer runs of the server use dedicated mode (no renderer).
- Headless: dedicated mode with the null drivers `vid_null.c`, `snd_null.c`, `cd_null.c`, `in_null.c`, `sys_null.c`
  (all five present in the pinned tree). `sys_null.c`'s `Sys_FloatTime` already returns a virtual clock, but it
  advances by 0.1 per *call*, not per frame; the harness replaces it.

### E.4 Inputs

- ~~Tapes recorded with a listen-server build~~ (revised 2026-10-12, see below). Original idea: per frame
  `(host_framecount, length, bytes)` of client 0's messages, including the signon string commands and `clc_move`
  with byte-quantized angles. Replay in dedicated mode through `net_tape.c`; C# implements the same driver.
- Revision: the reference is headless (no video, no input), so nobody can play it, and a listen-server recording
  would mix client `rand()` calls into the server stream (§A.1). Instead, tapes are **generated**: a seeded
  generator writes a tape file of client messages (signon string commands, then one `clc_move` per frame with
  forward/side/up moves, view angles, buttons and impulses following a scripted random walk). Input is open-loop
  (it does not react to the game), so the same file drives the C reference and the C# port, and the dedicated
  replay is the ground truth. Human-played tapes can be added later from any source port that can log
  `clc_move`; they are not required for the proof.
- Corpus: LibreQuake maps + own tapes only. No id data in the repository or in CI.
- R9 without input (2026-10-12, `tools/r9-linux.sh`): 10 maps × 2,000 frames, release twice, `-O2` vs `-O0`,
  release vs ASan/UBSan: identical in all lanes. Coverage is weak: without a player, 0 entities change on 7 of 10
  maps (3 on `start`, 5 on `lq_e0m6`, 2 on `dev`); the lanes differ per frame mainly through `sv.time` and the
  globals. R9 is repeated with generated tapes before it counts for the P0 exit gate.
  Cause found: `-dedicated` without a number means 8 client slots, `Host_FindMaxClients` then sets `deathmatch 1`
  (`host.c:197`), and the QuakeC removes all monsters in deathmatch. Runs now use `-dedicated 1` (single player).
- Tapes implemented (2026-10-12): `ref/src/edict_tape.c` (net driver, replaces `net_none.c`; format in its
  header), `tools/tapegen.py` (interim generator, splitmix64 so a C# port reproduces tapes bit for bit). Per frame
  a tape holds reliable messages followed by at most one unreliable one, because `SV_ReadClientMessage` stops
  reading after the first unreliable message.
- R9 with tapes (2026-10-12): 10 maps × 4,000 frames (seed = map number), single player; release twice,
  `-O2` vs `-O0`, release vs ASan/UBSan: identical in all lanes, including the raw globals lane. 48–169 entities
  change per map; the player entity changes in 3,996 of 3,999 frames on `start`. On `dev` the tape walks the
  player into an exit to `lq_e4m1`, which the lite package lacks; the server stops after 1,490 frames (game
  behaviour, not a harness fault; that map needs another seed). Windows leg: see E.6.

### E.5 Differential testing

The rules (R1–R10) and the three levels (function / module call tapes / lockstep) are in
[TESTING.md](TESTING.md). P0 builds the oracle library (`libedict_ref.so`) from the same objects as the
reference executable, so level-1 tests exist before the first C# module.

### E.6 Toolchain (state 2026-10-12)

- Reference and oracle: Linux, WSL Ubuntu 24.04, clang 18.1.3, make (CMake is not installed in WSL). Same
  platform as CI.
- Windows: LLVM 23.1.3 installed 2026-10-12 (`winget install LLVM.LLVM`); `clang.exe` finds Visual Studio and
  the Windows SDK itself. `ref/build.sh` runs in Git Bash with `CC`, `LIBS=""`, `EXE=.exe` and
  `EXTRA_CFLAGS=-DWINDED` (id's switch for its Windows dedicated server: `VID_LockBuffer` becomes the same empty
  macro as on Linux). `menu.c` is replaced on both platforms by `ref/src/edict_menu.c` (stubs): its `_WIN32` code
  needs DirectDraw and SciTech headers. Effect checked: Linux hashes unchanged in the gated lanes; on `lq_e0m8` one
  raw-globals word moves by 576 bytes (a string offset into the hunk, which no longer holds the menu's commands).
- R9 Windows vs Linux (2026-10-12, `tools/r9-windows.sh`): 10 maps × 4,000 frames, Windows (clang 23, MSVC ABI,
  UCRT) vs Linux (clang 18, glibc): identical in all lanes including raw globals. Tapes generated on Windows are
  byte-identical to the Linux ones.
- .NET: `global.json` pins 10.0.100 with `latestFeature`: Windows resolves 10.0.203, WSL 10.0.112.
- Spike (2026-10-12): the pristine tree builds headless on x64 with clang 18 (`sys_null`, `vid_null`, `snd_null`,
  `cd_null`, `in_null`, `net_loop`, `net_none`, `net_vcr` + the common and software-renderer files) after three
  build-level changes, none in id's files: `-std=gnu89` (implicit function declarations are errors in C99+),
  `-fcommon` (tentative definitions in headers, e.g. `sadjust` in `d_local.h`), and a glue file defining
  `isDedicated`, which only the platform `sys_*.c` files define. Hazard warnings to keep enabled and review:
  `-Wpointer-to-int-cast` (3 hits: `world.c:287/824` are offset computations in `EDICT_FROM_AREA`, harmless;
  `d_surf.c:141` renderer), `-Wabsolute-value` (the `sv_move.c` row in §A.2), `-Wshift-overflow` (`r_edge.c:700`,
  renderer). Data: LibreQuake v0.09-beta `lite.zip` via `tools/fetch-data.sh` (pinned SHA-256); all maps BSP29, `progs.dat` version 6, CRC 5927, as WinQuake requires.

### E.7 P0 exit gate

Identical `.edh` across two runs, `-O0`/`-O2`, Windows/Linux; `ftos`/`atof` fuzz matches; `float-cast-overflow`
sanitizer silent on all tapes; analyzer vendored and building; the R7 known-bad variants for level 1 all fail.

State 2026-10-12: **met.** Identical across two runs, `-O0`/`-O2`, ASan and Windows/Linux (10 maps × 4,000 frames,
every map runs all frames; `dev` uses seed 14); `ftos`/`vtos`/`atof`/`Q_atof` fuzz: the C# implementations match the
oracle on both platforms; R7: 11 known-bad variants, all detected (`tests/Edict.Exact.Tests`, 34 tests); analyzer copied
and building (68/68 tests), and it gates the C# code (NA008 on `double.IsNaN`, `double.TryParse`, answered by
`src/noalloc-allowlist.toml` with reasons). `float-cast-overflow` was silent on the corpus before casts were pinned; since
`-fno-strict-float-cast-overflow` overflow is defined (saturating), and the unchanged R9 hashes show the tapes never hit it.
Three R7 candidates were not bad and were replaced (reordered inner sin polynomial: bit-identical on 10^7 arguments;
`ToString("F1")`: correctly rounded; reordered `Q_atof` addition: same float). Lesson recorded in TESTING.md: whether a
change alters bits is measured, not argued.

## F. Repository layout and CI

```
Edict/
  global.json                  SDK 10.0.2xx pinned; .NET 11 only via the runtime variant lane
  Directory.Build.props        net10.0, LangVersion latest, Nullable, AllowUnsafeBlocks, TreatWarningsAsErrors,
                               Deterministic, InvariantGlobalization, TieredPGO, ConcurrentGarbageCollection=false,
                               AnalysisLevel latest-all
  Directory.Packages.props     BenchmarkDotNet, System.IO.Hashing, xunit.v3
  analyzers/Edict.NoAlloc/     copied from Prism and renamed (D5): Analyzers, Annotations, Analyzers.Tests
  analyzers/Edict.Analyzers/   [Exact]/[ExactSimd]; ED001 API denylist, ED002 casts, ED003 literal types, ED004 SIMD review
  src/Edict.Exact/             CMath, CCast, CAtof/QAtof, CFormat, CRand — all [NoAlloc] and [Exact]
  src/Edict.Engine/            one assembly: Common, Mathlib, Progs, World, Server, Net, Host (later Client, Render, Sound)
  src/Edict.Harness/           run tape → .edh, run trace corpus, edict-diff
  tests/Edict.Tests/           per-record corpus equality, frame-hash equality, allocation-delta assertions
  bench/Edict.Benchmarks/      BDN variants per module
  ref/                         upstream/ (pinned id-Software tree), patched/, edict.patch, edict_*.c, CMake
  corpus/                      golden .edh, tapes, trace corpora (LFS above 50 MB)
  gen/<module>/<model>/        multi-model candidates; gate script ranks: exact → analyzer clean → 0 B → ns/op
  docs/adr/                    one ADR per decision in §0 and per module
```

Analyzer wiring: `<ProjectReference … OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` to
`Edict.NoAlloc.Analyzers` and `Edict.Analyzers`. `Host_Frame` carries
`[NoAlloc(Constraints.NoLock | Constraints.NoBlock | Constraints.NoThrow | Constraints.NoDynamic)]`; map load uses
`Allow` with `Reason`. Every callee in the frame must carry a matching marker or a reviewed allowlist entry (NA008),
so the contract composes member by member.

Copy procedure (one commit, reviewable): copy the three projects from
`Prism/analyzers/hotpath/` at a recorded Prism commit, rename projects, namespaces, diagnostic IDs (`HP` → `NA`),
the attribute-recognition constants, the injected attribute source, the allowlist file name and the MSBuild
property; replace the license headers (§H); run the copied tests green before any Edict code uses the attribute.
The commit message records the Prism source commit.

CI jobs: (1) reference build on Linux clang, regenerate `.edh`, compare to committed golden (reference drift fails);
(2) build with NA + ED analyzers as errors; (3) corpus equality, frame hashes, allocation delta 0 over 1,000 frames;
(4) BDN short run, hard fail on `Allocated > 0`, performance regressions informational on shared runners and hard
against a committed baseline on the local benchmark machine; (5) weekly sanitizer lane on the reference.

## G. .NET 11 — does it help?

State on 2026-10-12: .NET 11 RC1 (`11.0.100-rc.1.26425.128`) shipped 2026-09-08 with a go-live license; GA expected at
.NET Conf, 10–12 Nov 2026; **STS** (24 months). .NET 10 is LTS until 2028-11. C# 15 is the default for `net11.0`.

| Change | Effect on Edict |
|---|---|
| More bounds-check elimination, loop cloning for `!=` loops, check coalescing | Positive for the VM loop and BSP traversal if indexing is span-based; little effect where we already use pointers. |
| Escape analysis: `Nullable<T>` boxes, address-taken locals, more devirtualization | Low: the design already avoids boxes, delegates and virtual calls. Closures are still heap-allocated. |
| Faster `[InlineArray]` struct copies | Medium: edict header, leaf lists, baselines. |
| `Vector*` `Zip/Unzip/Concat`; AVX dot product lowered to mul+permute+add | Low for sim (reductions are banned anyway). Relevant for the renderer later. |
| AVX10/APX/AVX-512 additions, x86-64-v2 minimum | Low. |
| **float→byte/sbyte/short/ushort/char now saturating (NaN → 0)** | **Breaking for exactness** if raw casts were used. Neutral with the `CCast` rule (§A.2), which is needed on .NET 10 anyway. |
| C# 15 "unsafe v2" (preview, opt-in): pointers/`stackalloc`/`fixed` without `unsafe` context | Do not opt in while it is preview; it changes what `unsafe` on a member means. |
| Hex float formatting/parsing | Useful for the diff tool and golden text dumps. |
| Native AOT: faster interface/GVM dispatch | Low; we use neither in the hot path. |
| BenchmarkDotNet | `net11.0` support only in 0.16.0-preview (stable 0.15.8 does not list it — (verify)). |

Unconfirmed and to be measured, not assumed: whether RyuJIT ever contracts `a*b+c` without an explicit FMA call
(believed no; a unit test plus `DisassemblyDiagnoser` check on every SDK bump), and whether `Math.Sin` uses the CRT
(irrelevant once `CMath` is used).

Recommendation: stay on `net10.0`. From P1 on, run the benchmark project against both runtimes
(`--runtimes net10.0 net11.0`) and the corpus on both. Switch only if the corpus is green on both and the gain on
`Host_Frame` exceeds 5 % — expected single-digit percent, not a deciding factor. Revisit with .NET 12 (LTS, Nov 2027).

## H. Licensing

- Edict is GPLv2 (derivative of WinQuake). No relicensing, no dual license.
- The `[NoAlloc]` analyzer originates in Prism (Apache-2.0, which the FSF considers incompatible with GPLv2; its
  attribute injector puts source into Edict's assemblies). All Prism commits have a single author, who holds the
  copyright and licenses the copy in Edict under GPLv2. Each copied file gets a GPLv2 header; the copy commit
  states the origin. Prism's own license is unaffected. Precondition checked on 2026-10-12: 158 commits in Prism,
  all by the same author; re-check if Prism gains contributors before the copy is made.
- musl libm subset: MIT. xxHash: BSD-2. Both GPLv2-compatible.

## Owner decisions (2026-10-12)

- Reference: 64-bit; string_t fix implemented as hunk copies (D2, §A.1).
- Copy only the `[NoAlloc]` analyzer, renamed, nothing named HotPath (D5).
- Analyzer copy licensed GPLv2 by its copyright holder (§H).
- Plan and ADRs in English.
