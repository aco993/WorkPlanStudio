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
horizon** (second 0). **All placement arithmetic is integer** — starts, ends,
setup, pauses, window runs and blackouts. Floating point appears exactly once, in
the objective: `ScheduleScore.Penalty` is a `double` in hours, and the descent
compares two of them with an epsilon. That is deliberate and it is the whole
extent of it; the *schedule* is integer, which is what makes it reproducible.

Why: the app runs in the browser (WebAssembly) but its tests run on desktop/CI.
Floating-point summation can differ in its last bits between runtimes; integers
cannot. Integer placement guarantees that the schedule verified in CI is
*bit-for-bit* the schedule produced in the browser.

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
| `LocalSearchAcceptance` | which improving neighbour the descent adopts (see §6 and [ADR 0022](adr/0022-local-search-acceptance.md)) |

`SchedulingParameterLimits` validates all of them and rejects three things the
form can otherwise ask for: a run above `MaxMultiStartRuns` (64), a budget above
`MaxLocalSearchSteps` (20 000), and — the one that was missing — a **product**
above `MaxTotalEvaluations` (200 000). 64 × 20 000 is 1.28 million candidate
schedules, which no browser should be asked for by accident. It also bounds a
single step's duration and the planning horizon, so a mis-mapped quantity
overflows into a rejection rather than into a negative penalty.

There is no `MinutesPerWorkingDay` here any more. It was a Gantt-rendering
constant living in a library whose headline claim is that it has no UI concerns;
it is now a field on the scheduling page and travels beside the parameters
rather than inside them.

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

`Explicit` is an engine capability for callers that own a real per-job due date.
The current Blazor application intentionally hides it because `WorkPlan` is a
routing template and the app has no `ProductionOrder` due-date field. See
[ADR 0007](adr/0007-defer-production-order.md).

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
        slot   = the slot whose placement FINISHES earliest       // see §6a
        start  = the first moment ≥ max(jobReadyAt, slotFreeAt[slot])
                 at which the calendar can hold setup + duration
        end    = start + setup + step.DurationSeconds + pausedSeconds
        slotFreeAt[slot] = end
        jobReadyAt       = end
