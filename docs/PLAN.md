# Edict — plan (draft 1, 2026-10-12)

Status: draft for review. Statements about the WinQuake source are from memory unless a file/line is cited;
items marked **(verify)** must be checked against the pinned tree in phase 0 before anything depends on them.

## 0. Decisions this plan rests on (each becomes an ADR)

| # | Decision | Alternative rejected | Why |
|---|---|---|---|
| D1 | Exactness beats performance. A faster variant that is not bit-identical on the corpus is not a candidate. | "Fast mode" with tolerance | The hash proof is the project's thesis; a second mode doubles the test matrix. |
| D2 | Reference build: 64-bit clang, `id386=0` (C paths only), `-O2 -ffp-contract=off -fno-fast-math -fno-strict-aliasing -march=x86-64-v2`, SSE scalar math, plus an engine-string table patch (§A.1). | MSVC; 32-bit SSE2 build | clang's FP contraction and excess-precision behaviour is documented and identical on Windows (clang-cl) and Linux. 64-bit keeps a modern toolchain and matches the C# process; the cost is one extra, well-known patch. |
| D3 | `sin`, `cos`, `atan2` and `rand` are vendored into the reference (forced-include header) and ported 1:1 to C#. | Use each platform's libm/CRT | Otherwise ground truth depends on the CRT version of the machine that built the reference. sqrt/floor/ceil/fabs are IEEE-exact and stay native. Consequence to state publicly: the reference is "WinQuake + pinned libm", not "WinQuake as shipped in 1996". |
| D4 | Target `net10.0` (LTS). .NET 11 is evaluated as a benchmark variant, not adopted by default (§G). | `net11.0` from day one | .NET 11 is STS, GA Nov 2026; one of its breaking changes touches float→int casts. |
| D5 | The `[NoAlloc]` analyzer is copied as source from Prism (analyzer + annotations + its tests), renamed to `Edict.NoAlloc.*`, with no "HotPath"/"Archfacts" names left: namespace `Edict.NoAlloc`, diagnostic prefix `NA` instead of `HP`, `noalloc-allowlist.toml`, MSBuild property `EdictNoAllocInjectAttributes`. Prism's transitive verifier and benchmarks are not copied. | NuGet / project reference into Prism | Requested; Edict builds from a single checkout and evolves the analyzer independently. Without the verifier, the per-member rule "every callee must carry a matching marker" (NA008) still composes; what is lost is the report of exemption chains, which is acceptable at this scale. |
| D6 | Zero runtime dependencies in `Edict.Engine`. Anything from the BCL that is in the hot path must be allowlisted for the `[NoAlloc]` analyzer or replaced by own code. | Use BCL collections/formatting | BCL formatting, `string`, `List<T>` and `Math.Sin` are either allocating or not bit-pinned. |

## A. Ground-truth attack — where "frame-exact" degrades to "approximately exact"

### A.1 Reference build traps

- **x87 / excess precision.** Any build that lets the compiler use x87 (32-bit without `-mfpmath=sse`, or `id386=1` with
  `Sys_SetFPCW`/`Sys_LowFPPrecision`) produces results that depend on register spills. Mitigation: SSE scalar only.
  Self-check: identical hashes at `-O0` and `-O2`.
- **Bitness.** WinQuake stores `string_t` as `char* - pr_strings` truncated to `int` (e.g. `PF_ftos` temp string,
  `sv.name`, `sv.modelname` assignments in `SV_SpawnServer`) **(verify sites)**. On x64 these offsets can exceed 32 bits.
  Decision (D2): 64-bit build with a QuakeSpasm-style engine-string table (`PR_SetEngineString`/`PR_GetString`,
  ~40 lines, ~10 call sites), guarded by `#ifdef EDICT_REF` and reviewed as its own patch hunk. P0 task: find every
  `- pr_strings` site with a grep plus a run under `-fsanitize=undefined` (pointer-overflow, implicit-conversion)
  over the corpus; a missed site shows up as a garbage string, which the content-based string hash (§A.3) catches.
- **FMA contraction.** `-ffp-contract=off` and a baseline ISA without FMA (`-march=x86-64-v2`). Self-check: a test
  expression whose fused and unfused results differ, compiled into the reference, must give the unfused bits.
- **libm / CRT.** See D3. Functions reached from simulation code: `sin/cos` (`AngleVectors` ← `SV_ClientThink`,
  `PF_makevectors`), `atan2` (`vectoyaw`, `PF_vectoyaw`, `PF_vectoangles`), `sqrt` (`VectorNormalize`, `Length`,
  `SV_UserFriction`, `PF_vlen`, `PF_normalize`). Candidate source: musl (MIT, derived from fdlibm).
- **`rand()`.** `PF_random` is `(rand() & 0x7fff) / ((float)0x7fff)`. The MSVC LCG differs from glibc. Vendor one LCG
  on both sides. In a listen server the client side (particles, `CL_ParseTEnt`, `R_RocketTrail`) consumes the same
  stream and perturbs server randomness → reference runs are `-dedicated`; client and server get separate streams in
  the client lane.

### A.2 Porting traps (C semantics that C# does not reproduce by default)

