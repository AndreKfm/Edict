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
| D8 | The bits of NaN results of float arithmetic are not state: such a result must be NaN on both sides; copies keep all bits; hash and NaN text are sign- and payload-free ([adr/D8](adr/D8-nan-bits-are-not-state.md), 2026-10-13). | Reproduce clang's operand order per expression with explicit NaN rules | LLVM leaves NaN payload and sign unspecified; clang and RyuJIT commute, vectorize and fold freely. |

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
| NaN results | `OP_MUL_V`, the sums of squares in `PF_normalize`/`PF_vlen`/`PF_vectoangles`, `AngleVectors`: with NaN operands clang's (and RyuJIT's) operand order and folds decide which NaN comes out | opcode and builtin fuzz with NaN payloads | Not state (D8): NaN results compare as a class; copies bit for bit. Port in C's source order. |
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
  Found one difference: which NaN `OP_MUL_V` returns for two NaN operands; with the builtin fuzz this became D8.
- R7 (`KnownBadVmTests`, 10 mutants of a copy of the VM): the replay alone detected 4 (no local save, one-word
  return, `<` for `<=`, sign-only `NE_S`); the game never produces -0.0 in a condition, NaN in a comparison, an empty
  string at a nonzero offset, nor a frame equal to its new value with the other sign of zero. The fuzz detects all 10.
  `nextthink` in float instead of double differs only at tiny times (2.9·10^-7 s): on 4,000 frame times from a run
  0 differences, because within one binade the rounding window lies at the same place relative to the float grid.
