# ADR 0022 — Which improving neighbour the local search adopts

* Status: accepted
* Date: 2026-09-11
* Supersedes the acceptance half of [ADR 0008](0008-insertion-neighbourhood.md); the
  neighbourhood half of 0008 stands unchanged.

## Context

ADR 0008 changed two things at once — the neighbourhood (adjacent swap → insertion)
and the acceptance rule (first improvement → steepest descent) — and validated the
pair on 8-job instances. At 8 jobs a full pass of the insertion neighbourhood is
56 neighbours, so a 2 000-step budget runs about 35 passes and the acceptance rule
is invisible. The conclusion was about the neighbourhood and it was right; the
acceptance rule came along with it untested.

At 100 jobs a pass is 9 900 neighbours. With the shipped default budget of 2 000
per restart, steepest descent never finishes a pass: the sweep stops around source
position 20, jobs 21 to 99 are never lifted out of the sequence at all, and the
whole budget buys **one** adopted move. `LocalSearchResult.AdoptedMoves` now
reports this, and `AcceptanceRuleTests.Steepest_descent_adopts_one_move_where_the_others_adopt_many`
pins it.

## Decision

`SchedulingParameters.LocalSearchAcceptance` offers three rules over the same
insertion neighbourhood. The default is **BestInsertion**.

| Rule | One adopted move costs | Reaches the back of the sequence |
| --- | --- | --- |
| `SteepestDescent` | a whole pass, n·(n−1) | only if the budget covers a pass |
| `FirstImprovement` | 1 to n−1, then on to the next job | yes |
| `BestInsertion` | n−1 — the best position for that job | yes |

`BestInsertion` sweeps the jobs in order and moves each to its best position if
that improves. It is the middle course: a sweep can relocate every job, and each
move it makes is the best available for that job rather than the first that
happened to help.

## Measurements

All numbers measured on this machine with
`dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- <mode>`,
.NET 10.0.9, 28 logical cores. Five instances per size — different routings,
durations and releases, not one instance under five seeds. One restart, so the
penalty column is the descent and nothing else. "Pass" is n·(n−1).

### Acceptance rules at equal budget (`-- acceptance`)

| Jobs | Budget | Pass | Steepest | FirstImprovement | BestInsertion | Best rule | Best vs steepest |
| ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| 5 | 20 | 20 | 331.384 | 331.384 | 331.384 | tie | 0.00 % |
| 5 | 20 | 20 | 331.384 | 331.384 | 331.384 | tie | 0.00 % |
| 5 | 2000 | 20 | 331.384 | 331.384 | 331.384 | tie | 0.00 % |
| 8 | 20 | 56 | 650.006 | 602.636 | **583.460** | BestInsertion | 10.24 % |
| 8 | 56 | 56 | 573.234 | 515.202 | **430.796** | BestInsertion | 24.85 % |
| 8 | 2000 | 56 | 517.117 | 493.117 | **430.796** | BestInsertion | 16.69 % |
| 20 | 95 | 380 | 331.698 | **248.344** | 289.523 | FirstImprovement | 25.13 % |
| 20 | 380 | 380 | 270.381 | 210.210 | **209.343** | BestInsertion | 22.57 % |
| 20 | 2000 | 380 | 249.074 | **186.982** | 209.343 | FirstImprovement | 24.93 % |
| 50 | 612 | 2450 | **3093.369** | 3428.177 | 3346.842 | SteepestDescent | 0.00 % |
| 50 | 2450 | 2450 | **3093.369** | 3223.022 | 3131.261 | SteepestDescent | 0.00 % |
| 50 | 7350 | 2450 | **3069.394** | 3108.089 | 3072.970 | SteepestDescent | 0.00 % |
| 100 | 2475 | 9900 | 10850.487 | 10838.857 | **10681.354** | BestInsertion | 1.56 % |
| 100 | 9900 | 9900 | 10850.487 | 10486.745 | **10428.989** | BestInsertion | 3.88 % |
| 100 | 20000 | 9900 | 10639.484 | 10430.611 | **10312.170** | BestInsertion | 3.08 % |

Mean improvement over steepest descent across the fifteen rows: **BestInsertion
+6.8 %**, FirstImprovement +5.6 %. BestInsertion is also the less volatile of the
two — its worst row is −8.2 % against FirstImprovement's −10.8 %.

