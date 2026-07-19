# AGENTS.md

Context for AI coding agents (OpenAI Codex, Cursor, Claude, …) working in this
repository. Read this first — it is the fastest way to be productive here without
breaking anything. Humans: this is also a good 2-minute orientation.

## What this project is

**WorkPlan Studio** is a .NET 10 hosted **Blazor WebAssembly + ASP.NET Core**
portfolio app for manufacturing routings, orders and finite-capacity scheduling.
The public offline demo uses SQLite in the browser; production mode uses Identity,
an owner-scoped API and PostgreSQL. The scheduler remains a *pure,
dependency-free* library. Full picture: [README.md](README.md).

## Golden rules — do not break these

1. **The engine stays pure.** `src/WorkPlanStudio.Scheduling` must not reference
   Blazor, EF Core, JS interop, SQLite or WebAssembly. `ArchitectureTests` fails the
   build if it does. Put algorithm code here. (See [ADR 0001](docs/adr/0001-pure-scheduling-library.md).)
2. **Time is integer seconds inside the engine.** The only `decimal`→seconds
   conversion is `ScheduleMapper.ToSeconds` (banker's rounding). No floats in the
   scheduler — determinism depends on it. ([ADR 0002](docs/adr/0002-integer-seconds-time.md).)
3. **Determinism.** No `System.Random`, `DateTime.Now` or `Math.Random` in the
   engine — use `DeterministicRandom`. Same seed ⇒ same schedule. ([ADR 0004](docs/adr/0004-deterministic-prng.md).)
4. **The build treats warnings as errors.** Keep it clean.
5. **UI strings are localised.** Add every resource key to **both**
   `src/WorkPlanStudio/Resources/SharedResource.resx` *and* `…SharedResource.de.resx`.
6. **Record significant decisions** as a new ADR in `docs/adr/`.

## Map of the repo

| Path | What |
| --- | --- |
| `src/WorkPlanStudio.Scheduling/` | the pure engine (Inputs, Parameters, Core, Evaluation, Outputs, `SchedulingEngine.cs`) |
| `src/WorkPlanStudio.Scheduling/Explain/` | the deterministic `ScheduleExplainer` (structured, language-neutral) |
| `src/WorkPlanStudio/` | Blazor client plus isolated offline demo persistence |
| `src/WorkPlanStudio.Api/` | hosted API, Identity/CSRF, health, AI proxy and scheduling worker |
| `src/WorkPlanStudio.Domain/` | production entities and invariants |
| `src/WorkPlanStudio.Contracts/` | shared HTTP request/response contracts |
| `src/WorkPlanStudio.Persistence/` | production DbContext and SQLite migrations |
| `src/WorkPlanStudio.PostgresMigrations/` | PostgreSQL-specific migrations |
| `src/WorkPlanStudio/Services/ScheduleMapper.cs` | the EF→engine boundary (the one `decimal`→seconds spot) |
| `src/WorkPlanStudio/Services/Assistant/` | the schedule assistant: rule-based (default) + optional BYOK AI narrator ([docs](docs/AI-ASSISTANT.md)) |
| `src/WorkPlanStudio/Pages/Schedule.razor` | the scheduling UI (parameters, Gantt, KPIs, assistant) |
| `tests/WorkPlanStudio.Scheduling.Tests/` | engine unit + architecture tests |
| `tests/WorkPlanStudio.Web.Tests/` | mapper + bUnit component tests |
| `tests/WorkPlanStudio.Api.Tests/` | hosted Identity/API integration tests on SQLite |
| `tests/WorkPlanStudio.Postgres.Tests/` | migrations and lease fencing on real PostgreSQL |
| `tests/WorkPlanStudio.E2E/` | Playwright end-to-end |
| `tests/WorkPlanStudio.ProductionE2E/` | authenticated Playwright checks against the production container |
| `docs/` | SCHEDULING.md, TESTING.md (both EN/DE), `adr/` |

## Build, run, test

```bash
# Prerequisites: .NET 10 SDK. To build/run the app you also need:
dotnet workload install wasm-tools

# Run the app  → http://localhost:5235
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Fast tests (no browser, no WASM)
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj

# Build everything
dotnet build WorkPlanStudio.slnx
```

E2E (Playwright) needs the app running + a browser — see [docs/TESTING.md](docs/TESTING.md).

## Conventions

- **Central Package Management** — add NuGet versions to `Directory.Packages.props`;
  reference packages *without* a version in the `.csproj`.
- **Shared build settings** live in `Directory.Build.props`.
- **Style** is in `.editorconfig`; run `dotnet format` before pushing.

## If you change…

- …**scheduling behaviour** → add/extend engine tests and update `docs/SCHEDULING.md`.
- …the **schedule explanation / AI narration** → keep the engine explanation
  deterministic (the AI only *rephrases* facts, never invents them); see
  `docs/AI-ASSISTANT.md`. Never commit an API key.
- …the **EF model or seed data** → bump `SchemaVersion` in `Data/BrowserDatabase.cs`
  so stored databases are re-seeded.
- …a **UI string** → update both `.resx` files.
- …a **dependency** → edit `Directory.Packages.props`.

## Out of scope on purpose

Backward scheduling, alternative-machine optimization, unrestricted optimality
proofs and a target-environment HA deployment remain roadmap items. Distributed
run claiming already uses fenced database leases; do not replace or expand these
boundaries without a measured requirement and an explicit architecture decision.
