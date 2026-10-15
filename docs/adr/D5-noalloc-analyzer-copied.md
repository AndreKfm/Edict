# D5: The [NoAlloc] analyzer is copied from Prism and renamed

Status: accepted 2026-10-12 (owner: copy only the analyzer, renamed, nothing named HotPath; licensed GPL-2.0 by its
copyright holder).

## Context

The game loop must not allocate (`docs/PLAN.md` B.2, P4). A Roslyn analyzer that checks a contract per member
(`[NoAlloc]` with constraints, and every callee carrying a matching marker or a reviewed allowlist entry) existed in
the owner's Prism repository as `Archfacts.HotPath`, together with a transitive verifier and benchmarks.

## Decision

The analyzer, its annotations and its tests are copied as source into `analyzers/Edict.NoAlloc` and renamed:
namespace `Edict.NoAlloc`, diagnostics `NA` instead of `HP`, `noalloc-allowlist.toml`, the MSBuild property
`EdictNoAllocInjectAttributes`. Prism's verifier and benchmarks are not copied. The copy is commit 84f4c40 (source:
Prism `analyzers/hotpath` at 6e60e5aba15f467327c8b1ef1665e3e9136e9ad7; 68 of 68 copied tests green).

## Consequences

- Edict builds from one checkout, without a package or a reference into Prism, and the analyzer evolves with Edict.
- The per-member rule (NA008: a callee without a matching contract) composes the contract over the call graph, so
  the frame's guarantee holds member by member; what the verifier would have added, a report of exemption chains,
  is not available. `[NoAllocUnchecked]` callees break a checked caller (NA013), which keeps the load paths and the
  console commands visibly apart from the frame.
- The analyzer's tests run in the full tier (`tools/test.sh full`) and in the CI's Windows job.
- Fixes to the copy are Edict's; they are not merged back into Prism automatically.
