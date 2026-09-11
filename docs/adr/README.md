# Architecture Decision Records

**English** · [Deutsch](README.de.md)

Short, dated records of the decisions that shaped this project — the *why* behind the
structure, not just the *what*. Each one captures the context, the decision and its
consequences, in the spirit of
[Michael Nygard's ADRs](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

Several carry a dated amendment recording where a *later* decision dissolved their own
premise — 0008's optimality reference, 0012's averaging paragraph, 0006's schema number.
That is what a decision log is for, and it is why the earlier records are amended in
place rather than quietly rewritten.

The record bodies are English only. [README.de.md](README.de.md) is the German index,
with a one-line summary of each.

| # | Decision | Status |
| --- | --- | --- |
| [0001](0001-pure-scheduling-library.md) | Keep the scheduling engine a pure, dependency-free library | Accepted |
| [0002](0002-integer-seconds-time.md) | Model all internal time as integer seconds | Accepted |
| [0003](0003-forward-only-scheduling.md) | Ship forward scheduling only (no backward pass) | Accepted |
| [0004](0004-deterministic-prng.md) | Use a hand-rolled deterministic PRNG, not `System.Random` | Accepted |
| [0005](0005-explainable-scheduling-and-optional-ai.md) | Deterministic explanation first; AI is an optional narrator | Accepted |
| [0006](0006-explicit-browser-storage-recovery.md) | Explicit browser-storage recovery instead of migrations | Accepted |
| [0007](0007-defer-production-order.md) | Defer ProductionOrder and constrain scheduling scope | Superseded by 0011 |
| [0008](0008-insertion-neighbourhood.md) | Search the insertion neighbourhood, not adjacent swaps | Accepted |
| [0009](0009-report-rule-equivalences.md) | Report dispatch-rule equivalences instead of hiding them | Accepted |
| [0010](0010-periodic-calendars-and-setup-families.md) | Model calendars as a repeating period, and setup by family | Accepted |
| [0011](0011-production-orders-own-routing-snapshots.md) | Schedule production orders that own an immutable routing snapshot | Accepted |
| [0012](0012-working-time-as-capacity.md) | Model working time and German labour law as capacity, in a second pure library | Accepted |
| [0013](0013-personas-through-the-real-authorization-pipeline.md) | Personas through the real authorization pipeline, without a backend | Accepted |
| [0014](0014-schedule-chat-on-device-first-with-pluggable-models.md) | A conversation over the schedule: on-device first, models pluggable | Accepted |
| [0015](0015-exact-solver.md) | Prove the optimum with a disjunctive branch-and-bound, and label what was not proved | Accepted |
| [0016](0016-cost-centre-master-data.md) | Promote the cost centre from a string to master data | Accepted |
| [0017](0017-in-browser-export.md) | Write the CSV, the workbook and the PDF by hand, in the browser | Accepted |
| [0018](0018-csv-import.md) | A CSV import that refuses to write before you have read the preview | Accepted |
| [0019](0019-off-thread-scheduling.md) | Slice the scheduling run on the one thread there is, rather than pretend to leave it | Accepted |
| [0020](0020-optional-backend-and-real-auth.md) | An optional backend, and the first real authentication | Accepted |
| [0021](0021-visual-baselines-linux-only.md) | Maintain visual baselines for one operating system, and fail on a missing one | Accepted |
| [0022](0022-local-search-acceptance.md) | Which improving neighbour the local search adopts | Accepted |
| [0023](0023-accessible-gantt-and-responsive-tables.md) | Make the chart operable without a mouse and stop the cards clipping the tables | Accepted |
| [0024](0024-arbzg-in-the-ui.md) | Put the Arbeitszeitgesetz on the screen, and only as far as it is computed | Accepted |
| [0025](0025-hostile-input-on-the-model-path.md) | Hostile input on the model path: what a browser-only app can promise about a key and about prompt injection | Accepted |