Step 3 in three parts: 3a builtins on VM memory, rand and cvars; 3b edicts (`spawn`, `remove`, `find`,
`findradius`); 3c the world (areanodes, linking, `setorigin`/`setsize`/`setmodel`, `traceline`, `droptofloor`,
`walkmove`, `movetogoal`, `checkclient`), with engine-side links recorded as events so area lists form in the same
order.
Step 3a done (2026-10-13): `src/Edict.Engine/Server` — `Server` (rand stream, `sv.time`, state, string temp,
precache lists), `Cvars` (`Cvar_VariableValue` as Q_atof of the string on every call, `Cvar_Set` into fixed
buffers), `Builtins`: makevectors, random, normalize, vlen, vectoyaw, vectoangles, fabs, floor, ceil, rint, ftos,
vtos, changeyaw, cvar, cvar_set, precache_sound/model/file (and their `*2` aliases); `Mathlib.Anglemod`,
`AngleVectors`. The print builtins and `lightstyle` stay replayed: their effect is message output, not ported yet.
Capture version 3 adds the rand seed and `sv.time` at entries and builtin returns, the cvars and the
`pr_string_temp` offset per map. The replay is hybrid: ported builtins run as the port's code, and their changes,
the rand seed afterwards and the text they return must match; the others are replayed. About 285,000 ported
builtin calls on the 10 maps, all matching. Level 1: `EdictOracle_Builtin` runs `pr_builtins[num] ()` on test
memory; the builtin fuzz (edge and random vectors and floats, angles on and off multiples of 45°, any rand seed,
self's yaw fields, cvar values against the reference's Q_atof) found only NaN-bit differences, which led to D8.
R7: 10 builtin mutants (random by 32768, vectoyaw in float, rint with a float sum, normalize in double, runtime
sin, anglemod in float, a strict bound in changeyaw, atof for cvar, precache without return value, ftos always
"%5.1f"), all detected; first by the fuzz for 9, by the replay for the precache one. Cvar via atof was blind on
game values (the cvars are integers) until the fuzz got cvar values with decimals, exponents and hex.
Step 3b done (2026-10-13): edicts. `Server`: `EdictHeader` (free, freetime; the port's own layout inside the
reference's 108 bytes), `EDICT_NUM`, `NUM_FOR_EDICT`, `ED_Alloc` (first free edict after the clients that was
freed in the first two seconds or more than half a second ago, else a new one; `MAX_EDICTS` compared as in C),
`ED_Free` (unlinking from the world comes with 3c); builtins spawn, remove, find, findradius (the centre in
double: `(mins + maxs)*0.5`), nextent, and makestatic's freeing (its signon output comes with messages).
Capture version 4: `svs.maxclients` per map, the free flag and freetime of every edict whose header changed in
every diff, and a new map record when the same map spawns again: five of the ten tape runs restart their map
after the player dies, which the capture had not noticed (the shadow then compared against the old spawn). The
builtin fuzz sets reference headers through `EdictOracle_EdictHeader` and puts ED_Alloc's tests on their edges
(freetime 2 and its neighbours, `sv.time - freetime` at 0.5 and one ulp off, NaN). R7: 8 more builtin mutants
(alloc without the two-second rule, with a float time difference, from edict 1; free keeping origin; find and
nextent taking free edicts; findradius with a float centre or taking SOLID_NOT), all detected; 18 in all.
Steps 3c-1 and 3c-2 done (2026-10-13): the world and movement. `Server/World.cs`: areanodes (`SV_CreateAreaNode`),
the area lists as index links (prev/next arrays, one solid and one trigger head per node, so that the order of a
list is C's), `SV_UnlinkEdict`, `SV_LinkEdict` (absmin/absmax, item boxes grown by 15, `SV_FindTouchedLeafs` with
`MAX_ENT_LEAFS`, `SV_TouchLinks` running touch functions), `ED_Free` now unlinks; `Mathlib`: `BOX_ON_PLANE_SIDE`
and `BoxOnPlaneSide`. `Server/Move.cs`: `SV_Move` over the hull traces of phase 1 (`SV_ClipMoveToEntity`,
`SV_MoveBounds`, `SV_ClipToLinks`), `SV_PointContents`, and `sv_move.c` (`SV_CheckBottom`, `SV_movestep`,
`SV_StepDirection`, `SV_NewChaseDir`, `SV_MoveToGoal`). Builtins setorigin, setsize, setmodel (model bounds from the
loaded models: brush models from the BSP, alias models and sprites from their headers), traceline, droptofloor,
walkmove, movetogoal; 36 builtins ported in all. Patch `0004` adds three capture points around linking; capture
version 6 records `'K'` before `SV_LinkEdict`, `'k'` after it (leafs, area node, list and the previous edict in it)
and `'U'` at `SV_UnlinkEdict`. The replay links in the port wherever the reference linked: inside a ported builtin
the port's own code links and the record checks it; for an engine or replayed caller the replay drives the port's
`SV_LinkEdict` (touch functions inside it run as port code) and checks leafs, node, list and position in the list.
About 201,000 links and 318,000 ported builtin calls on the 10 maps, all matching. Level 1: a `BoxOnPlaneSide`
fuzz against the oracle (2·10^6 boxes and planes, planes through box corners, all plane types). R7 at source level
(`tools/mutate.py`, `docs/TESTING.md`): 13 linking mutants all detected (the order of a solid list only through the
previous-edict check, the strict comparisons in `BoxOnPlaneSide` only through the fuzz); of 10 movement mutants 6
detected and 4 known blind spots, each with its reason in the tool: the one-unit margin of `SV_MoveBounds` (only a
preselection), the missile box (`MOVE_MISSILE` comes from physics), own missiles in `SV_ClipToLinks` (likewise), and
`SV_NewChaseDir` with a float `abs` (needs two deltas that truncate to the same integer in one call). All four needed
`SV_Move`/`SV_NewChaseDir` fuzzed on a world inside the oracle; that came with step 3c-3 and detects all four. Not
every candidate was bad: `SV_CheckBottom`'s midpoint in float instead of double gives the same bits (halving is
exact) and was dropped.
Step 3c-3 done (2026-10-13): `Server/CheckClient.cs` — `Mod_PointInLeaf`, `Mod_DecompressVis`/`Mod_LeafPVS` (stops
with an error where C would read past the visibility lump or write past its buffer), `PF_newcheckclient` and
`PF_checkclient` with `sv.lastcheck`, `sv.lastchecktime` (double) and `checkpvs` as server state; `PF_aim` (the
target's centre in double, teamplay and `sv_aim` as cvar values), `Mathlib.VectorNormalize`. 38 builtins ported.
Level 2: the replay runs both as port code, about 606,000 ported builtin calls on the 10 maps, all matching;
`checkclient` returns a client in 4,344 of its 287,740 calls, `aim` is called 163 times. That was not enough: of 14
mutants the replay detected 7. Level 1 therefore got a world (oracle version 3, new entry points only, no hook
lines): `EdictOracle_LoadWorld` loads a map with `Mod_LoadBrushModel` onto the oracle's own hunk (with
`COM_Init`'s byte order setup, which the loader reads through) and runs `SV_ClearWorld`; `EdictOracle_LinkEdict`,
`EdictOracle_Move`, `EdictOracle_PointInLeaf`, `EdictOracle_LeafPvs`, `EdictOracle_CheckState`,
`EdictOracle_NewChaseDir` and `EdictOracle_AimCvars` call the reference's code on it. `WorldFuzzTests`, per map:
the PVS of every leaf, 40,000 `Mod_PointInLeaf` points (half on node planes), then 40 rounds of 23 random entities
(all solid types, brush models, items, monsters, owners, free edicts) linked on both sides, with 1,500 `SV_Move`
calls, 150 `checkclient` calls (notarget, dead and free clients, the state at the 0.1 s boundary), 60 `aim` calls
(teamplay, `sv_aim` from -1 to 1, two targets at the same angle) and up to 40 `SV_NewChaseDir` calls (deltas that
truncate alike) per round; 2 s for all maps. It found no difference in the port; the two failures on the way were
in the harness (the byte order pointers, a port header not reset like the reference's). R7 (`tools/mutate.py`, groups `move`
and `check` now include the world fuzz): link 13 of 13, move 10 of 10 detected (the four blind spots of 3c-2
included); check 12 of 13 detected, and one known blind spot: `aim`'s target centre in float differs only on
denormal coordinates (rounding the double sum to float is innocuous, 53 >= 2·24 + 2, and halving is exact otherwise).
Two candidates were shown not to be bad and dropped: `checkclient`'s interval with `>` instead of `>=` (0.1 as a
double is an odd multiple of 2^-55, and with `sv.time` >= 1 the difference is a multiple of 2^-52, so the two
never meet), and in the fuzz itself two targets mirrored sideways (`aim` drops the sideways component, so they give
the same answer; the second target now sits above `v_forward` instead).
Step 4 in two parts: 4a the gate's reference side and the port's state hash, 4b the port's spawn.
Step 4a done (2026-10-13): `SV_SpawnServer` runs `SV_Physics` twice itself (to let everything settle) and then
`SV_CreateBaseline`, so "frame 0, before `SV_Physics`" is the point right after `ED_LoadFromFile`. Patch `0005`
puts one call there (`Edict_Spawned`): with `-edicthash` it writes a map record and a spawn record `'S'` (the three
lanes and one hash per edict; `.edh` version 2), with `-edictcapture` a spawn record with the engine's last changes
and the engine state the hash covers (rand seed, `sv.time`, `svs.serverflags`, the light styles; capture version
7). `tools/captures.sh` writes the `.edh` of the same run next to each capture. Per-frame hashes of all 10 maps ×
4,000 frames are unchanged by the patch, Windows and Linux agree on every frame and spawn record, and the captures
differ from version 6 only by the new records (and the cvar `cmdline`, which holds the output paths): on 8 maps
removing them gives the old file byte for byte; on `lq_e0m6` and `lq_e0m7` the spawn record also takes over engine
changes after the last spawn function (entities freed by `ED_LoadFromFile`) that version 6 had in the next diff. The port's side:
`Edict.Harness/StateHash.cs` builds the same canonical streams over the port's `Server` and hashes them with its own
XXH64; `EdhFile` reads the spawn records. Checked before the port spawns anything itself: the capture replay hashes
the reference's state at every spawn point (15 on the 10 maps, respawns included) and matches every lane and every
entity; `StateHashTests` checks XXH64 against the reference's on every length up to 1,100.
Step 4b done (2026-10-13), and with it the cut-2 gate: the port spawns every map itself and hashes as the reference
at frame 0. `Server/Spawn.cs`: `SV_SpawnServer` up to the frame-0 point (skill rounded and clamped, deathmatch reset
for coop, `sv.time` 1, the world model and its inline models as precache entries, the world edict, the spawn
globals) and pr_edict.c's parser (`ED_FindField`, `ED_FindFunction`, `ED_NewString` with its escapes,
`ED_ParseEpair`, `ED_ParseEdict` with the angle and light hacks and the trailing-space trim, `ED_LoadFromFile` with
the skill and deathmatch filters); `Common/ComParse.cs` (`COM_Parse`, with C's signed `char`: bytes from 0x80 on are
white space); `HunkStrings` (engine strings at offsets of their own; hashed by content); `GameHost` (the engine's VM
host: the port's builtins, an error for one it does not have); builtins `lightstyle` (the state; the message to
clients comes with messages) and `ambientsound` (the precache check; its signon message likewise); 40 builtins in
all; `Cvars.SetValue` for integral values; the entity lump in `BrushLoader`. Mod_ForName differs in when, not what:
C loads a model when precache_model first names it, inside the VM; the port's `ModelCache` loads at map load every
model whose name is a string of progs.dat or a token of the entity lump, so that the VM path does not allocate; a
model name built at run time would stop with an error. Where C has undefined behaviour on malformed text (strcpy
into fixed buffers, a vector value with fewer than three components read past its end, atoi out of int, a quoted
string open at the end of the text), the port stops with an error. Gate (`SpawnGateTests`): the 10 maps of the tapes
and 80 spawn-only variants (`tools/captures.sh`: skills 0, 0.6, 2, 2.6, 3, 5, deathmatch 2 and coop with four
client slots; negative skills cannot be set, `Cmd_StuffCmds_f` ends a `+` command at any `-`), inputs from the
capture of the run (maxclients, cvars, rand seed; for the variants the cvars as the command line set them, since the
capture records them after `SV_SpawnServer` has adjusted them); every entity, the edicts lane and the typed globals
lane must match. All 90 green on the first complete run, 80 to 291 edicts per map. Windows and Linux references
agree on all 80 variants. Level 1 (`SpawnParseTests`): `COM_Parse` on 10^5 random texts, `ED_ParseEpair` on 2·10^5
strings, vectors and floats, `ED_ParseEdict` on 2·10^4 random entities of the game's progs.dat (all field defs as
keys, with trailing spaces, the hacks, unknown keys, values of each type), each against the reference through the
oracle (version 5: the progs image on the oracle's hunk, which `string_t` offsets need, as patch 0002 does for the
engine). R7 (`tools/mutate.py`, group `spawn`): 20 of 20 detected, one known blind spot (sprite bounds in float: the
three sprites of LibreQuake are 16×16, 32×32 and 64×64); two candidates shown not to be bad (underscore keys:
LibreQuake's progs has no field whose name begins with one; setting both the coop and the deathmatch global: the
other one is 0 either way). The first run of the group, with only the gate on the 10 maps and 50 variants, let 7
survive (the parser's corner cases, the skill rounding and the coop reset, whose inputs the capture records after the
adjustment); keys with trailing spaces then survived the first level-1 tests too and led to the `ED_ParseEdict` fuzz.
Not in the gate yet: messages (signon, baseline), which no lane hashes; the two settling `SV_Physics` frames are cut 3.
**Cut 3:** `SV_Physics*`, `sv_user.c`, `SV_ReadClientMessage`, tape replay. Gate: per-frame hash, ≥ 3 maps × 10 min.
Steps (2026-10-13), each with its own gate, the capture replay localizing the first difference as in cut 2:
(1) census of what a frame reaches; (2) the frame's server part up to physics and the client: `rand ()` per host
frame, `SV_CheckForNewClients`/`SV_ConnectClient`, `SV_RunClients` with `SV_ReadClientMessage` on the tape's
messages, the host commands the tape sends (`prespawn`, `name`, `color`, `spawn`, `begin`), `sv_user.c`; gated at
each frame's first engine entry, where the client edict's words the port changed must be the recorded ones;
(3) `SV_Physics` with every movetype the census found, gated by the replay running each frame's physics as the
port's, plus a physics fuzz on the oracle's world; (4) the frame's end and the loop: the state effects of
`SV_SendClientMessages`, `SV_SpawnServer`'s two settling frames and `SV_CreateBaseline`, `restart`/`changelevel`
through `localcmd`; gate: the per-frame hash of every frame record, then ≥ 3 maps × 10 min on new tapes.
Step 1 done (2026-10-13), from the captures (VM memory rebuilt from the diffs, live edicts at each frame's first
entry, 10 maps × 4,000 frames): entity-frames by movetype none 2,403,973, step 2,101,720, toss 1,434,327, push
1,068,041, noclip 49,135, walk 27,726 (the player), bounce 4,729, fly 2,157, flymissile 491; anglenoclip and
angleclip never. The player walks fewer than 4,000 frames where it dies: QuakeC's `localcmd ("restart")` respawns the
map, and the open-loop tape sends no new `spawn`, so the client stays connected but not spawned.
Step 2 done (2026-10-13): `Server/Clients.cs` — `client_t` (clients outlive a map, as svs.clients),
`SV_CheckForNewClients`, `SV_ConnectClient` (SetNewParms), `SV_SendServerinfo`, `SV_RunClients`,
`SV_ReadClientMessage` and `SV_ReadClientMove`, `Cmd_TokenizeString` and the client commands of the tape
(`prespawn`, `name`, `color`, `spawn` with ClientConnect and PutClientInServer, `begin`), `SV_WriteClientdataToMessage`
(its state: damage and fixangle cleared, idealpitch) and `SV_SetIdealPitch`; `Server/User.cs` — `sv_user.c`
(`SV_ClientThink`, air, water and noclip moves, friction, acceleration) and `V_CalcRoll`; `Common/SizeBuf.cs` —
`sizebuf_t` with `MSG_Write*`/`MSG_Read*`, written in full because overflows are state; `Edict.Harness/TapeNet.cs`
— the tape driver. Two things found on the way: C's `SV_ReadClientMessage` never tests its `while (ret == 1)` (every
message ends in `goto nextmsg`), so it reads all pending messages; and `netname` holds the offset of the client's
name buffer, which the capture now records per map (capture version 9) so that the port uses the reference's offset
and copies of netname compare word for word. Gate (`VmReplayTests.Server_frame_start_matches_the_reference`): at
every `'H'` record the port runs the frame's start itself on the run's tape; at the frame's next engine entry every
word the port changed must be the recorded value and every change the reference made to a client edict the port
must have made. 4,000 frames on each of the 10 maps, green on the first run, Windows and Linux. R7 (`clients`, 25
mutants): 18 detected; 7 known blind spots, each with its reason in the tool: the tapes never send an upmove, a
color other than 0 0, or a backward walk out of a teleporter, and SV_WriteClientdataToMessage runs only once per spawn
until SV_SendClientMessages is ported (damage, idealpitch).
Step 3 done (2026-10-13): `Server/Physics.cs` — sv_phys.c without its QUAKE2 parts (every movetype the census found
and the client's, `SV_RunThink`, `SV_Impact`, `ClipVelocity`, `SV_FlyMove`, `SV_PushMove`, `SV_WalkMove` with
`SV_TryUnstick` and `SV_WallFriction`, `SV_CheckStuck`, `SV_CheckWater`, `SV_CheckWaterTransition`), the state of
`SV_SendClientMessages` (frag updates, each spawned client's client data every frame, signon, `SV_CleanupEnts`),
`SV_CreateBaseline` (the baseline is in the edict header and in the hash), `SV_StartSound` and `SV_StartParticle`,
and the eleven builtins still missing (the `Write*` family, sound, particle, the prints, stuffcmd, localcmd,
changelevel): all 51 builtins the corpus reaches are the port's; messages are written in full, though no lane hashes
them yet. Gate (`VmReplayTests.Server_frames_match_the_reference`): the port runs every server frame whole; every
boundary inside it is checked, and at the next frame's start what changed since, the next frame's `rand ()` and the
state hash of the reference's frame record (frame N is record N + 1: the record is written after `_Host_Frame` has
counted the frame). 39,985 frames hashed alike on the 10 maps (all but the last and those before a restart). Level 1
(`PhysicsFuzzTests`, oracle version 6): `SV_Physics` itself on the oracle's world and the game's progs.dat, every
function pointed at SUB_Null, random entities of every movetype, speeds beyond sv_maxvelocity, gravity fields,
pushers with riders, stuck players, think times on the frame boundary; 1,500 frames per map on 4 maps. R7
(`physics`, 35 mutants): the replay alone detected 27 of 36; with the fuzz 33 of 35 (a think exactly at the frame
end among them), and 2 known blind spots (a player stuck exactly 17 units deep, `old_frags`, which only messages
show); one candidate shown not to be bad (the pusher's `<` with `<=`).
Step 4 done (2026-10-13), and with it the cut-3 gate: `Host.cs` — the host loop of a server without a local client:
`_Host_Frame`'s server part (the frame's `rand ()`, `Host_FilterTime` with host_framerate, `Cbuf_Execute`,
`Host_ServerFrame`, host_framecount), the commands a game issues (`map`, `restart` after a death through QuakeC's
localcmd, `changelevel` with `SV_SaveSpawnparms`), and the whole `SV_SpawnServer` (two settling frames with
host_frametime 0.1, which the host frame keeps; baselines; serverinfo, which after a restart leaves the client not
spawned). Gate (`LoopGateTests`): the port's host runs the tape alone, no capture in the loop, and after every frame
its state must hash as the reference's frame record, at every spawn as its spawn record. Inputs taken from the
reference: the cvars, maxclients and the rand state at the first spawn. The 10 corpus runs: all 40,000 frames and 15
spawns (four restarts, `dev`'s changelevel to `lq_e0m1`). The long runs (`tools/longruns.sh`, new tapes, seeds
101, 103 and 106): `lq_e0m1`, `lq_e0m3` and `lq_e0m6` × 43,200 frames (ten minutes at 72 fps; sv.time from 1.3 to
601.3), every frame and spawn hashed alike, on Windows and Linux. Not ported yet: SV_WriteEntitiesToClient (messages
only), dropping a client, saved games, a local client, any other console command; the message bytes are produced but
not gated (the messages lane).
The messages lane (2026-10-13), the server's output in the gate. Reference, without a hook line: the tape driver
(`edict_tape.c`) hands every message the server sends to `edict_hash.c`; `.edh` version 3 has `lane_messages` in every
spawn and frame record, XXH64 over what was sent since the previous record (per message u8 kind, u32 length, the
bytes). `-edictmessages` writes the messages themselves (`.emg`), so that a difference can be shown to the byte;
`tools/captures.sh` and `tools/longruns.sh` write one per run. On all 90 corpus runs and the three long runs every
other lane is identical to version 2. Port: `Server/Send.cs` — SV_SendClientMessages whole: SV_SendClientDatagram
with SV_WriteEntitiesToClient and SV_FatPVS, SV_SendNop on the host's realtime, the reliable message and
last_message, SV_SendReconnect when a map spawns over a running server; the connection got the three send calls,
`TapeNet` keeps what is sent. The gate found what was still missing on its first run, in frame 3 of `start`: the
prespawn reply began with svc_spawnstaticsound in the reference and with svc_spawnbaseline in the port — `ambientsound`
and `makestatic` now write their signon messages and `lightstyle` its message to the clients. Gate
(`LoopGateTests`): at every record the port's messages must hash as the reference's; with the `.emg` a difference is
reported as message, kind and byte. Green on the 10 corpus runs (40,000 frames, 15 spawns; per map 8–22 reliable and
451–3,993 unreliable messages, 30–390 KB) and the three long runs (on `lq_e0m3` 43,193 datagrams, 2.1 MB; on
`lq_e0m1` and `lq_e0m6` the player dies early, and the client that stays connected after the restart gets a nop
every five seconds: 114 and 108 of them). Level 1 (`MessageFuzzTests`, oracle version 7: `EdictOracle_SetBaseline`,
`EdictOracle_ClientDatagram`, which runs SV_WriteClientdataToMessage and SV_WriteEntitiesToClient on the oracle's world
into a buffer that allows overflow): 300 linked entities, origins 0.1 off their baseline and one float either side,
fractional and out-of-byte frames, skins, colormaps and model indexes, the client exactly 8 units (and a float less)
from axial node planes (130–290 per map), messages that start near their end; per map 1,000 datagrams, 2,800–3,900
entity updates in those without client data, 320–500 with a long entity number, 250–265 stopped by the 16-byte check,
73–84 overflows; and SV_SendClientDatagram
itself (`EdictOracle_SendClientDatagram`; the oracle takes the message over through the driver table) with a server
datagram that fits, just fits or misses by one byte, 131–136 of 250 calls per map at that edge. `MessageBuiltinTests`
(`EdictOracle_MessageSetup`, `EdictOracle_TakeMessage`): the 17 message builtins — the `Write*` family to every
destination, sound, particle, sprint, centerprint, bprint, stuffcmd, lightstyle, ambientsound, makestatic — against
`pr_builtins[num] ()`, with clients active, spawned, both or neither, and every buffer they can write compared
(sv.datagram, sv.reliable_datagram, sv.signon, each client's message); about 2,300 calls each. All green on the first
run. R7 (`messages`, 29 mutants): all detected. On the first run of the group 4 survived, each a gap in level 1, not
in the port: `U_MOREBITS` from 257 (only an update that changes the pitch alone has bits == 256; the fuzz changed all
three angles together), the server datagram copied when it just fits (the fuzz did not call SV_SendClientDatagram),
`makestatic` with skin and colormap swapped (both are 0 on LibreQuake's statics) and `lightstyle` only to spawned
clients (after a restart nobody switches a light); the two datagram cases and the builtin fuzz above closed them.
The groups `clients` and `physics` now run the loop gate too, which sees the messages their known blind spots needed:
the two ideal-pitch mutants, damage kept and `old_frags` are detected now. One more defect found on the way: three
fuzzes (world, physics, datagram) took their seed from `string.GetHashCode`, which .NET randomizes per process, so
every run fuzzed other inputs (`CheckStuck: 17 up` was detected in one run and not in the next). The seeds are XXH64
of the map name now; `move`, `check`, `physics` and `messages` were rerun with them: 86 of 87 detected (`CheckStuck:
17 up` among them, by the physics fuzz on `lq_e0m3`), the one left is `aim`'s known denormal case. Deviations from C: SV_SendClientDatagram's buffer has an
uninitialized `allowoverflow`, so an overflow (an entity update is up to 18 bytes, the check leaves 16) is undefined
in C; the port stops with an error, the fuzz compares the allowed overflow. SV_FatPVS writes past `fatpvs` on a map
with more than 8,189 leafs; the port stops. Not ported yet: dropping a client, saved games, a local client, any other
console command.

**P4: zero allocation, performance variants, all gates on `Host_Frame`** (section D; time-boxed). Steps
(2026-10-13): (1) the allocation gate on the host loop; (2) a frame benchmark, the port against the reference on
the same tapes; (3) variants, each through the loop gate (D1) before it is measured.
Step 1 done (2026-10-13): `AllocationTests` runs the port's host loop on the 10 corpus tapes, without hashing, and
measures the managed bytes the thread allocates around every host frame. The analyzer had already kept the server
frame free of allocations; the one allocation found was a type initializer: `Server`'s static readonly fields are
preallocated exceptions, read only when thrown, so the first static read came in SV_SendServerinfo at the first
client connect and allocated 4,672 bytes inside frame 1. The host's constructor now runs the type initializers of
all engine types on the load path. Gate: 0 bytes in every frame that spawns nothing (39,985 frames); the 15 spawn
frames (map, restart, changelevel: the load path) allocate 2.6–7.4 MB each and are counted apart. The long runs are
in the gate too (129,595 frames more, 0 bytes).
Step 2 done (2026-10-13): `FrameBenchmarks` (BenchmarkDotNet, one invocation is a whole tape after frame 0, a fresh
host and the spawn in the iteration setup) and `edict_ref -edictframetime` (the reference's frames 1 to n−1 as one
`clock ()` interval, without hashing; a harness file, no hook line). Ryzen 9 3900X, Windows, .NET 10.0.9, the
reference as in D2 (clang 23, x86-64-v2, no LTO):

| Tape | Frames | Reference | Port | Port / reference |
|---|---|---|---|---|
| `lq_e0m3`, ten-minute run | 43,199 | 13.6 µs/frame (7 runs, 13.57–13.73) | 9.78 µs | **0.72** |
| `lq_e0m3`, corpus | 3,999 | 14.5 µs (±0.25: 58 ms in `clock ()` ticks of 1 ms) | 11.1 µs | 0.77 |
| `start`, corpus | 3,999 | 4.5 µs (±0.25) | 4.88 µs | 1.08 |

The ten-minute run is the comparison that counts: on 4,000 frames the reference's interval is 18–58 ms, and Windows'
`clock ()` counts milliseconds. The reference's time includes its console output (Con_Printf to stdout, e.g. sounds
that are not precached), which the port does not write. BenchmarkDotNet reports 32 B per invocation of the long run
(none for the others); the allocation gate measures 0 on the host's thread for all its frames, so they come from
another thread of the runtime.
Step 3, first variants (2026-10-13). Profile (dotnet-trace CPU sampling on the ten-minute run): SV_Physics 73 %,
of it SV_Physics_Step 46 % and in that SV_CheckWaterTransition 28 % — SV_PointContents for every step monster in
every frame, the walk down the world's hull 0; the VM 23 %, SV_SendClientMessages 19 % (SV_WriteEntitiesToClient
13 %). Three exact variants, each with the test that sees it done wrong (R7 group `perf`, `--only` per mutant):

