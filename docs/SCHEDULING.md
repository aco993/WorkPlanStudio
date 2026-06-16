# Production scheduling — how it works

**English** · [Deutsch](SCHEDULING.de.md)

This document describes the scheduling engine in
[`src/WorkPlanStudio.Scheduling`](../src/WorkPlanStudio.Scheduling). It is a
self-contained, dependency-free .NET library: no Blazor, no Entity Framework, no
JavaScript, no WebAssembly. That isolation is deliberate — it keeps the algorithm
unit-testable on a plain runner and reusable outside the browser app.

> **TL;DR** — Released work plans become *jobs*; each operation is a *step* that
> must run in sequence on a *work center* of finite capacity. The engine assigns
> every job a target date, sequences the work to hit those targets, optimises the
> sequence, and scores the result. Given the same seed it always produces the
> exact same schedule.

---

## 1. The problem

This is a finite-capacity **job-shop / flow-shop scheduling** problem:

- A **job** (`ProductionJob`) is an ordered chain of **steps**.
- A **step** (`JobStep`) is one operation: it runs on a single **work center** for
  a fixed duration and cannot start until the previous step of the same job has
  finished (operation precedence).
- A **work center** (`MachineCapacity`) has `ParallelCapacity` identical slots; it
  can run at most that many operations at the same time (the hard capacity
  constraint).

The objective is to decide *when* and *on which slot* every operation runs so that
jobs meet their target dates and the shop finishes quickly.

## 2. Time is integer seconds

Every duration and instant is a `long` number of **seconds from the planning
horizon** (second 0). There is no floating-point anywhere in the scheduling loop.

Why: the app runs in the browser (WebAssembly) but its tests run on desktop/CI.
Floating-point summation can differ in its last bits between runtimes; integers
cannot. Integer time guarantees that the schedule verified in CI is *bit-for-bit*
the schedule produced in the browser.

The one place `decimal` minutes are converted to integer seconds is the app's
mapping layer (`ProductionScheduleService.ToSeconds`), with an explicit
`MidpointRounding.ToEven`. Money and the cost rollups stay in `decimal` and never
enter the scheduler — cost is a display projection, not a scheduling input.

## 3. Parameters

All knobs live in `SchedulingParameters` (immutable):

| Parameter | Meaning |
| --- | --- |
| `DispatchRule` | priority rule on a contended work center (see §5) |
| `DueDateRule` | how target dates are assigned (see §4) |
| `TwkFlowFactor`, `NopSecondsPerOp`, `SlackSeconds`, `ConstantAllowanceSeconds` | per-rule due-date factors |
| `MultiStartRuns` | number of restarts; run 0 is the pure rule order |
| `LocalSearchMaxSteps` | budget for the local-search polish (0 disables it) |
| `Seed` | seed for the deterministic PRNG |
| `MakespanWeight`, `TardinessWeight`, `LatePenalty` | objective weights (see §7) |
| `MinutesPerWorkingDay` | **display only** — maps work-time onto calendar days for the Gantt; not a within-day capacity cut-off |

## 4. Target-date assignment ("meta")

`DueDateAssigner` gives each job a target completion date before scheduling. These
are the classic operations-research due-date rules (`release` = job release second,
`P` = total processing seconds, `n` = number of operations):

| Rule | Formula |
| --- | --- |
| **TWK** — Total Work Content | `due = release + factor · P` |
| **NOP** — Number of Operations | `due = release + secondsPerOp · n` |
| **SLK** — Equal Slack | `due = release + P + slack` |
| **CON** — Constant Allowance | `due = release + allowance` |
| **Explicit** | the job's own value, else falls back to CON |

The targets drive both the due-date dispatch rules (EDD, Critical Ratio) and every
lateness / tardiness KPI.

## 5. Dispatch scheduling

`DispatchScheduler` turns a **job priority order** (a permutation of the jobs) into
a concrete schedule with a simple list-scheduling loop:

```
for each work center: slotFreeAt[slot] = 0           // one clock per parallel slot
for each job in priority order:
    jobReadyAt = job.ReleaseSeconds
    for each step in the job (in sequence):
        slot   = the work center's earliest-free slot
        start  = max(jobReadyAt, slotFreeAt[slot])
        end    = start + step.DurationSeconds
        slotFreeAt[slot] = end
        jobReadyAt       = end
```

Two invariants hold by construction, which is what makes every output **feasible**:

- **precedence** — a step's `start` is `≥ jobReadyAt`, the end of the previous step;
- **capacity** — each slot's clock is strictly serial, so a work center never runs
  more than `ParallelCapacity` operations at once.

