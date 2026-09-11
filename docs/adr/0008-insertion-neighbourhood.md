# 8. Search the insertion neighbourhood, not adjacent swaps

- **Status:** Accepted
- **Date:** 2026-08-16
- **Supersedes:** the adjacent-swap local search shipped in earlier versions
- **Superseded in part by [ADR 0022](0022-local-search-acceptance.md)** (2026-09-11)
  for the *acceptance rule* only — the neighbourhood conclusion below stands, but
  "the single best strict improvement" is no longer the default
- **Reference corrected by [ADR 0015](0015-exact-solver.md)** (2026-09-11): every
  figure in this record is measured against the best **dispatch order**, which is the
  search's own ceiling and not the optimum of the scheduling problem

## Context

The engine searches the space of job priority orders: a candidate order is
dispatched into a schedule, scored, and kept if it beats the incumbent. The
original implementation used **adjacent swaps** with first-improvement
acceptance and a 2000-neighbour budget per run.

It was using 7 to 16 of that budget before stalling, which should have been the
tell. Measured against brute-force enumeration of all `n!` orders on 20 random
8-job instances, it left a **27.3 % mean gap to the optimum and solved 0 of 20
instances**. Random shuffles beat the dispatch rule's own order on 56 of 63
draws, so most of the improvement came from the restarts rather than the search.

The cause is the neighbourhood, not the budget. An adjacent swap moves a job one
position per improving step, so a job that belongs ten places earlier is only
reachable if all ten intermediate positions also improve the objective. On a
tardiness objective they generally do not — moving an urgent job halfway forward
delays several others without yet fixing the one that matters — so the descent
hits a local optimum almost immediately.

## Decision

Use the **insertion** (or-opt) neighbourhood: remove one job from the sequence
and re-insert it at every other position, `n·(n−1)` neighbours per pass, taking
the single best strict improvement (steepest descent). Run the descent from
every multi-start restart rather than only from the best raw shuffle.

## Consequences

- ✅ Mean gap to the best **dispatch order** **27.3 % → 0.2 %**; 19 of 20 instances
  matched; worst case 62.8 % → 3.0 %. ADR 0015 later measured what that reference is
  worth: the best dispatch order is itself a median 5.3 % — and a mean 327 % — above
  the true optimum, because the dispatcher never back-fills.
- ✅ The budget parameter now means something. It was decorative before — the
  search never approached it.
- ✅ `OptimalityTests` asserts the result against brute-force enumeration, so a
  future regression in search quality fails the build instead of quietly producing
  worse schedules. Since ADR 0015 it measures against **two** references and names
  which is which.
- ➖ Each pass costs `O(n²)` dispatches instead of `O(n)`. At the time this was
  written a 100-job run was ~530 ms; the zero-allocation scoring path of 0.3.0
  measures **91 ms** for the same benchmark, and the page still caps the budget —
  see [PERFORMANCE.md](../PERFORMANCE.md).
- ➖ A better search makes the *dispatch rule* matter less — different rules now
  converge on the same schedule unless the optimiser is switched off. This is
  correct behaviour, but it means the rule selector is now more of a teaching
  device than a lever.
- ➖ Still a descent, so still a local optimum. Tabu search or simulated
  annealing would do better on larger instances; at this size the gap does not
  justify the complexity.