| Variant | Why it is exact | Gate that sees it done wrong | Long run |
|---|---|---|---|
| SV_PointContents over the packed nodes | the same planes, children and operations in another layout | the trace corpus's hull contents, now for the packed walk too | 420 → 400 ms |
| cvar: the lookup cached by the name's address, a hit confirmed by the name; the value field instead of Q_atof of the string | names are unique and the list fixed; Store sets the value as Q_atof of the same string, and in a dedicated server only client and renderer code writes a value directly | the builtin fuzz, now with names rewritten at one address (the ftos buffer): an unconfirmed hit is detected | 400 → 377 ms |
| SV_PointContents cached by the point's bits (1,024 entries) | the world's hull 0 does not change while a server runs; a new map is a new Server, ClearWorld empties the cache | the world fuzz: each of its 40,000 points per map, an earlier one again and one with an earlier point's x and y, through the cache and without it; a key without z is detected | 377 → 304 ms |

Each variant passed the loop gate (10 corpus runs, the long run of `lq_e0m3`) and the allocation gate. Result
(BenchmarkDotNet, as in step 2): the ten-minute run 7.07 µs per frame (reference 13.6, **0.52**), `lq_e0m3` corpus
8.20 µs (0.57), `start` 4.32 µs (0.96).
Measured and dropped (2026-10-14): pr_xstatement set only at calls instead of at every statement (it is read only by
PR_EnterFunction): 304 ms before and after. The interpreter's own time is under 14 % of the frame, so even a
fifth faster interpreter would give less than 3 %.
P4 closed (2026-10-14), within its time box: the host loop allocates nothing outside a spawn, and the frame runs in
half the reference's time. Left in the profile: the VM (23 %, with the builtins it calls), SV_WriteEntitiesToClient
(13 %), checkclient (5 %), SV_CleanupEnts (3 %); each a few per cent at most. Open: the frame benchmark on .NET 11
at GA (section G) and on Linux.