The audit's original figures reproduce exactly on the reference 100-job instance
(variant 0, one restart): steepest 10 256.988 against first improvement
10 001.542 at 9 900 steps and 9 943.367 at 20 000.

### 50 jobs is the honest exception

Steepest descent wins every 50-job row. It is one shape of one family and it is
reported rather than explained away; BestInsertion loses by 0.12 % at the largest
budget there, against gains of 3 to 25 % at 8, 20 and 100 jobs.

### The budget has to buy something (`-- budget`)

The reference 100-job instance, 8 restarts, one row per budget:

| Acceptance | Candidates | Penalty | ms |
| --- | ---: | ---: | ---: |
| SteepestDescent | 16 008 | 10 256.988 | 97.1 |
| SteepestDescent | 48 008 | 10 256.988 | 292.2 |
| SteepestDescent | 96 008 | 10 253.738 | 600.1 |
| SteepestDescent | 160 008 | 10 161.488 | 1014.0 |
| BestInsertion | 16 008 | 10 394.854 | 97.7 |
| BestInsertion | 48 008 | 10 190.404 | 292.6 |
| BestInsertion | 96 008 | 9 921.020 | 586.0 |
| BestInsertion | 160 008 | **9 915.442** | 971.9 |

Ten times the budget moves steepest descent 0.9 % and BestInsertion 4.6 %. This is
the argument: steepest descent does not have a budget problem, it has a rule that
cannot spend one.

On this one instance at 16 008 candidates the new default is 1.3 % worse. At the
same **wall clock** it is better — 10 190.404 in 292.6 ms against the old engine's
10 256.988 in 300.9 ms — because a candidate now costs a third of what it did.

### Multi-start restarts (`-- restarts`, `-- optimality`)

The size sweep says restarts buy nothing: at 5, 20, 50 and 100 jobs, eight
restarts matched one on all five instances, at eight times the runtime. That
sweep under-samples the regime where restarts matter, because its largest budget
is three passes and a descent has not converged by then.

At 7 jobs with the default 2 000-step budget the descent converges long before the
budget runs out, and the dispatch-order optimum is exactly computable. 48 instances
(6 dispatch rules × 8 seeds), gap to that optimum:

| Starts | FirstImprovement | BestInsertion | SteepestDescent |
| ---: | ---: | ---: | ---: |
| 1 | 13.26 % (23 over 5 %) | 9.01 % (16) | 5.18 % (13) |
| 2 | 3.28 % (8) | 3.83 % (9) | 2.61 % (7) |
| 4 | 1.11 % (2) | 1.38 % (3) | 0.46 % (1) |
| 8 | **0.21 % (0)** | **0.45 % (1)** | **0.20 % (1)** |

**`MultiStartRuns` stays at 8**, now for a stated reason. The "eight restarts buy
nothing" reading is true of the 100-job benchmark instance and false of every size
below it, where a converged descent has nothing left but a restart. The cost of
being generous is no longer what it was: eight restarts of the 100-job problem
take 97 ms and allocate 92 KB, against 301 ms and 1.05 GB before the workspace
split.

`SearchTests.More_starts_never_hurt` could not have caught a broken multi-start —
restart 0 is the rule order and ties are kept, so it is true by construction.
`More_starts_sometimes_strictly_help` is the other half: over six fixed 7-job
fixtures, eight restarts must beat one strictly on at least one of them.

## Consequences

* The default schedule changes for existing inputs. It is a heuristic and the
  result was never contractual, but a caller comparing against a stored schedule
  will see a difference; `LocalSearchAcceptance.SteepestDescent` restores the old
  behaviour exactly.
* `LocalSearchMaxSteps` now means something at every size instead of only small
  ones, which makes the product cap in `SchedulingParameterLimits` matter: the
  budget is per restart, and 64 × 20 000 is 1.28 million dispatches.
* `LocalSearchResult.AdoptedMoves` is public, because the ratio of adopted moves
  to evaluated neighbours is the one number that says whether a budget bought
  search or only comparison. It is what made this ADR possible to write.
* Reproducing any table here is one command; the harness is in the repository
  rather than in a throwaway project, so the numbers can be re-measured when the
  engine changes rather than quoted from a commit message.
