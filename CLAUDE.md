# CLAUDE.md

Project guidance for Claude Code. [`AGENTS.md`](AGENTS.md) is the single source of
truth for repo layout, conventions and the "if you change X" map — read it first.
This file is the short version: the rules that must never be broken and the
commands you will actually run.

## Golden rules

1. **The engine stays pure.** `src/WorkPlanStudio.Scheduling` must not reference
   Blazor, EF Core, JS interop, SQLite or WebAssembly. `ArchitectureTests` fails
   the build otherwise.
2. **Integer seconds in the engine.** The only `decimal`→seconds conversion is
   `ScheduleMapper.ToSeconds`. No floats in the scheduler — determinism needs it.
3. **Determinism.** No `System.Random` / `DateTime.Now` in the engine — use
   `DeterministicRandom`. Same seed ⇒ same schedule.
4. **Warnings are errors.** Keep the build clean.
5. **Localise every UI string** in **both** `SharedResource.resx` and
   `SharedResource.de.resx`.
6. **Record significant decisions** as a new ADR in `docs/adr/`.

## Commands

```bash
# Run the app (needs: dotnet workload install wasm-tools) → http://localhost:5235
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Fast tests — no browser, no WASM
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj

# Whole solution
dotnet build WorkPlanStudio.slnx
dotnet format            # before committing
```

Playwright E2E needs the app running plus a browser — see
[`docs/TESTING.md`](docs/TESTING.md).

## Workflow

- Central Package Management — add NuGet versions to `Directory.Packages.props`,
  reference packages without a version in the `.csproj`.
- Keep the app green after every change; add or extend tests with each behaviour
  change; keep commits small and conventional (`feat:`, `test:`, `docs:`, …).
