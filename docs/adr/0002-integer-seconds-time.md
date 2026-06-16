# 2. Model all internal time as integer seconds

- **Status:** Accepted
- **Date:** 2026-06-16

## Context

The app runs in the browser (WebAssembly) but its tests and CI run on the desktop.
Floating-point arithmetic — in particular the order of summation — can differ in
its last bits between those runtimes. Any such difference would break the core
guarantee that *the same seed produces the same schedule everywhere*. The source
data, however, stores times as `decimal` minutes (e.g. `4.2`).

## Decision

Model every internal duration and instant as an integer number of **seconds**
(`long`). Convert `decimal` minutes to seconds exactly **once**, at the EF→domain
boundary (`ScheduleMapper.ToSeconds`), with an explicit `MidpointRounding.ToEven`.
Keep money and `decimal` out of the scheduler entirely — cost is a display
projection, never a scheduling input.

## Consequences

- ✅ Schedules are bit-for-bit identical across the browser, the desktop and CI.
- ✅ Determinism becomes achievable and directly testable.
- ✅ The only `decimal`→integer conversion is a single, explicit, unit-tested step.
- ➖ Sub-second precision is not modelled — which the domain never needs.
- ➖ Callers must convert their own time units before handing data to the engine.
