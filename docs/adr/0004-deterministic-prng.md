# 4. Use a hand-rolled deterministic PRNG, not `System.Random`

- **Status:** Accepted
- **Date:** 2026-06-16

## Context

The multi-start search needs randomness to diversify its restarts, but the result
must be **reproducible** for a given seed — across .NET versions, operating systems
and the browser runtime. `System.Random`'s algorithm is explicitly *not*
contractually stable; it has changed between .NET versions, so the same seed can
yield different sequences after a runtime upgrade.

## Decision

Hand-roll a tiny fixed-algorithm PRNG (`DeterministicRandom`, an xorshift64\* with
documented constants). Each multi-start restart gets its own stream derived from
`(Seed, runIndex)`, and the search runs single-threaded so draw order is fixed.

## Consequences

- ✅ The same seed produces the same stream — and therefore the same schedule —
  on every platform and .NET version.
- ✅ Pinned by a golden-value unit test, so an accidental change to the algorithm
  fails the build.
- ✅ No external dependency for randomness.
- ➖ We own ~20 lines of PRNG code (with documented constants) instead of using the
  framework's.
