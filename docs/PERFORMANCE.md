# Scheduling performance scenarios

This is a reproducible scenario test, not a production capacity benchmark. It measures the current deterministic heuristic on generated routing-shaped data and helps decide when browser UI-thread execution stops being appropriate.

Run it with:

```bash
dotnet run --project tools/WorkPlanStudio.Scheduling.Scenarios/WorkPlanStudio.Scheduling.Scenarios.csproj -c Release
```

Verified on 2026-07-12 with .NET runtime 10.0.9, Windows 10.0.26200 and four reported logical processors:

| Scenario | Jobs | Operations | Centers | Capacity | Starts | Local steps | Duration ms | Allocated MB | Peak working MB | Penalty | Deterministic |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| small | 25 | 100 | 5 | 1 | 4 | 500 | 2.0 | 0.32 | 25.4 | 1515.5194 | yes |
| medium | 100 | 600 | 10 | 2 | 8 | 2,000 | 85.1 | 11.17 | 29.3 | 17692.0125 | yes |
| large | 250 | 2,000 | 20 | 2 | 16 | 5,000 | 394.6 | 243.43 | 40.8 | 83099.8894 | yes |

Numbers vary by machine and JIT state. The runner warms the JIT, forces GC before each measured run, reports current-thread allocations, and repeats each run to compare exact operation signatures.

## Complexity and limits

One forward dispatch is approximately `O(operations × capacity)` because each step scans the center's slots. Multi-start multiplies dispatch cost by its run count. Local search evaluates up to the configured neighbor budget and re-dispatches each candidate, so its practical upper bound is approximately `O(localSteps × operations × capacity)`. Evaluation and due-date assignment are linear in jobs/operations.

Central limits are 64 multi-start runs and 20,000 local-search evaluations. They prevent accidental unbounded browser work; they are not a claim that every maximum-sized input will feel interactive.

The large scenario completes comfortably on this desktop but allocates about 243 MB. That is the strongest reason not to claim broad browser scalability. A Web Worker becomes justified after browser profiling shows visible main-thread blocking for representative user data. A server-side scheduler becomes justified when jobs are shared, durable, audited or long-running. OR-Tools CP-SAT/MILP becomes justified when global bounds, calendars, alternative machines, setup matrices or hard delivery constraints matter more than the current heuristic's simplicity and explainability.
