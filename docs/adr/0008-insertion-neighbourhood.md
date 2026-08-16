# 8. Search the insertion neighbourhood, not adjacent swaps

- **Status:** Accepted
- **Date:** 2026-08-16
- **Supersedes:** the adjacent-swap local search shipped in earlier versions

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

- ✅ Mean gap to optimum **27.3 % → 0.2 %**; 19 of 20 instances solved to
  optimality; worst case 62.8 % → 3.0 %.
- ✅ The budget parameter now means something. It was decorative before — the
  search never approached it.
- ✅ `OptimalityTests` asserts the result against brute-force enumeration, so a
  future regression in search quality fails the build instead of quietly
  producing worse schedules.
- ➖ Each pass costs `O(n²)` dispatches instead of `O(n)`. At 8 jobs a full run
  is ~10 ms; at 100 jobs ~530 ms. Acceptable on the UI thread, and the reason
  the page caps the budget.
- ➖ A better search makes the *dispatch rule* matter less — different rules now
  converge on the same schedule unless the optimiser is switched off. This is
  correct behaviour, but it means the rule selector is now more of a teaching
  device than a lever.
- ➖ Still a descent, so still a local optimum. Tabu search or simulated
  annealing would do better on larger instances; at this size the gap does not
  justify the complexity.
