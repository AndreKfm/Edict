# D4: Target .NET 10 (LTS)

Status: accepted 2026-10-12 (the plan's decision table, with the owner).

## Context

.NET 10 is the long-term support release (until November 2028). .NET 11 is a standard-term release, at RC1 when the
plan was written (GA expected November 2026); one of its breaking changes makes float-to-small-integer casts
saturate, which touches exactness wherever raw casts are used (`docs/PLAN.md` G).

## Decision

All projects target `net10.0`; `global.json` pins SDK 10.0.100 with `latestFeature` roll-forward. .NET 11 is evaluated
as a benchmark variant and on the corpus, not adopted by default. A switch needs the corpus green on both runtimes and
a gain on the host frame above 5 %.

## Consequences

- Float-to-int conversions go through `CCast` (`docs/PLAN.md` A.2) on every runtime, which makes the .NET 11 cast
  change neutral.
- Measured 2026-10-13 on a copy retargeted to `net11.0`: builds without warnings, the gates of that time green, the
  hull trace benchmark 1.02–1.06× slower than on .NET 10 (`docs/PLAN.md` G). No reason to switch; revisit with
  .NET 12 (LTS, November 2027).
- The CI (`.github/workflows/ci.yml`) installs the SDK from `global.json`.
