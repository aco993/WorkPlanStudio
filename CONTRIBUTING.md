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
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj
```

The Playwright end-to-end tests are described in [`docs/TESTING.md`](docs/TESTING.md).

## How the code is organised

- `src/WorkPlanStudio.Scheduling` — the **pure** scheduling engine. It must not
  reference Blazor, EF Core, JS interop or WebAssembly; an architecture test
  enforces this. Keep new algorithm code here and unit-test it directly.
- `src/WorkPlanStudio` — the Blazor app. The `ScheduleMapper` is the boundary that
  turns EF entities into engine inputs (and the one place `decimal` becomes
  integer seconds).
- `tests/` — engine tests, mapper + bUnit component tests, and Playwright E2E.

## Conventions

- **Style** lives in [`.editorconfig`](.editorconfig); run `dotnet format` before pushing.
- **The build treats warnings as errors** — keep it clean.
- **Package versions** are managed centrally in [`Directory.Packages.props`](Directory.Packages.props);
  add the version there, reference the package without a version in the `.csproj`.
- **Add or update tests** for any behaviour change (see [`docs/TESTING.md`](docs/TESTING.md)).
- **Record significant decisions** as a new ADR in [`docs/adr/`](docs/adr).
- **UI strings** are localised — add the key to both `SharedResource.resx` and
  `SharedResource.de.resx`.
