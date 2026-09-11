# 15. Prove the optimum with a disjunctive branch-and-bound, and label what was not proved

- **Status:** Accepted
- **Date:** 2026-09-11
- **Replaces** the role `ExhaustiveDispatchOrderSearch` played as the optimality oracle; that class
  stays, as a second opinion on the *search*
- **Related:** [ADR 0008](0008-insertion-neighbourhood.md) and
  [ADR 0022](0022-local-search-acceptance.md), whose measurements are against the dispatch-order
  reference this record replaces; [ADR 0001](0001-pure-scheduling-library.md), which is why there is
  no solver package here; [ADR 0010](0010-periodic-calendars-and-setup-families.md), whose calendar
  and change-over model the solver has to honour exactly

## Context

The library shipped a class called `ExhaustiveDispatchOrderSearch`. It enumerates all `n!` **job
orders** and hands each one to the same greedy dispatcher the heuristic uses, then returns the best.
Within that model it is exact. The model is the problem: the dispatcher places one whole job at a
time and never back-fills, so the schedules it can emit are a small, awkwardly shaped subset of the
feasible ones.

Two jobs, three work centers, ten seconds an operation:

```
J1 = M1 → M2 → M3        J2 = M3 → M2 → M1
best over both job orders: 60 s
a schedule any first-year OR student writes down:
    J2 0-10 M3 | J1 0-10 M1, 10-20 M2, 20-30 M3 | J2 20-30 M2, 30-40 M1   = 40 s
```

Fifty per cent. And `OptimalityTests` used that class as its own oracle, so both sides of
`Assert.Equal(optimum, found)` ran the same `DispatchScheduler` and the same `ScheduleEvaluator`:
any bug in placement or scoring cancelled exactly, and the suite could only ever catch a
search-order bug — the one class of bug it was not advertised to catch. Meanwhile eight documents
said "0.2 % mean gap, 19 of 20 instances solved exactly" and attributed it to that test, which
computes neither figure on no such set.

## Decision

Add `src/WorkPlanStudio.Scheduling/Exact/`: a **disjunctive-graph branch-and-bound** that solves the
job shop as a scheduling problem, plus a **CPLEX LP-format writer** so the answer can be checked by
somebody who does not trust any of this code, plus a **fixed twenty-instance study** so the
published numbers are measurements.

### Why branch-and-bound, and not a MILP solved in process

Writing a big-M MILP is easy; solving one is not. Doing it here would mean shipping a simplex
implementation, a cut generator and a branch-and-cut driver — several thousand lines whose failure
mode is a wrong number rather than a compile error — to solve instances a specialised job-shop
search handles in milliseconds. The disjunctive formulation also has a known weakness the
branch-and-bound does not share: its linear relaxation is poor, because big-M rows relax to nothing.
The branch-and-bound's bounds are combinatorial (Jackson's preemptive schedule, job chains), which
is why twenty-one operations are proved in a second rather than surrendered to a weak LP bound.

So: the MILP is **emitted, not solved**. `MilpModelWriter` writes the same instance in a format any
solver reads, which is the part of a MILP that is actually worth having here — an independent check
that costs a file rather than a dependency.

### Why not OR-Tools (or any other solver package)

`WorkPlanStudio.Scheduling` targets WebAssembly and has no dependency but the base class library
(ADR 0001, guarded by `ArchitectureTests`). OR-Tools' CP-SAT is a native library with no
WebAssembly build; taking it would move scheduling to a server and delete the property the whole
project is built around — that the engine runs in the browser with no backend. The same argument
rules out Gurobi, CPLEX and HiGHS as in-process dependencies. It does not rule them out as
*reviewers*, which is what the LP writer is for.

### The search

The routing arcs are fixed by the problem. What is left to decide is the order in which operations
that compete for a work center use it, and which of its parallel slots each one takes. A node
extends the partial schedule by one operation: an operation whose routing predecessor is already
sequenced is appended to the end of one slot's sequence, at the earliest moment the calendar, the
change-over and its two predecessors allow.

That is exact rather than merely thorough, and the argument is three steps:

1. Every feasible schedule induces an order per slot.
2. The schedule that starts every operation as early as that order permits has completion times no
   later than the original, because a placement's end is monotone in the moment it may start. A
   change-over depends on the order and not on the timing, so it survives this step untouched.
3. The objective is a non-negative weighted sum of makespan, total tardiness and late jobs, each
   non-decreasing in completion times.

So the best schedule for an order is the one the search builds, and the search reaches every order.

