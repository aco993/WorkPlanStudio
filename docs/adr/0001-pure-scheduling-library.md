# 1. Keep the scheduling engine a pure, dependency-free library

- **Status:** Accepted
- **Date:** 2026-06-16

## Context

The scheduler is the most complex part of the project. The rest of the app is a
Blazor WebAssembly front end whose data layer (EF Core + SQLite) is compiled to
WebAssembly and only runs inside the browser — it cannot be exercised in a normal
test host. If the algorithm lived among that code, it could not be unit-tested
quickly, and every test run would drag in the `wasm-tools` toolchain.

## Decision

Put the entire scheduling algorithm in its own class library,
`WorkPlanStudio.Scheduling`, that references **nothing** from Blazor, EF Core, JS
interop or WebAssembly. It works on plain input records. The Blazor app owns a
thin mapping layer (`ScheduleMapper`) that projects its EF entities into those
inputs and the engine result back into view models.

## Consequences

- ✅ The engine is unit-testable on a plain .NET runner in about a second, with no
  `wasm-tools` workload — so CI's fast lane needs no browser toolchain.
- ✅ The library is reusable outside the browser app (a console tool, a service…).
- ✅ The boundary is **enforced by a test** (`ArchitectureTests`) that fails the
  build if anyone references Blazor/EF/JS/SQLite from the engine.
- ➖ A mapping layer is required at the app boundary (itself unit-tested).
- ➖ Two projects instead of one, plus a small amount of view-model plumbing.
