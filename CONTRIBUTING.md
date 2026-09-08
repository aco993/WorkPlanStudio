# Contributing

This is a portfolio project, but it is built like a real one — so contributions
(and curious readers) are very welcome. This guide is the short version; see the
[README](README.md), [`docs/SCHEDULING.md`](docs/SCHEDULING.md) and
[`docs/TESTING.md`](docs/TESTING.md) for the detail.

## Build & test

```bash
# Prerequisites: the .NET 10 SDK, and (only to run the app) the WASM workload:
dotnet workload install wasm-tools

# Run the app
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Run the fast tests (no browser, no WASM)
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.WorkingTime.Tests/WorkPlanStudio.WorkingTime.Tests.csproj
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj
```

The browser suite (Playwright flows, axe accessibility scans, visual baselines) is described in [`docs/TESTING.md`](docs/TESTING.md). A visible change must keep axe at zero violations; refresh the visual baselines with `VISUAL_UPDATE=1` when the change is intended.

## How the code is organised

- `src/WorkPlanStudio.Scheduling` — the **pure** scheduling engine. It must not
  reference Blazor, EF Core, JS interop or WebAssembly; an architecture test
  enforces this. Keep new algorithm code here and unit-test it directly.
- `src/WorkPlanStudio.WorkingTime` — the **pure** working-time library: shift
  patterns, the ArbZG rules as parameters, holidays, and the timeline that
  becomes a machine calendar. Same boundary rule as the engine.
- `src/WorkPlanStudio` — the Blazor app. The `ScheduleMapper` is the boundary that
  turns EF entities into engine inputs (and the one place `decimal` becomes
  integer seconds); `ShopCalendar` turns plant settings into calendars.
- `tests/` — engine, working-time, web (SQLite, mapper, authorization, bUnit,
  assistant), the browser suite, and the benchmarks.

## Conventions

- **Style** lives in [`.editorconfig`](.editorconfig); run `dotnet format` before pushing.
- **The build treats warnings as errors** — keep it clean.
- **Package versions** are managed centrally in [`Directory.Packages.props`](Directory.Packages.props);
  add the version there, reference the package without a version in the `.csproj`.
- **Add or update tests** for any behaviour change (see [`docs/TESTING.md`](docs/TESTING.md)).
- **Record significant decisions** as a new ADR in [`docs/adr/`](docs/adr).
- **UI strings** are localised — add the key to both `SharedResource.resx` and
  `SharedResource.de.resx`.