**P5: the client** (section D). Steps (agreed 2026-10-14): (1) the reference side: demos of what the server sent
(the `.emg` of every run), played back by the reference without a screen, with client hashes after every host frame;
(2) a census of the server messages and client paths the corpus reaches; (3) the port: cl_parse.c, cl_main.c,
cl_tent.c, the demo reader, gated at every client frame, with a level-1 fuzz of the message parser; (4) the gate on
every corpus and long-run demo, then R7. A listen server (client and server in one process, one rand stream, §A.1)
stays a step of its own.
Step 1 done (2026-10-14). `tools/emg.py dem` writes a Quake demo of a run's `.emg` (cl_demo.c's format: the CD track
line, then per message its length, three view angles and the bytes), in the order the server sent the messages; the
view angles are the tape's last clc_move up to the message's host frame, as a recording client would have had them.
`tools/demos.sh` writes the demos into the game directory (`id1/edict/`, where playdemo looks) and runs the reference
on each: `-edictclient` (harness only, no hook line) sets scr_skipupdate, so SCR_UpdateScreen returns at once. The
client is the network state, the renderer is P6, and the renderer's long-for-pointer arithmetic writes through
truncated pointers on 64-bit Windows (LLP64): with the screen the Windows build crashed in the first demo frame.
What SCR_UpdateScreen would change on the way (V_UpdatePalette's color shifts, the particles' movement and expiry in
R_DrawParticles) is therefore not part of the truth; a run ends with its demo, before CL_NextDemo would play the
game's own. `.edh` version 4 adds a client record `'C'` after every host frame of a playback, three lanes: `client`
(cls.state and signon, cl's scalars, stats, items, color shifts, view and velocity vectors, the times, the precache
lists by name, scores, light styles, the visible entities as references, and the rand state), `entities`
(cl_entities, statics, temp entities, the gun model; pointers as what they point to: models by name, colormaps as
an index), `effects` (dynamic lights, beams, the numbers of active and free particles). The rand state is hashed
because client state depends on rand (a muzzle flash's light radius, an entity's syncbase when its model changes, a
lightning beam's roll), and the particle functions draw from it once per particle they get from the free list; with
no R_DrawParticles the list runs out and stays out, so the port has to model which particles are taken, not what
they do. A record `'I'` after frame 0 gives the port the client's inputs (the rand state before frame 0, all
cvars after it: frame 0 runs quake.rc, which execs LibreQuake's default.cfg, and +playdemo), and every `'C'` record carries the rand state in plain as well;
`-edictclientdump <first> <last> <file>` writes the canonical streams themselves for a range of frames, so that a
difference can be named down to the field. Corpus: the 10 corpus runs and the 3 long runs, 77,253 client frames; Windows and Linux identical on every
record and entity, two Windows runs identical, and server runs hash as before (version 4 adds only the record kind).
Found on the way: after a restart the tape's client never sends prespawn again, so the client stays at signon 1, and
CL_GetMessage, which reads without waiting until the signon is complete, parses the rest of the demo in one frame;
that is the reference's behaviour on these demos and the port's to reproduce.
Step 2 (census, `tools/emg.py census`, all 77,858 messages of the 13 runs): 415,905 entity updates (frame 174,490,
nolerp 108,917, long entity number 11,863, model 8,049, effects 284; colormap and skin never), svc_time and
svc_clientdata 77,256 each (ideal pitch in 10,747), sound 6,493, baselines 1,570, statics 467, static sounds 508,
temp entities 1,655 (gunshot 1,560, teleport 52, explosion 38, wizspike 5), light styles 832, prints 640, nops 253,
damage 71, setangle 52, stats 52, signon 46, particles 26, the scoreboard's name, frags and colors 26 each, cdtrack and
setview 20 each, stufftext 11, killed monster 4, centerprint 2, found secret 1. Never: disconnect, version,
stopsound, setpause, intermission, finale, sellscreen, cutscene.
Step 3 done (2026-10-14): `src/Edict.Engine/Client/` — `ClientState` (cls, cl and the client's arrays; CL_ClearState
with Host_ClearMemory's part; the demo reader of CL_GetMessage), `ClientParse` (CL_ParseServerMessage with every
message type but svc_disconnect and svc_sellscreen, which act outside the client and stop with an error;
CL_ParseServerInfo, CL_ParseUpdate, baselines, statics, client data, V_ParseDamage's color shift, CL_SignonReply),
`ClientMain` (CL_SendCmd with what CL_AdjustAngles and CL_BaseMove compute when no key is held, CL_ReadFromServer,
CL_LerpPoint, CL_RelinkEntities with its lights and trails, CL_AllocDlight, CL_DecayLights), `ClientTEnt` (every
temp entity, the beams and CL_UpdateTEnts), `Particles` (which particles are taken and how often each draws from
rand: 4 to 8 per particle by function, R_RocketTrail's float loop, R_EntityParticles' lazy setup of 486 draws that
repeats while its first value is 0), `ClientHost` (the client's _Host_Frame and the commands a playback issues:
playdemo, bf, reconnect). The models: `ModelCache` reads synctype and flags from alias and sprite headers, loads at
playdemo every name in the demo that ends in .mdl, .spr or .bsp (the server's Preload, for the client) and looks
them up at the server info without allocating; a map registers its inline models "*1", ... again on every load, as
Mod_LoadBrushModel does (the server holds them directly and had not noticed that the names kept the last map's).
Found in C on the way: CL_UpdateTEnts's segment loop reuses the beam loop's i, so after a beam with segments the beam
loop goes on from i = 4 while its pointer keeps going, and reads past cl_beams for a beam from index 4 on; the port
does the same and stops with an error where C would read past the array. The C client keeps cl_static_entities over
a CL_ClearState; the port too. `ClientHash` (Edict.Harness) builds the reference's client streams.
Gate (`ClientGateTests`): every corpus and long-run demo, 77,253 client frames, every record identical — the rand
state, the three lanes, every entity — on the first run. R7 (`client`) then showed what the corpus does not reach:
of 25 mutants 17 detected, 2 blind (no lightning, no TE_SPIKE in the corpus), 6 survived (one equivalent: clearing
the statics, whose untouched fields are 0 forever). `tools/demogen.py` writes synthetic demos for that (4 seeds × 3,000
messages, in `tools/demos.sh`): message times with every kind of step, gap and jump back, entity numbers from 256
on, moves of 99 to 101 units, angles across ±180, model changes onto copies of progs/armor.mdl with synctype ST_RAND
and each trail flag (LibreQuake has no ST_RAND model at all), every effect bit and temp entity type (lightning from
the view entity too), particles of every count, damage, stats, the scoreboard, light styles, setangle, setpause,
the bonus flash, intermission, finale and cutscene. The edges of CL_LerpPoint and CL_DecayLights are not reachable
in a steady playback: the client reads until a message's time reaches cl.time, so at CL_LerpPoint mtime[1] < cl.time
<= mtime[0] — frac is in [0, 1] and f is never 0. They are reachable in the signon frame, where CL_ParseServerInfo
has set cl.time to 0 and the client reads without looking at the times; each seed puts one edge there (a time that
goes back makes f negative and frac above 1: in (1, 1.01] and above 1.01; f exactly 0.1f; f == 0 with an explosion's
die equal to cl.time). With them (23,554 client frames more, identical on Windows and Linux, the port's on the first
run) all 24 non-equivalent mutants are detected.

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
  promoted to gate when stable). State 2026-10-13: `arealinks` is checked by the capture replay instead (linking
  records, section C); `messages` is a gate since `.edh` version 3: the bytes sent to the client since the previous
  record, from the tape driver, with no hook line in id's files.

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
- `0004` (2026-10-13, harness hook): `SV_LinkEdict` becomes `SV_LinkEdict_` with a wrapper holding a capture point
  before and after it (one line covers every exit), one capture point in `SV_UnlinkEdict`, `sv_areanodes` no longer
  static, the include: 12 lines, 20 of the 30 hook lines used. Hashes of all 10 R9 maps unchanged with and without
  the patch and `-edictcapture`.
- `0005` (2026-10-13, harness hook): one call after `ED_LoadFromFile` in `SV_SpawnServer` and the include, 2 lines,
  22 of the 30 hook lines used. Per-frame hashes of all 10 R9 maps unchanged with and without the patch.
- `0006` (2026-10-13, harness hook): a capture point at the start of `Host_ServerFrame` and the include, 2 lines,
  24 of the 30 hook lines used (capture version 8, record `'H'`). All 90 hash files of the corpus (10 maps × 4,000
  frames, 80 spawn variants) identical with and without the patch.
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

Measured 2026-10-13 (.NET 11 RC1 SDK `11.0.100-rc.1.26425.128`, a copy of the tree retargeted to `net11.0`, C# 15):
builds without warnings, analyzers included; all gates green (94 engine tests: hull traces, VM replay, VM opcode
fuzz; 34 Exact tests). Hull trace benchmark (BenchmarkDotNet 0.15.8 in-process on both runtimes, Ryzen 9 3900X):
.NET 11 is 1.02–1.06× slower than .NET 10.0.9 in every variant (packed: 214.0 vs 209.3 ns on `start`, 246.2 vs
239.8 on `lq_e0m3`), 0 B on both. No reason to switch; repeat at GA and with a frame benchmark (P4). The run also
exposed a phase-0 harness race: two Exact test classes reseeded the oracle's `rand` in parallel; .NET 10's timing
had hidden it. Fixed with a non-parallel collection.

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