| Trap | Concrete sites | Detection | Rule |
|---|---|---|---|
| Double literals promote float expressions | `OP_STATE` (`time + 0.1`), `anglemod` (all-double), `AngleVectors` (`angles[YAW] * (M_PI*2/360)`, then `sin((double)angle)` → float), `SV_RecursiveHullCheck` (`DIST_EPSILON` = `0.03125` double), `SV_AddGravity`, `MSG_ReadAngle` (`* (360.0/256)`) | hash gate | Port literal types exactly; analyzer ED003 flags float expressions mixed with literals in `[Exact]` code unless suffix matches C. |
| float vs double state | `sv.time`, `host_frametime`, `realtime` are `double`; `pr_global_struct->time`, `nextthink`, `freetime`, `ltime` are `float` | hash gate | Never "upgrade" a type. |
| Out-of-range float→int | `anglemod`, `PF_rint` (`(int)(f+0.5)`), `MSG_WriteCoord` (`(int)(f*8)`), `MSG_WriteAngle` | `-fsanitize=float-cast-overflow` lane over the whole corpus | C: UB, x64 `cvttss2si` gives `0x80000000`. .NET 9+ saturates for int/long; **.NET 11 also saturates for byte/short**. All casts go through `CCast.I32/U8/...` reproducing `cvttss2si`; ED002 bans raw casts of float in `[Exact]`. |
| `min/max` macros vs `Math.Min` | bound clamps | unit test with NaN, ±0 | `CMath.Min/Max` = `(a<b)?a:b`; ban `Math(F).Min/Max` in `[Exact]`. |
| Reduction order | `DotProduct` macro is `a0*b0 + a1*b1 + a2*b2`, left-associative; `Length` loop | hash gate | Own `Vec3`; ban `System.Numerics.Vector3`, `Vector128.Dot/Sum` in `[Exact]` (ED001). |
| `atof` vs `Q_atof` | `ED_ParseEpair`/`PF_stof` use CRT `atof` (double then float: double rounding); `Cvar_Set` uses Quake's `Q_atof` | P0 fuzz vs reference | Two separate C# functions; `(float)ParseDouble`, never `float.Parse`. |
| `ftos`/`vtos` formatting | `PF_ftos`: integer → `"%d"`, else `"%5.1f"` (space padded); `PF_vtos` likewise | P0 fuzz (millions of floats) vs reference `sprintf` | Own fixed-point formatter writing into `Span<byte>`. Strings affect state through `bprint`/`centerprint` messages and string fields. |
| Edict free-list reuse | `ED_Alloc`: reuse if `free && (freetime < 2 \|\| sv.time - freetime > 0.5)`; scan from `maxclients+1` | hash includes free edicts | Port literally; free edicts stay in the hash (their fields are not cleared until reuse). |
| Area list order | `SV_ClipToLinks` keeps the first entity on equal `fraction`; list insertion order decides `trace.ent` and touch order | diagnostic lane: ordered areanode lists | Intrusive index lists with identical insertion points. |
| Wall clock | `Sys_FloatTime`, `host_frametime` | two identical runs must hash identically | Virtual clock; `host_framerate` fixed so `host_frametime` is the same double every frame. |
| Denormals | MXCSR FTZ/DAZ | startup probe on both sides (`1e-40f * 1f != 0`) | Neither side sets FTZ/DAZ; assert, don't assume. |

### A.3 Hash-scope traps

- `string_t` values are address-dependent → hash string **content**, never offsets.
- Entity references are byte offsets with layout-dependent stride (`EDICT_TO_PROG`) → hash as **index**.
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
  `progdefs.h`, checked against `PROGHEADER_CRC`) + extra progs fields. Accessors return `ref`:
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

**Cut 2:** `PR_LoadProgs`, VM, builtins, `ED_LoadFromFile`, `SV_SpawnServer`, areanodes. Gate: frame-0 hash
(edicts + typed globals after spawn, before `SV_Physics`) per map.
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

- Algorithm: XXH3-64 (`xxhash.h`, BSD-2 on the C side; `System.IO.Hashing.XxHash3` on the C# side). Not a
  cryptographic property; only collision resistance across a corpus matters.
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
- `ref/patched/` = upstream + `ref/edict.patch` (generated by `git diff`, reviewed as a unit).
- New files only, no logic in hooks: `edict_hash.c` (lanes, serialization, virtual clock, argv), `net_tape.c`
  (net driver modeled on `net_loop.c` that feeds recorded client messages), `edict_libm.c/.h`, `edict_rand.c`.
- Hook lines under `#ifdef EDICT_HASH`: end of `Host_ServerFrame`, main loop (`Host_Frame(1.0f/72)` until tape end),
  `Sys_FloatTime`, net driver table, `SV_ReadClientMessage` (record mode). Target: ≤ 30 changed lines in id files,
  checked by CI (`git diff --stat` budget).
- Headless: dedicated mode with the null drivers (`vid_null.c`, `snd_null.c`, `cd_null.c`, `in_null.c`, `sys_null.c`
  — **(verify)** which exist in the pinned tree).

### E.4 Inputs

- Tapes recorded with a listen-server build of the same patch at fixed 72 Hz frametime: per frame
  `(host_framecount, length, bytes)` of client 0's messages, including the signon string commands and `clc_move`
  with byte-quantized angles. Replay in dedicated mode through `net_tape.c`; C# implements the same driver.
- Corpus: LibreQuake maps + own tapes only. No id data in the repository or in CI.

### E.5 P0 exit gate

Identical `.edh` across two runs, `-O0`/`-O2`, Windows/Linux; `ftos`/`atof` fuzz matches; `float-cast-overflow`
sanitizer silent on all tapes; analyzer vendored and building.

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

- Reference: 64-bit with engine-string table (D2).
- Copy only the `[NoAlloc]` analyzer, renamed, nothing named HotPath (D5).
- Analyzer copy licensed GPLv2 by its copyright holder (§H).
- Plan and ADRs in English.
