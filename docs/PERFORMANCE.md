# Scheduling performance scenarios

This is a reproducible scenario test, not a production capacity benchmark. It measures the current deterministic heuristic on generated routing-shaped data and helps decide when browser UI-thread execution stops being appropriate.

Run it with:

```bash
dotnet run --project tools/WorkPlanStudio.Scheduling.Scenarios/WorkPlanStudio.Scheduling.Scenarios.csproj -c Release
```

CI runs the same scenarios with `--verify`; each scenario must remain deterministic, finish within 10 seconds and allocate less than 512 MB on the hosted runner. These are regression ceilings, not latency promises.

Verified on 2026-07-13 with .NET runtime 10.0.9, Windows 10.0.26200 and four reported logical processors:

| Scenario | Jobs | Operations | Centers | Capacity | Starts | Local steps | Duration ms | Allocated MB | Peak working MB | Penalty | Deterministic |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| small | 25 | 100 | 5 | 1 | 4 | 500 | 32.9 | 0.57 | 27.5 | 1515.5194 | yes |
| medium | 100 | 600 | 10 | 2 | 8 | 2,000 | 275.2 | 42.73 | 31.8 | 10457.8722 | yes |
| large | 250 | 2,000 | 20 | 2 | 16 | 5,000 | 601.0 | 436.57 | 42.7 | 73010.8853 | yes |

Numbers vary by machine and JIT state. The runner warms the JIT, forces GC before each measured run, reports current-thread allocations, and repeats each run to compare exact operation signatures.

## Complexity and limits

One forward dispatch is approximately `O(operations × capacity)` because each step scans the center's slots. Multi-start multiplies dispatch cost by its run count. Local search evaluates up to the configured neighbor budget and re-dispatches each candidate, so its practical upper bound is approximately `O(localSteps × operations × capacity)`. Evaluation and due-date assignment are linear in jobs/operations.

Central limits are 64 multi-start runs and 20,000 local-search evaluations. They prevent accidental unbounded browser work; they are not a claim that every maximum-sized input will feel interactive.

The large scenario completes within the regression ceiling but allocates about 437 MB. That is the strongest reason not to claim broad browser scalability. Hosted mode already moves durable shared runs to the server; further optimization or a distributed worker is justified only after profiling representative production data. OR-Tools CP-SAT/MILP becomes justified when global bounds or a measurable solution-quality SLA matter more than the current heuristic's simplicity and explainability.
