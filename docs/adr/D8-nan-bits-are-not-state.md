# D8: Which NaN float arithmetic produces is not part of the state

Status: accepted 2026-10-13 (owner).

## Context

R1 (docs/TESTING.md) compares floats bit for bit, NaN payloads included. The opcode and builtin fuzzes (cut 2) found
the port and the reference disagreeing only in one way: when an operation has two NaN operands with different bits,
or makes a NaN from non-NaN operands, which NaN comes out. On x86 the first NaN operand of the machine instruction
wins, and the instruction's operand order is the compiler's choice:

- clang commutes the operands of multiplications and additions (`OP_MUL_V`, the sums of squares in `PF_normalize`,
  `PF_vlen`, `PF_vectoangles`), vectorizes some of them (`mulps`), folds `-1*x` into a negation (which flips a NaN's
  sign where a multiplication keeps it) and removes double negations (`AngleVectors`);
- RyuJIT commutes operands too, so the port's source order does not decide it either.

LLVM's semantics leave the payload and sign of a NaN result unspecified, so these choices are legal and can change
with any compiler version. The reference is deterministic in NaN bits only for one binary; clang 18 and 23 happen to
agree today. Matching it would mean writing every affected expression in the form one clang version emits, with an
explicit NaN rule per operation in the hot path, re-derived whenever clang or RyuJIT changes.

## Decision

The exactness contract (D1) covers every value except the bits of NaN results of float arithmetic: such a result must
be NaN on both sides, with any payload and sign. Copies of a value (stores, loads, returns, parameter copies) keep all
of its bits, NaN included. QuakeC can observe the bits of a NaN in only two places, and both are made insensitive:

- the state hash (ref/src/edict_hash.c, and the port's hash when it exists) hashes every float NaN as `0x7fc00000` and
  every double NaN as `0x7ff8000000000000`;
- the pinned `sprintf` (ref/src/edict_libc.c, `CFormat`) prints a NaN as `nan`/`NAN` without a sign (infinities keep
  theirs). This changes what `ftos` returns for a negative NaN compared with WinQuake built against glibc (`-nan`), as
  D3 already does for libm and NaN text; recorded as a deviation of the reference.

## Consequences

- The port is written in C's source order everywhere; no clang code shapes in C#.
- Gates compare NaN results as a class: the capture replay for all words, the fuzzes for arithmetic statements and
  builtins. The opcode fuzz compares copies bit for bit, NaN payloads included, so a copy that loses NaN bits is still
  caught.
- A word that is an integer with a NaN bit pattern (above `0x7f800000` in magnitude) would compare as NaN in the
  replay; entity offsets, string offsets and function numbers are far below that.
- R9 hashes were unchanged by the canonicalization (no NaN occurs in the corpus).
