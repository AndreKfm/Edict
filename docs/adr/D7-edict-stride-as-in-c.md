# D7: The port lays out edicts with the reference's stride

Status: accepted 2026-10-13 (owner).

## Context

QuakeC refers to entities by byte offset: `EDICT_TO_PROG (e) = (byte *)e - (byte *)sv.edicts`, with the stride
`pr_edict_size = progs->entityfields * 4 + sizeof (edict_t) - sizeof (entvars_t)` (`pr_edict.c:1021`, not rounded).
`OP_ADDRESS` stores pointers into entity fields as offsets from `sv.edicts` too. These values live in globals and in
entity fields, typed (`ev_entity`, `ev_pointer`) or not (compiler temporaries).

`docs/PLAN.md` B.2 left the C# stride free and planned to compare entities by index only. Cut 2 replays recorded VM
calls in isolation (`docs/PLAN.md` section C, step 2): every word the VM reads and writes is compared with the
reference's, including untyped temporaries, whose meaning (entity offset, pointer, float bits) is unknown to the
harness.

## Decision

The port's edict area uses the same stride as the reference: the C# edict header occupies exactly
`sizeof (edict_t) - sizeof (entvars_t)` bytes of the x64 reference build (its contents are the port's own), followed by
`entityfields` 32-bit words. Entity references and field pointers are then numerically identical to the reference's.

## Consequences

- Recorded VM state compares word for word, with no translation layer; the raw-globals lane of the hash
  (`docs/PLAN.md` E.1) becomes comparable and can be promoted to a gate.
- Edicts stay misaligned for 8-byte members, as in C (`docs/PLAN.md` E.3); the port's header must not depend on the
  alignment of its members. 32-bit float and int accesses on x64 tolerate it.
- The header size is a constant of the reference build (x64, clang, both ABIs); it is recorded in every capture file and
  checked when the port loads one.
- Not covered: string offsets of strings the engine allocates (on the hunk in C, elsewhere in the port) differ. They are
  compared by content, as the hash already does (E.1, A.3).
