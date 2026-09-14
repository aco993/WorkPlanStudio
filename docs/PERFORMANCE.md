# Performance

**English** · [Deutsch](PERFORMANCE.de.md)

Three kinds of measurement, each with its own tool and its own honesty caveat.
None of them is a production capacity claim; all of them are reproducible with
one command.

| What | Tool | Where | Gate? |
| --- | --- | --- | --- |
| Engine, candidate and working-time throughput | BenchmarkDotNet (`tests/WorkPlanStudio.Benchmarks`) | [`performance.yml`](../.github/workflows/performance.yml), weekly + on `main` | reported |
| Regression tripwires | xUnit budgets (`PerformanceBudgetTests` in both library test projects) | `performance.yml` on `main`, weekly, on demand | **fails** the build |
| Page load of the published site | Lighthouse CI against the published `wwwroot`, served the way GitHub Pages serves it | `performance.yml` | accessibility, best practices, SEO and layout shift **fail**; the performance score is a warning |

## The machine these numbers come from

Every figure below was measured on **2026-09-11**, on the tip this document ships
with: Intel Core i7-14700K (28 logical, 20 physical cores), Windows 11 26200,
.NET SDK 10.0.301 / runtime 10.0.9, BenchmarkDotNet 0.15.8, `--job short`
(3 warm-up + 3 measured iterations). They are indicative and comparable only
against themselves on the same machine — a browser on a phone is a different
world, and the section on page load is the one that speaks for it.

```bash
dotnet run -c Release --project tests/WorkPlanStudio.Benchmarks -- --filter '*' --job short
```

## Whole runs

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| small: 25 jobs, 4 starts, 500 local steps | 1.33 ms | 19.1 KB |
| medium: 100 jobs, rule only (one dispatch of 600 operations) | 31.2 µs | 84.9 KB |
| medium: 100 jobs, 8 starts, 2 000 local steps | 91.1 ms | 91.7 KB |
| medium + shift calendars, breaks, blackouts | 239.9 ms | 91.8 KB |
| large: 250 jobs, 16 starts, 5 000 local steps | 1.59 s | 289.3 KB |

**The allocation column is the interesting one, and it is flat.** The medium run
evaluates 16 008 candidate orders and allocates 92 KB in total — not per
candidate, in total. It used to allocate about a gigabyte, because every
candidate built a whole `Schedule` object and then computed every KPI on it. The
search now scores candidates into a workspace it reuses for the whole run and
materialises exactly one `Schedule`: the one it keeps.

## Per candidate

The unit the entire search is built out of, measured directly rather than divided
out of a whole run — which is how the figure this document used to publish came
to be wrong by a factor of 7.6:

| Jobs | score one candidate into the workspace | dispatch and materialise a `Schedule` | score, then roll up every KPI |
| ---: | --- | --- | --- |
| 25 | 1.53 µs · **0 B** | 2.89 µs · 17 128 B | 3.78 µs · 18 824 B |
| 100 | 6.10 µs · **0 B** | 13.34 µs · 66 624 B | 13.35 µs · 68 320 B |
| 250 | 15.79 µs · **0 B** | 27.30 µs · 165 624 B | 33.06 µs · 167 400 B |

The first column is what local search calls, tens of thousands of times per run.
The third is what it *used* to call. `AllocationBudgetTests` pins the zero, so it
is a property rather than a fact about one afternoon.

Reading the rest of the table:

- **One dispatch is cheap.** 31.2 µs places 600 operations, builds the schedule
  and computes every KPI. Everything above it is the optimiser.
- **Calendars cost 2.6×** on the medium problem — 91.1 ms without them, 239.9 ms
  with shifts, breaks and blackouts — because every placement walks the window
  runs it crosses, bridges breaks and skips blackouts. It allocates nothing extra.
  Still interactive, and it is the honest price of the feature that makes the
  schedule mean anything.
- **Utilisation, mean flow, on-time rate and max tardiness are computed once**, for
  the schedule that is kept, not for every candidate. They scaled with the
  planning horizon rather than with the instance, which made them 29 % of a
  candidate's cost for information the search never reads.

## The working-time library

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| holidays: all 16 states, one year *(memoised — see below)* | 81.2 ns | 88 B |
| timeline: three-shift pattern, 400 days → machine calendar | 14.2 µs | 89.5 KB |
| timeline: one shift with an absence, 400 days | 6.0 µs | 39.3 KB |

**The holiday row measures the cache, not the computation**, and saying so matters
because the number is otherwise meaningless: `GermanHolidays` memoises by
`(year, state, partial)` and the benchmark asks for 2026 on every iteration, so
after the first one it is a dictionary lookup. The computation is bounded instead
by a tripwire — `Holidays_for_every_state_and_a_decade_compute_within_their_budget`
computes ten uncached years × 16 states inside **50 ms** — and no per-call figure
for it is published here, because the benchmark as committed does not isolate one.