```

With no calendar and no change-over matrix the two middle lines collapse to
`start = max(jobReadyAt, slotFreeAt[slot])` and `end = start + duration`, which
is the classic list scheduler — the general form above is what §6a adds.

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

### The rules are not independent of the target rule

With every job released at second 0 and `P` = total processing time:

| Target rule | Sets | Therefore |
| --- | --- | --- |
| **TWK** | `due = f · P` | strictly increasing in `P`, so **EDD ≡ SPT**; `CR = due/P = f` is constant, so **CR ≡ FIFO** |
| **SLK** | `due = P + s` | **EDD ≡ SPT**; `CR = (P+s)/P` decreases in `P`, so **CR ≡ LPT** |
| **CON** | `due = c` | every target is equal, so **EDD ≡ FIFO**; `CR = c/P` decreases in `P`, so **CR ≡ LPT** |
| **NOP** | `due = t · n` | keyed on the operation count rather than the work content — the only rule that decouples all six |

So on the default TWK targets, the six dispatch rules produce **four** distinct
schedules. This is a property of the formulas, not a bug — but a user who changes
a dropdown and sees no change deserves to be told why, so
`PriorityOrdering.EquivalentRules` computes the collapse from the orders
themselves (never from a hard-coded table, which could drift from the code) and
the page shows it beneath the selector. `RuleEquivalenceTests` pins every
identity above; see [ADR 0009](adr/0009-report-rule-equivalences.md).

### The collapse depends on equal release times

Everything above assumes every job is released at second 0, which was true while
the app scheduled work plans — they carried no release date. Production orders
carry real ones, and a staggered release breaks most of the identities: with
`due = release + f·P`, the critical ratio `due/P = release/P + f` is no longer
constant, so CR stops sorting like FIFO.

On the current sample data no collapse occurs at all. That is a result, not an
omission: giving orders real release dates is what made the six dispatch rules
genuinely six rules. The reporting stays because the collapse returns the moment
orders share a release date — which is exactly why it is computed from the
orders rather than from the table above.


## 6. Multi-start + local search

A single greedy pass is rarely optimal, so `SchedulingEngine` wraps the dispatcher
in a small, transparent metaheuristic (a GRASP-style scheme):

1. **Restart 0** starts from the pure rule order, so the result is *never worse
   than the dispatch rule on its own*.
2. **Restarts 1…N−1** shuffle that order with a stream seeded from
   `(Seed, runIndex)`. More restarts can only help.
3. **Every restart** then runs a `LocalSearch` descent over the **insertion**
   (or-opt) neighbourhood: remove one job from the sequence and re-insert it at
   each other position — `n·(n−1)` neighbours per pass. The incumbent is never
   replaced by something worse, so the final schedule is guaranteed `≤` the rule
   order.

Because the search perturbs the **priority order** (not the placed operations)
and re-runs the dispatcher, every candidate it ever looks at is a valid schedule.

### Which improving neighbour to adopt

"Take the best strict improvement in the pass" (steepest descent) is one answer
of three, and it is no longer the default. `LocalSearchAcceptance` offers:

| Rule | What a pass does |
| --- | --- |
| `SteepestDescent` | evaluate every neighbour, adopt the single best |
| `FirstImprovement` | adopt the first improving neighbour and carry on from there |
| `BestInsertion` *(default)* | for each job in turn, move it to its own best position |

Measured over 5 sizes × 5 instances × 3 budgets at equal candidate count,
`BestInsertion` is **6.8 %** better than steepest descent on the mean of those
fifteen rows and `FirstImprovement` 5.6 %. Steepest descent wins every 50-job row,
which is one shape of one family and is reported rather than explained away. The
tables and the command that produces them are in
[ADR 0022](adr/0022-local-search-acceptance.md).

The budget also has to buy something, and now it does: ten times the candidate
count moves steepest descent 0.9 % and the new default 4.6 %.

### Why insertion and not adjacent swaps

The original implementation swapped adjacent jobs and accepted the first
improvement. It stalled after 7–16 of its 2000-neighbour budget, which was the
clue: an adjacent swap moves a job one position per improving step, so a job that
belongs ten places earlier is unreachable unless all ten intermediate positions
also improve. On a tardiness objective they usually do not.

Measured against brute-force enumeration of all `n!` **dispatch orders**, 20
random 8-job instances:

| | mean gap to the best dispatch order | worst | matched |
| --- | ---: | ---: | ---: |
| dispatch rule alone | 72.5 % | 127.5 % | 0/20 |
| adjacent swap + 8 restarts | 27.3 % | 62.8 % | 0/20 |
| **insertion + 8 restarts** | **0.2 %** | **3.0 %** | **19/20** |

Read the column heading carefully: that is the gap to the **best order the
dispatcher can be handed**, which is the search's own ceiling and not the optimum
of the scheduling problem. §6c measures the distance between the two, and it is
large. See [ADR 0008](adr/0008-insertion-neighbourhood.md).

A consequence worth knowing: a good search makes the *starting rule* matter much
less. Different dispatch rules now converge on the same schedule unless the
optimiser is turned off (multi-start = 1, local search = 0).

> **Why the sample data has seven jobs.** On a two-job problem the search is
> effectively exhaustive, so every dispatch rule and every seed converge to the
> same optimum — changing them *looks* like it does nothing. With seven jobs
> competing for the same machines the search is no longer exhaustive, so the rule
> and the seed visibly change the schedule. To watch a rule's *raw* effect (before
> optimisation), set **multi-start = 1** and **local-search = 0**.

## 6a. Calendars and change-over

Both are optional and both default to "no constraint", so an instance that sets
neither behaves exactly as it did before they existed.

### Availability windows

A work center may declare windows it is available in, plus the length of the
period they repeat over — `[08:00, 16:00)` with a 24-hour period is a day shift.

Calendars repeat deliberately. A finite list of windows either runs out
mid-schedule or forces the caller to materialise a year of them; a period makes
the calendar total without either. Windows live on the same abstract work-time
axis as everything else, so no time zone or daylight-saving rule enters the core.

Three refinements arrived with the working-time library ([ADR 0012](adr/0012-working-time-as-capacity.md)):
a **phase** shifts the pattern so a schedule may start on a Monday 06:00 rather
than at the period origin; **blackouts** are dated closed intervals with a tag
(a holiday, an absence, Sunday rest) that are never bridged; and a **bridgeable
gap** lets an operation pause across a short gap — a break — and resume, which
the result reports as paused seconds. Utilisation is measured against open
time, not the makespan.

An operation must fit **entirely inside one window** (or a run of windows joined
by bridgeable gaps) — there is no preemption across anything longer.
That is checked when the `SchedulingContext` is built, not during dispatch: the
search evaluates thousands of candidate orders, and an exception thrown from
inside that loop would abort the whole run rather than reporting an input problem
the caller can fix.

### Sequence-dependent setup

Each step belongs to a *family*, and a work center may declare what changing
between families costs. Same family costs nothing; an undeclared transition
costs nothing; the first operation on a slot costs nothing.

This is what makes the order of work on a machine matter beyond queueing —
running all the steel parts together and then all the aluminium ones beats
alternating. With a 2-hour change-over, four alternating jobs cost 6 hours of
setup against 2 hours when grouped.

It also changes how a slot is chosen. The dispatcher picks the placement that
**finishes earliest**, not the slot that is free earliest: a slot that frees
later but already ran this family can finish sooner than one that is free now
and needs a change-over.

## 6b. Exhaustive search over dispatch orders

`ExactDispatchOrderOptimizer` evaluates all `n!` job orders, up to 9 jobs
(362 880 dispatches, about a second).

Be precise about what it proves. It is exact **within the dispatch-order model**:
of every order the dispatcher can be handed, it returns the best. It is *not* a
general job-shop optimality proof — the dispatcher places each job greedily and
never back-fills idle gaps, so better schedules exist outside that model. On a
two-job, three-work-center instance its answer is **60 seconds where the optimum
is 40**, and `ExactSolverTests.The_counterexample_that_costs_the_permutation_optimiser_fifty_percent`
pins exactly that ratio.

It is kept because three implementations agreeing is evidence and one
implementation agreeing with itself is not: it is the second opinion on the
*search*, while §6c is the authority on the *optimum*.

## 6c. The optimum, proved

`WorkPlanStudio.Scheduling.Exact` is a **disjunctive-graph branch-and-bound** that
solves the scheduling problem rather than the dispatch-order problem. It shares no
code with the dispatcher: its own placement rule, its own relaxation bounds
(Jackson's preemptive schedule, job chains), its own propagation (time-window
tightening, overload, edge finding, not-first/not-last) and its own depth-first
search. It honours the whole model — releases, parallel capacity,
sequence-dependent change-over, repeating calendars with phases and bridgeable
gaps, blackouts, every due-date rule and the same weighted penalty — and where it
cannot answer it **refuses** by size rather than approximating.

It also emits the same instance as a CPLEX LP file (`MilpModelWriter`), so the
answer can be checked by a solver that trusts none of this code. No external
solver was run for the numbers below; the recipe is in the ADR and is labelled
untested.

On a fixed, committed, twenty-instance set spanning 2–8 jobs, 1–5 work centers,
1–3 slots, staggered releases, four due-date rules, change-over families, a day
shift, shifts with a bridgeable break and a two-day shutdown:

| | value |
| --- | ---: |
| instances proved optimal by the solver | **20 of 20** (1.26 s for the set) |
| engine matches the **best dispatch order** | **18 of 20**, mean gap **0.27 %** |
| engine matches the **true optimum** | **7 of 20** |
| gap to the true optimum | median **5.34 %**, mean **327 %**, worst **6 091 %** |
| the best dispatch order's own gap to the optimum | mean **326.76 %** |

Two honest readings of that table.

**The search is close to its ceiling; the ceiling is the model.** 0.27 % is close
enough to the 0.2 % of §6 that it is almost certainly the same quantity. The
engine finds the best order its dispatcher can be handed on eighteen of twenty
instances. Everything else in the gap is the dispatch-order model — one whole job
at a time, no back-filling — and a better metaheuristic would not touch it.

**The mean is dominated by one instance and the median is the number to read next
to it.** The objective charges a flat hundred points per late job. On `wide-5x4`
the optimum has no late job at all and the best dispatch order has two, so the
ratio is 61×. That is a real difference — two orders late instead of none — but
quoting only the mean would be its own kind of dishonesty.

```bash
dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- exact
```

reproduces the full per-instance table, and `OptimalityStudyTests` pins every
figure in it to six decimal places, asserts that all twenty optimal schedules pass
the independent feasibility checker, and asserts that the instance set still spans
the feature space rather than collapsing into twenty copies of one shape. The
whole record is [ADR 0015](adr/0015-exact-solver.md).

**Where it stops.** Twenty-one operations is a cliff rather than a slope: up to
eighteen every sampled instance is proved inside a second; at twenty-one and
twenty-four, three of five are proved in milliseconds and the other two are not
proved in a minute. Size is not the predictor — thirty-five operations have been
proved in 406 nodes and twenty-one left unproved after two hundred million. What
separates them is how much of the optimum the relaxation already knows.

The application can ask for the proof on the plan currently on screen, under a
wall-clock budget, and reports only what was proved: "optimal", or the gap to a
better schedule that was found, or the proved lower bound — "at most *n* % above
the best possible" — when the budget ran out.

## 7. Scoring

`ScheduleEvaluator` rolls the schedule up into KPIs and a single penalty:

- **Makespan** — when the last operation finishes.
- **Tardiness** — per job `max(0, completion − due)`; reported as total and max.
- **On-time rate** — fraction of jobs meeting their target.
- **Utilisation** — busy ÷ (capacity × open time) per work center, plus an average; closed time (shifts, breaks, Sundays, holidays, absences) does not count as idle.
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

- All placement arithmetic is integer.
- Randomness is a fixed-algorithm xorshift64\* PRNG (`DeterministicRandom`), not
  `System.Random` (whose algorithm is not stable across .NET versions). Each
  restart gets its own stream from `(Seed, runIndex)`, and the engine runs
  single-threaded.
- The priority order is canonical (sorted by key, then job id), so the schedule is
  even **independent of the order the jobs are handed to the engine** — and a
  duplicate job id is now rejected by name rather than tolerated, which is what
  makes that sentence true instead of nearly true.
- Slicing the run across browser turns changes nothing:
  `ScheduleRunnerTests.Slicing_the_run_does_not_change_the_schedule_it_produces`
  asserts the sliced and straight-through runs agree on schedule signature,
  penalty and step count.

These properties are asserted directly by the tests (golden PRNG values, identical
schedule for a repeated run, identical schedule for reordered inputs).

## 8a. What the engine refuses

A heuristic that quietly accepts nonsense produces a plausible schedule from it,
which is worse than an exception. `SchedulingContext` therefore validates at
construction and throws, naming the offending job or work center:

- duplicate job ids, duplicate work-center ids;
- a job with no target date — it used to be silently "never late";
- a negative release, a weight that is not finite and positive, a reference over
  80 characters;
- a change-over declared from a family to itself, which used to be dropped;
- a step longer than `MaxStepDurationSeconds` or a horizon past
  `MaxHorizonSeconds`, both with `checked` arithmetic behind them, because an
  unbounded duration used to overflow into a *negative* penalty the search then
  happily minimised towards.

The context also **copies** every collection it is handed, so a caller that
mutates its own list afterwards cannot change a context that has already been
validated. Anything mapping external data in — the app's `ScheduleMapper`, the CSV
import — has to satisfy these, and the app turns each one into a per-order
rejection with a sentence rather than a stack trace.

## 9. How the app uses it

`ProductionScheduleService` (in the Blazor app) is the boundary:

1. load the **released production orders** and active work centers from the
   in-browser database, with each order's frozen routing snapshot;
2. map operations to steps through `ScheduleMapper`, converting `decimal` minutes
   to integer seconds with banker's rounding — the one place in the repository
   that conversion happens;
3. reject an order **completely** when any of its operations cannot be mapped, with
   a stable reason code the page localises: an inactive or missing work center, an
   operation longer than the center's longest shift window, an operation longer
   than the engine's duration bound, or a work center the working-time rules have
   closed outright;
4. build one `MachineCalendar` per work center from the plant's rules and that
   center's shift pattern and absences (`ShopCalendar`);
5. run the engine through `IScheduleRunner`, which slices the multi-start loop so
   the browser keeps painting and the Cancel button works
   ([ADR 0019](adr/0019-off-thread-scheduling.md));
6. project the result into the Gantt rows with their closed segments, the per-job
   table and the KPI cards — and, on request, into a PDF, an Excel workbook or a
   CSV file ([ADR 0017](adr/0017-in-browser-export.md)).

## 10. Scope & possible extensions

Kept out of scope on purpose, to stay simple and provably correct:

- **Backward (due-date-anchored) scheduling** — under shared finite capacity this
  needs a second scheduler and can produce infeasible plans; forward scheduling
  with due-date *dispatch* rules captures most of the value.
- **Per-work-center machine counts in the app** — the app maps every work center
  to one slot, though the engine supports `ParallelCapacity > 1` (and the tests,
  the exact solver and the ADR 0015 study all use it).
- **Gap back-filling** — and §6c is now the measurement that says how much it is
  worth. The dispatcher places each job's operations in sequence and never
  inserts later work into an earlier idle window, which is the whole of the
  327 %-mean / 5.3 %-median distance between the best dispatch order and the
  optimum. It is also the property the search depends on — "the order determines
  the schedule" is what makes every candidate feasible by construction — so it is
  a redesign, not a patch.
- **Lot-splitting**, and a **cumulative edge finder** so a work center with several
  slots gets more than an overload check (it is why one twelve-operation instance
  in the study costs 174 562 nodes), and **setup-aware relaxation bounds**. All
  three are named in [ADR 0015](adr/0015-exact-solver.md) as the next things to do.

---

*See the unit tests in
[`tests/WorkPlanStudio.Scheduling.Tests`](../tests/WorkPlanStudio.Scheduling.Tests)
for executable specifications of every guarantee described here.*
