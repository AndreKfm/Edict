# D1: Exactness beats performance

Status: accepted 2026-10-12 (the plan's decision table, with the owner).

## Context

Edict's claim is that the port runs WinQuake's game frame for frame as the C reference does, shown by state hashes
(`docs/PLAN.md` E.1) on a corpus of runs. Performance work (P4) and every later module offer variants that are
faster but compute differently: fused multiply-adds, reordered sums, SIMD reductions, float where C has double, a
cache instead of a recomputation. A "fast mode" with a tolerance would let such variants in.

## Decision

A variant that is not bit-identical to the reference on the whole corpus is not a candidate. There is one mode, the
exact one; performance is measured among exact variants only (`docs/PLAN.md` B: SIMD only where lanes are
independent and the scalar order is kept, no reductions).

## Consequences

- One test matrix: every gate checks the one build that ships; there is no tolerance to tune or to defend.
- Some speedups are out for good (FMA contraction, reassociated sums, float shortcuts). P4 found exact ones instead:
  the port runs the ten-minute tape at 0.72 of the reference's time per frame (`docs/PLAN.md` P4, step 2).
- Where a part cannot be exact, it is outside the proof by name and never writes the exact state: the GPU renderer
  (P6 step 7) reads the client's state, and the sockets of the network (P6 step 9.4) carry what the exact code sends.
- The rule applies to the reference too: what the reference does not pin (the CRT's math, the compiler's FP
  contraction) is pinned there first (D2, D3), so "exact" has one meaning.