Working time is otherwise free at this scale: the app rebuilds a 400-day calendar
for every work center before every run rather than caching anything.

## Regression tripwires

`PerformanceBudgetTests` are not benchmarks. Their bounds sit roughly an order of
magnitude above the measurements above — the medium problem under 3 s, with
calendars under 4 s, one large rule-only dispatch under 0.5 s, a 400-day timeline
under 50 ms, `Segments` over the whole five-year range under 150 ms, the
compliance evaluation over 400 days under 100 ms — measured after a warm-up run. A
pass proves nothing precise; a failure means something went quadratic, which is
the regression worth catching without the noise of a benchmark on a shared runner.

They run in [`performance.yml`](../.github/workflows/performance.yml) on `main`,
weekly and on demand, and are **excluded from the pull-request gate**: a
wall-clock tripwire that reddens one pull request a month teaches the author to
re-run rather than to look, and then it is noise rather than signal.

## Page load

The app ships the .NET runtime, EF Core and SQLite as WebAssembly. Published, that
is **103 files and 19.8 MB uncompressed**, of which the pre-compressed Brotli
siblings total **5.8 MB** — so its Lighthouse performance score is what a
WebAssembly runtime scores, and it is reported rather than gated.

Measured on 2026-09-11 with Lighthouse 13.4.1, desktop preset, against the
published `wwwroot` served with Brotli the way `performance.yml` serves it:

| Page | Performance | Accessibility | Best practices | SEO | FCP | LCP | TBT | CLS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `/` | 36 | 100 | 100 | 100 | 0.3 s | 7.7 s | 2 134 ms | 0.000 |
| `/schedule` | 36 | 100 | 100 | 100 | 0.3 s | 7.7 s | 2 210 ms | 0.001 |
| `/working-time` | 36 | 100 | 100 | 100 | 0.3 s | 7.8 s | 2 179 ms | 0.000 |

Reproduce it with:

```bash
dotnet publish src/WorkPlanStudio/WorkPlanStudio.csproj -c Release -o release
npx http-server release/wwwroot -p 8080 -P "http://localhost:8080?" --gzip --brotli -s &
npx lighthouse http://localhost:8080/ --preset=desktop
```

**Thirty-six is the number, and it is the runtime's, not the application's.** The
branded boot screen paints at 0.3 s; the largest contentful paint is the dashboard
after the runtime has downloaded, started, opened SQLite and seeded the demo
plant. Two seconds of total blocking time is the WebAssembly module compiling.
Layout shift is effectively zero, accessibility and best practices are perfect,
and those three *are* gated — the performance score is the one that is published
without being defended.

The levers, in order of effect, if this ever needs to move: lazy-loading the
assemblies the first screen does not need, a smaller runtime through trimming
settings the native SQLite relink currently constrains, and — no longer on the
list — a Web Worker for the scheduler, because
[ADR 0019](adr/0019-off-thread-scheduling.md) establishes that this app cannot be
built with threads at all while SQLite is linked into the module.

## Complexity and limits

One forward dispatch is approximately `O(operations × capacity)` because each step
scans the center's slots; with calendars each placement additionally walks the
window runs it crosses. Multi-start multiplies dispatch cost by its run count.
Local search evaluates up to the configured neighbour budget and re-scores each
candidate, so its practical upper bound is approximately
`O(localSteps × operations × capacity)`. Evaluation and due-date assignment are
linear in jobs and operations.

`SchedulingParameterLimits` caps 64 multi-start runs, 20 000 local-search
evaluations **and their product at 200 000 candidate schedules**. The product
limit is the one that matters: the two factors were bounded independently, so the
form could ask for 1.28 million dispatches without any single value looking
unreasonable. The page validates the product before it calls the service and
names the number it would have taken. 200 000 candidates of the 100-job problem
cost about a second on this machine, so treat it as a ceiling rather than a
recommendation — a browser is slower than this.

Proving a schedule optimal is a different budget and a different promise. The
branch-and-bound in `WorkPlanStudio.Scheduling.Exact` is a tight loop with no
hand-back, so it freezes the tab for exactly as long as it is allowed; the page
gives it **two seconds**, measured live on the seeded plant, and reports the
proved lower bound if that is not enough. Ten seconds was tried and is too long
to freeze a page for. See [ADR 0015](adr/0015-exact-solver.md).

A server-side scheduler becomes justified when schedules are shared, durable,
audited or long-running — and the optional backend in `src/WorkPlanStudio.Api`
is where that would go. OR-Tools CP-SAT or a MILP becomes justified when global
bounds, alternative machines or hard delivery constraints matter more than the
heuristic's simplicity; the exact solver already writes the LP file, so the first
step of that road is a command rather than a rewrite.
