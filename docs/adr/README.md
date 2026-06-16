# Architecture Decision Records

Short, dated records of the decisions that shaped the scheduling engine — the
*why* behind the structure, not just the *what*. Each one captures the context,
the decision and its consequences, in the spirit of
[Michael Nygard's ADRs](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-pure-scheduling-library.md) | Keep the scheduling engine a pure, dependency-free library | Accepted |
| [0002](0002-integer-seconds-time.md) | Model all internal time as integer seconds | Accepted |
| [0003](0003-forward-only-scheduling.md) | Ship forward scheduling only (no backward pass) | Accepted |
| [0004](0004-deterministic-prng.md) | Use a hand-rolled deterministic PRNG, not `System.Random` | Accepted |