**Bounds.** Two families, because they fail in different places.
`RelaxationBounds` computes, for every unsequenced operation, the earliest it can start given the
routing so far and the earliest its work center can take anything. From those heads it takes a
**job bound** (the longest chain through each routing's remainder) and a **machine bound** — on a
single-slot work center, Jackson's preemptive schedule, which relaxes the machine to allow
preemption, runs the released operation with the longest tail, and reads off `max(completion +
tail)`; on a work center with several slots, the volume argument `min head + ⌈work / slots⌉ + min
tail`. Both ignore change-over and calendars, which is what makes them a relaxation and therefore a
lower bound.

**Propagation.** `DisjunctivePropagator` tightens time windows along each routing, then applies the
**overload** check and, on single-slot work centers, **edge finding**, **not-first** and
**not-last** over task intervals. The deadlines it pushes against come from the incumbent: whatever
objective is still unspent buys a makespan deadline, a per-job completion deadline, and — where the
flat late-job penalty alone would exhaust the remainder — a hard "this job cannot become late".

**The state memo.** Placing A then B and placing B then A reach the identical shop. Without a memo
the tree explores both, and the duplication compounds with depth. `SearchStateMemo` stores each
expanded state in full and compares exactly, never by hash: a hash set would be smaller, faster, and
would eventually prune a state it had never seen and return a wrong answer with a confident label
on it.

### Saying what was proved

`ExactSolution.Status` is `Optimal`, `FeasibleWithGap` or `NoSolutionFound`, with `BestBound`
alongside `Penalty`. `OptimalPenalty` throws unless the status is `Optimal`, so the difference
between "found" and "proved" cannot be dropped by accident — which is precisely how a 50 % gap came
to be quoted as an optimality result.

The limit that matters is `NodeLimit`, because it is deterministic: the same instance under the same
node limit explores the same tree and returns the same answer on every machine and in every runtime.
`TimeLimit` is the opposite and is off by default; it exists for an interactive caller and it makes
the answer depend on how busy the machine was. `ExactSolverOptions.Interactive` is the browser
preset (50 000 nodes, a small memo). An instance larger than `MaxOperations` is **refused** rather
than answered approximately under an exact-sounding name.

## What the solver cannot do

* **Nothing in the model, at any size, is approximated.** Releases, per-work-center parallel
  capacity, sequence-dependent change-over, repeating calendars with phases and bridgeable gaps,
  blackouts, every due-date rule and the full weighted penalty are all handled exactly. The
  restrictions are on *size*, and they are refusals.
* **Edge finding is a unary-resource rule** and is applied only to single-slot work centers. A work
  center with several slots gets the overload check scaled by its slot count and nothing else. This
  costs search time, never correctness.
* **The bounds ignore change-over and calendars.** A relaxation is what a bound has to be, but it
  does mean an instance with a heavy change-over matrix or a sparse calendar prunes less well —
  visible in the node counts of `parallel-6x2-cap2` and `mixed-5x3` below.
* **A node-limited answer carries a weak bound.** A depth-first search that stops has no cheap
  description of its frontier, so `BestBound` on a `FeasibleWithGap` answer is close to the root
  relaxation. It is a true bound and it is not a tight one.
* **The LP writer refuses two things.** Availability windows and blackouts are not expressible on a
  continuous start variable without the time-indexed model this record rejects. A work center with
  both several parallel slots and a change-over matrix is refused for a narrower reason: the
  change-over arcs form one path per slot and the number of operations on a slot is itself a
  decision. Sixteen of the twenty study instances are written; four say why not.

## Measurements

Everything below is from this machine — .NET 10.0.9, Windows 11 26200, 28 logical cores, Release —
and every table is one command.

```bash
dotnet build WorkPlanStudio.slnx -c Release --nologo
dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- exact
dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- exactwall
dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- milp ./milp
dotnet run -c Release --project tests/WorkPlanStudio.Benchmarks -- --filter "*ExactSolverBenchmarks*" --job short
```

### The twenty-instance study (`-- exact`)

`Optimum` is proved. `Best order` is the best of all `n!` job orders through the dispatcher —
the reference this project used to measure against. `Engine` is `SchedulingEngine` at its shipped
defaults. `Model gap` is what the dispatch-order model costs; `Search gap` is what the search costs.

| Instance | Jobs | Ops | WC | Optimum | Best order | Engine | Gap | Model gap | Search gap | Nodes | ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `flow-2x3` | 2 | 6 | 3 | 106.1667 | 106.1667 | 106.1667 | 0.00 % | 0.00 % | 0.00 % | 11 | 6.6 |
| `jobshop-3x3` | 3 | 9 | 3 | 213.2500 | 213.2500 | 213.2500 | 0.00 % | 0.00 % | 0.00 % | 52 | 0.9 |
| `jobshop-4x3-released` | 4 | 12 | 3 | 3.2500 | 3.2500 | 3.2500 | 0.00 % | 0.00 % | 0.00 % | 66 | 0.2 |
| `jobshop-5x3` | 5 | 15 | 3 | 112.3333 | 230.8333 | 230.8333 | 105.49 % | 105.49 % | 0.00 % | 1 455 | 7.4 |
| `jobshop-6x3` | 6 | 18 | 3 | 118.6667 | 246.8333 | 253.8333 | 113.90 % | 108.01 % | 2.84 % | 9 540 | 54.9 |
| `jobshop-7x3` | 7 | 21 | 3 | 244.9167 | 371.4167 | 371.4167 | 51.65 % | 51.65 % | 0.00 % | 535 922 | 1 040.2 |
| `bottleneck-6x2` | 6 | 12 | 2 | 438.1667 | 441.5000 | 441.5000 | 0.76 % | 0.76 % | 0.00 % | 1 259 | 1.4 |
| `wide-5x4` | 5 | 20 | 5 | 3.5833 | 221.8333 | 221.8333 | 6 090.70 % | 6 090.70 % | 0.00 % | 145 | 0.1 |
| `single-machine-8` | 8 | 8 | 1 | 705.5833 | 705.5833 | 705.5833 | 0.00 % | 0.00 % | 0.00 % | 1 155 | 0.4 |
| `parallel-6x2-cap2` | 6 | 12 | 2 | 109.8333 | 212.5833 | 212.5833 | 93.55 % | 93.55 % | 0.00 % | 174 562 | 56.1 |
| `parallel-6x3-cap3` | 6 | 18 | 3 | 1.6667 | 1.6667 | 1.6667 | 0.00 % | 0.00 % | 0.00 % | 333 | 0.1 |
| `setup-2fam-5x2` | 5 | 10 | 2 | 339.1667 | 360.6667 | 360.6667 | 6.34 % | 6.34 % | 0.00 % | 1 132 | 0.7 |
| `setup-3fam-6x2` | 6 | 12 | 3 | 349.0833 | 455.9167 | 467.6667 | 33.97 % | 30.60 % | 2.58 % | 1 091 | 0.7 |
| `setup-heavy-4x3` | 4 | 12 | 2 | 363.1667 | 429.5833 | 429.5833 | 18.29 % | 18.29 % | 0.00 % | 933 | 0.7 |
| `calendar-dayshift-4x3` | 4 | 12 | 3 | 8.8333 | 10.1667 | 10.1667 | 15.09 % | 15.09 % | 0.00 % | 52 | 0.2 |
| `calendar-breaks-5x2` | 5 | 10 | 2 | 9.0833 | 9.0833 | 9.0833 | 0.00 % | 0.00 % | 0.00 % | 208 | 0.2 |
| `calendar-shutdown-4x3` | 4 | 12 | 3 | 10.0000 | 10.0000 | 10.0000 | 0.00 % | 0.00 % | 0.00 % | 161 | 0.1 |
| `duedates-tight-6x2` | 6 | 12 | 3 | 211.0833 | 220.2500 | 220.2500 | 4.34 % | 4.34 % | 0.00 % | 158 | 0.1 |
| `duedates-slack-7x2` | 7 | 14 | 3 | 346.4167 | 351.5833 | 351.5833 | 1.49 % | 1.49 % | 0.00 % | 2 289 | 2.3 |
| `mixed-5x3` | 5 | 15 | 3 | 8.4167 | 9.1667 | 9.1667 | 8.91 % | 8.91 % | 0.00 % | 206 144 | 83.1 |

**20 of 20 proved optimal, 1 256 ms of solver time for the whole set.** The engine matches the true
optimum on **7 of 20**: mean gap **327.22 %**, median **5.34 %**, worst **6 090.70 %**. It matches
the best dispatch order on **18 of 20**, mean search gap **0.27 %**. The best dispatch order is
itself **326.76 %** above the optimum on average.

Two things about those numbers.

*The search was never the problem.* 0.27 % is close enough to the "0.2 %" the documentation quoted
that it is probably the same quantity, honestly measured against the wrong reference. The engine
finds the best order its dispatcher can be handed on eighteen of twenty instances. Everything else
is the model.

*The mean is dominated by one instance, and the median is the number to read next to it.* The
objective charges a flat hundred points per late job. On `wide-5x4` the optimum has no late job at
all (3.58 is makespan alone) and the dispatcher's best is two late jobs, so the ratio is 61×. That
is a real difference — two orders delivered late instead of none — but it is a ratio between a very
small number and a moderate one, and quoting only the mean would be its own kind of dishonesty.

### Where the exponential wall is (`-- exactwall`)

Makespan-dominated job shops — target dates loose enough that nothing is late — because that is the
hard case. With tight dates the job bound is often already the optimum and the search proves it in
a few dozen nodes; with loose dates the objective is the makespan alone and the bound has much less
to say. Limits: 200 million nodes, a one-million-state memo, sixty seconds.

**How many of five instances of each size are proved, and inside what**
(`-- exactbudget`; wall-clock limits, so this is the one measurement here that
does not reproduce exactly — the node counts do):

| Jobs | Steps | WC | Ops | Proved ≤ 1 s | Proved ≤ 60 s | Median nodes when proved |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 3 | 3 | 12 | 5/5 | 5/5 | 52 |
| 5 | 3 | 3 | 15 | 5/5 | 5/5 | 60 |
| 6 | 3 | 3 | 18 | 5/5 | 5/5 | 113 |
| 7 | 3 | 3 | 21 | **3/5** | **3/5** | 141 |
| 8 | 3 | 3 | 24 | **3/5** | **3/5** | 200 |

**The wall is at twenty-one operations on this family, and it is a cliff rather
than a slope.** Up to eighteen operations every instance is proved inside a
second. At twenty-one and at twenty-four, three of five are proved in
milliseconds and the other two are not proved in a minute — the median of the
ones that finish is a couple of hundred nodes. There is no middle. The sweep
stops at twenty-four because every further size costs a minute per unproved
instance and the shape of the answer is already clear.

**Size is not the predictor** (`-- exactwall`, one instance per shape, 200 million
nodes or sixty seconds):

| Jobs | Steps | WC | Ops | Status | Nodes | ms |
| ---: | ---: | ---: | ---: | --- | ---: | ---: |
| 3 | 3 | 3 | 9 | Optimal | 44 | 7 |
| 4 | 3 | 3 | 12 | Optimal | 39 | 0 |
| 5 | 3 | 3 | 15 | Optimal | 102 | 0 |
| 6 | 3 | 3 | 18 | Optimal | 113 | 0 |
| 7 | 3 | 3 | 21 | FeasibleWithGap | 200 000 000 | 42 728 |
| 8 | 3 | 3 | 24 | FeasibleWithGap | 84 375 919 | 60 000 |
| 9 | 3 | 3 | 27 | Optimal | 487 | 2 |
| 10 | 3 | 3 | 30 | FeasibleWithGap | 142 820 226 | 60 000 |
| 4 | 4 | 3 | 16 | Optimal | 50 | 0 |
| 5 | 4 | 3 | 20 | Optimal | 167 | 0 |
| 6 | 4 | 3 | 24 | Optimal | 293 | 0 |
| 7 | 4 | 3 | 28 | Optimal | 213 | 0 |
| 5 | 4 | 4 | 20 | Optimal | 128 | 0 |
| 6 | 4 | 4 | 24 | Optimal | 112 | 0 |
| 7 | 4 | 4 | 28 | Optimal | 341 | 0 |
| 8 | 4 | 4 | 32 | Optimal | 998 | 1 |
| 6 | 5 | 5 | 30 | Optimal | 1 767 | 5 |
| 7 | 5 | 5 | 35 | Optimal | 406 | 0 |
| 8 | 5 | 5 | 40 | FeasibleWithGap | 200 000 000 | 47 689 |

Thirty-five operations in 406 nodes, twenty-one in two hundred million. What
separates them is not the size but how much of the optimum the relaxation already
knows. When one routing's own work content is the binding constraint — a long job
on a wide shop — the root bound *is* the optimum and the proof is immediate at any
size. When several jobs contend for few work centers and the target dates are
loose enough that nothing is late, the objective is the makespan alone, every
one-dimensional bound is far below it, and the search is exponential in the way
the literature says a job shop is.

That is also why the twenty-instance study above proves all twenty in 1.3
seconds: its target dates are tight enough to give the bound something to hold
on to. It is the honest limit of the study and it is stated rather than hidden —
the set measures the heuristic where the truth is computable, which is not where
the heuristic is normally used.

### What the settings buy

Measured on the study's seven-job instance, which is the hardest in the set:

| State memo capacity | Status | Nodes | Time |
| ---: | --- | ---: | ---: |
| 16 384 | FeasibleWithGap | 20 000 000 | 16.5 s |
| 65 536 | FeasibleWithGap | 20 000 000 | 15.9 s |
| **131 072** | **Optimal** | **535 922** | **0.81 s** |
| 262 144 | Optimal | 535 922 | 0.81 s |
| 1 048 576 | Optimal | 535 922 | 0.81 s |

The memo does not degrade gently; it has a cliff. The default is the first power of two comfortably
past it (262 144), which costs about 42 MB on that instance — memory taken on demand, so a
three-job instance still costs kilobytes. `ExactSolverOptions.Interactive` deliberately does not use
it.

### Allocation

The engine stream took a candidate schedule from 69 032 B to 0 B; an exact solver that allocated
per node would undo that at a million nodes a second. Measured on an eighteen-operation instance:

| | Nodes | Allocated | Per node |
| --- | ---: | ---: | ---: |
| memo off | 69 354 | 10 672 B | **0.15 B** |
| memo on | 2 830 | 244 464 B | 86 B |

The search loop allocates nothing: one state, one set of bound workspaces, one extension buffer,
all sized from the instance and overwritten in place. Everything the second row reports is the memo
— twenty-four times fewer nodes for the memory it holds.
`ExactSolverTests.The_search_loop_itself_allocates_nothing_per_node` pins the first row.

## Checking an instance with a free solver

The LP writer exists so this document is not the last word. Neither HiGHS nor CBC is installed on
the machine these figures were measured on, **so no external solver was run here** — the recipe
below is offered untested, which is the honest state of it.

```bash
dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- milp ./milp

# HiGHS (pip install highspy, or the standalone binary)
highs --model_file ./milp/jobshop-5x3.lp

# CBC
cbc ./milp/jobshop-5x3.lp solve solu /dev/stdout
```

The objective a solver reports is the engine's penalty directly — the model's coefficients are
`MakespanWeight / 3600`, `TardinessWeight / 3600` and `LatePenalty`, so the optimum of
`jobshop-5x3.lp` should be **112.3333**, the `Optimum` column above.

Inside the repository the same model is checked two ways, neither of which needs a solver.
`ExactMilpWriterTests.The_proved_optimum_satisfies_every_constraint_at_the_reported_objective`
substitutes the branch-and-bound's optimal schedule into every row and requires all of them to hold
at exactly the reported objective — that catches a formulation that is too tight.
`The_models_own_optimum_is_the_one_the_branch_and_bound_proved` enumerates every binary assignment
of a small model and solves the difference-constraint system each one leaves behind, which computes
the model's true optimum with no solver and no shared code — that catches a formulation that is too
loose.

## Consequences

- ✅ The project can now say something true about optimality, and say it with a command attached.
  `OptimalityStudyTests` pins every figure in the study table to six decimal places; changing the
  engine means re-running the tool and updating this record, which is the point.
- ✅ `OptimalityTests` is no longer circular. It measures the engine against the branch-and-bound
  for the optimum and against the permutation enumerator for the search, and names which is which.
  The permutation enumerator stays because three implementations agreeing is evidence and one
  implementation agreeing with itself is not.
- ✅ The counterexample is in CI.
  `ExactSolverTests.The_counterexample_that_costs_the_permutation_optimiser_fifty_percent` asserts
  60 seconds and 40 seconds on the same instance, so the gap is a fact rather than a footnote.
- ➖ **The documented optimality numbers get much worse, and they were the project's headline.**
  "Solves 19 of 20 instances exactly" becomes "finds the best schedule its dispatcher can produce on
  18 of 20, and the true optimum on 7 of 20". That is the correction, not a regression.
- ➖ The solver is exponential and eighteen to twenty-four operations is where a second runs out on
  a hard instance. It answers questions about a work cell, not about a plant.
- ➖ A third scheduling implementation now has to stay in step with the model: a new constraint has
  to reach the dispatcher, the exact solver and — if it is expressible — the LP writer. The
  feasibility checker in the test suite is what keeps the first two honest about each other.
- ➕ The older class was renamed, from `ExactDispatchOrderOptimizer` to
  `ExhaustiveDispatchOrderSearch`. It is a public API break on a pre-1.0 library, and worth it: the
  word "exact" is the one that made a 50 % gap readable as an optimality result, and a doc-comment
  stating the limitation does not help a reader who only ever sees the call site. "Exhaustive" is
  what it actually is — it enumerates every dispatch order — and it no longer claims the rest.