The scheduler is a pure function of `(context, order)` — no randomness, no shared
state — so it is trivially reproducible.

**Dispatch rules** (`PriorityOrdering`) produce the *initial* order by sorting jobs
on a key, with the job id as a deterministic tie-break:

| Rule | Key (ascending = higher priority) |
| --- | --- |
| FIFO | release time |
| SPT — shortest processing time | `P` |
| LPT — longest processing time | `−P` |
| EDD — earliest due date | `due` |
| CR — critical ratio | `due / P` (evaluated at the horizon) |
| WSPT — weighted shortest processing | `P / weight` |

## 6. Multi-start + local search

A single greedy pass is rarely optimal, so `SchedulingEngine` wraps the dispatcher
in a small, transparent metaheuristic (a GRASP-style scheme):

1. **Run 0** uses the pure rule order, so the result is *never worse than the
   dispatch rule on its own*.
2. **Runs 1…N−1** shuffle the order with a stream seeded from `(Seed, runIndex)`
   and keep the best by penalty. More starts can only help.
3. **Local search** (`LocalSearch`) then polishes the best order with
   first-improvement **adjacent swaps**, re-dispatching and re-scoring each
   neighbour. It accepts a neighbour only on a *strict* improvement and never
   overwrites the incumbent with something worse — so the final schedule is
   guaranteed `≤` the best multi-start result, which is `≤` the rule order.

Because local search perturbs the **priority order** (not the placed operations)
and re-runs the dispatcher, every candidate it ever looks at is a valid schedule.

> **Why the sample data has seven jobs.** On a two-job problem the search is
> effectively exhaustive, so every dispatch rule and every seed converge to the
> same optimum — changing them *looks* like it does nothing. With seven jobs
> competing for the same machines the search is no longer exhaustive, so the rule
> and the seed visibly change the schedule. To watch a rule's *raw* effect (before
> optimisation), set **multi-start = 1** and **local-search = 0**.

## 7. Scoring

`ScheduleEvaluator` rolls the schedule up into KPIs and a single penalty:

- **Makespan** — when the last operation finishes.
- **Tardiness** — per job `max(0, completion − due)`; reported as total and max.
- **On-time rate** — fraction of jobs meeting their target.
- **Utilisation** — busy ÷ (capacity × makespan) per work center, plus an average.
- **Penalty** (minimised by the search), computed in hours so the weights are
  intuitive:

  ```
  penalty = MakespanWeight · makespanHours
          + TardinessWeight · totalTardinessHours
          + LatePenalty     · lateJobCount
  ```

  With the defaults the late-job count dominates, then total tardiness, then
  makespan — i.e. *meet the targets first, then finish fast*.

## 8. Determinism

- All schedule arithmetic is integer.
- Randomness is a fixed-algorithm xorshift64\* PRNG (`DeterministicRandom`), not
  `System.Random` (whose algorithm is not stable across .NET versions). Each
  restart gets its own stream from `(Seed, runIndex)`, and the engine runs
  single-threaded.
- The priority order is canonical (sorted by key, then job id), so the schedule is
  even **independent of the order the jobs are handed to the engine**.

These properties are asserted directly by the tests (golden PRNG values, identical
schedule for a repeated run, identical schedule for reordered inputs).

## 9. How the app uses it

`ProductionScheduleService` (in the Blazor app) is the boundary:

1. load the **Released** work plans and active work centers from the in-browser
   database;
2. map operations to steps, converting `decimal` minutes to integer seconds and
   re-indexing step numbers so malformed data cannot break the engine's contract;
3. skip operations on inactive/missing work centers and plans left with no steps;
4. run `SchedulingEngine` and project the result into the Gantt rows, the per-job
   table and the KPI cards.

## 10. Scope & possible extensions

Kept out of scope on purpose, to stay simple and provably correct:

- **Backward (due-date-anchored) scheduling** — under shared finite capacity this
  needs a second scheduler and can produce infeasible plans; forward scheduling
  with due-date *dispatch* rules captures most of the value.
- **A working-day calendar** — the engine uses a continuous work-time axis;
  `MinutesPerWorkingDay` only buckets it into days for the Gantt.
- **Per-work-center machine counts** — the app maps every work center to one slot,
  though the engine already supports `ParallelCapacity > 1` (and the tests use it).
- **Sequence-dependent setup, lot-splitting, gap back-filling** — all natural next
  steps, none required for a clear, well-tested baseline.

---

*See the unit tests in
[`tests/WorkPlanStudio.Scheduling.Tests`](../tests/WorkPlanStudio.Scheduling.Tests)
for executable specifications of every guarantee described here.*
