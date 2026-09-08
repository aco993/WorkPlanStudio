# Performance

Three kinds of measurement, each with its own tool and its own honesty
caveat. None of them is a production capacity claim; all of them are
reproducible.

| What | Tool | Where | Gate? |
| --- | --- | --- | --- |
| Engine and working-time library throughput | BenchmarkDotNet (`tests/WorkPlanStudio.Benchmarks`) | [`performance.yml`](../.github/workflows/performance.yml), weekly + on `main` | reported |
| Regression tripwires | xUnit budgets (`PerformanceBudgetTests` in both library test projects) | every pull request | **fails** the build |
| Page load of the published site | Lighthouse CI against the published `wwwroot`, served like GitHub Pages | `performance.yml` | accessibility, best practices, SEO and layout shift **fail**; the performance score is a warning |

## Benchmarks

```bash
dotnet run -c Release --project tests/WorkPlanStudio.Benchmarks -- --filter '*'            # full job
dotnet run -c Release --project tests/WorkPlanStudio.Benchmarks -- --filter '*' --job short  # what CI runs
```

Measured on 2026-09-08 (short job, .NET 10.0.9, Windows 11, four logical
processors — indicative, compare only on the same machine):

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| small: 25 jobs, 4 starts, 500 local steps | 6.0 ms | 25 MB |
| medium: 100 jobs, rule only (one dispatch) | 57 µs | 105 KB |
| medium: 100 jobs, 8 starts, 2 000 local steps | 281 ms | 1.05 GB |
| medium + shift calendars, breaks, blackouts | 478 ms | 1.06 GB |
| large: 250 jobs, 16 starts, 5 000 local steps | 4.75 s | 14.8 GB |
| holidays: all 16 states, one year | 2.7 µs | 20 KB |
| timeline: three-shift pattern, 400 days → machine calendar | 10.2 µs | 71 KB |
| timeline: one shift with an absence, 400 days | 4.9 µs | 33 KB |

Reading the table:

- **One dispatch is cheap** (57 µs for 600 operations). Everything above it is
  the optimiser: 8 starts × 2 000 insertion-neighbourhood steps, each of which
  re-dispatches a candidate and allocates a fresh schedule. The allocation
  column is that arithmetic — about 0.5 MB per candidate — and is the first
  thing to attack if the browser ever feels it (reuse the candidate buffers;
  the engine is deterministic, so a pooled schedule changes no result).
- **Calendars cost about 70 %** on the medium problem: every placement walks
  window runs, bridges breaks and skips blackouts. Still interactive.
- **Working time is free** at this scale: a 400-day calendar for a work
  center builds in microseconds, so the app rebuilds all of them before every
  run rather than caching anything.
- The scenario tool in `tools/WorkPlanStudio.Scheduling.Scenarios` reports the
  same shapes with wall-clock, allocation and a determinism check; on
  2026-09-08 it measured the medium scenario at 308 ms / 1.05 GB on this
  branch against 280 ms / 0.98 GB on `main` before the calendar work — the
  cost of the working-time features on an uncalendared problem is within
  10 %.

## Regression tripwires

`PerformanceBudgetTests` run with every pull request. Their bounds are an
order of magnitude above the numbers above (medium problem under 3 s, with
calendars under 4 s, one large dispatch under 0.5 s, a 400-day timeline under
0.5 s), measured after a warm-up run. A pass proves nothing precise; a failure
means something went quadratic, which is the regression worth catching in CI
without the noise of a benchmark on a shared runner.

## Page load

The app ships the .NET runtime, EF Core and SQLite as WebAssembly. That is
5.8 MB compressed on the wire and about 30 MB once decoded, so its Lighthouse
performance score is what a WebAssembly runtime scores, and it is reported
rather than gated. Measured on 2026-09-08 with Lighthouse 13.4 (desktop
preset) against the published `wwwroot` served with Brotli, the way
`performance.yml` measures it:

| Page | Performance | Accessibility | Best practices | SEO | FCP | LCP | TBT |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `/` | 36 | 100 | 100 | 91 | 0.4 s | 7.4 s | 2.1 s |
| `/schedule` | 36 | 100 | 100 | 91 | 0.4 s | 7.4 s | 2.1 s |

The branded boot screen paints at 0.4 s; the largest contentful paint is the
dashboard after the runtime has loaded and the database has been seeded. The
levers, in order of effect, if this ever needs to move: a Web Worker for the
scheduler (moves the blocking time off the main thread; justified only after
profiling shows it on real data), lazy-loading the assistant and working-time
pages' assemblies, and a smaller runtime via trimming settings that the
SQLite native relink currently constrains.

## Complexity and limits

One forward dispatch is approximately `O(operations × capacity)` because each
step scans the center's slots; with calendars each placement additionally
walks the window runs it crosses. Multi-start multiplies dispatch cost by its
run count. Local search evaluates up to the configured neighbour budget and
re-dispatches each candidate, so its practical upper bound is approximately
`O(localSteps × operations × capacity)`. Evaluation and due-date assignment are
linear in jobs/operations.

Central limits are 64 multi-start runs and 20 000 local-search evaluations.
They prevent accidental unbounded browser work; they are not a claim that
every maximum-sized input will feel interactive. A server-side scheduler
becomes justified when schedules are shared, durable, audited or long-running;
OR-Tools CP-SAT or MILP when global bounds, alternative machines, setup
matrices or hard delivery constraints matter more than the heuristic's
simplicity and explainability.
