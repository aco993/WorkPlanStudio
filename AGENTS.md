# AGENTS.md

Context for AI coding agents (OpenAI Codex, Cursor, Claude, …) working in this
repository. Read this first — it is the fastest way to be productive here without
breaking anything. Humans: this is also a good 2-minute orientation.

## What this project is

**WorkPlan Studio** is a .NET 10 **Blazor WebAssembly** portfolio app for
manufacturing routings (work plans). Its headline feature is a finite-capacity
**production scheduler**. The scheduler is a *pure, dependency-free* library; the
Blazor UI, EF Core + SQLite (compiled to WebAssembly, running in the browser) and
localisation sit around it. Full picture: [README.md](README.md).

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
| `src/WorkPlanStudio/` | the Blazor app (Models, Data, Services, Pages, Layout, Resources, wwwroot) |
| `src/WorkPlanStudio/Services/ScheduleMapper.cs` | the EF→engine boundary (the one `decimal`→seconds spot) |
| `src/WorkPlanStudio.WorkingTime/` | the pure working-time library (shift patterns, ArbZG rules, holidays, `WorkingTimelineBuilder` → `MachineCalendar`) |
| `src/WorkPlanStudio/Services/ShopCalendar.cs` | plant settings + work centers + absences → one timeline per work center |
| `src/WorkPlanStudio/Services/Auth/` | personas: `DemoAuthenticationStateProvider`, `Permissions` (the policy table), `IPermissionGuard` |
| `src/WorkPlanStudio/Services/Assistant/` | narration + `Chat/` (on-device answerer, `ScheduleChat` façade, `IChatProvider` with OpenAI-compatible, Anthropic and Gemini clients) ([docs](docs/AI-ASSISTANT.md)) |
| `src/WorkPlanStudio/Pages/Schedule.razor` | the scheduling UI (parameters, Gantt, KPIs, assistant) |
| `tests/WorkPlanStudio.Scheduling.Tests/` | engine unit, property, optimality, architecture, budget tests |
| `tests/WorkPlanStudio.WorkingTime.Tests/` | holidays, rules, timeline invariants, budgets |
| `tests/WorkPlanStudio.Web.Tests/` | SQLite, mapper, authorization, bUnit, assistant + chat |
| `tests/WorkPlanStudio.E2E/` | Playwright flows, axe accessibility, visual baselines (`visual-baselines/<os>/`) |
| `tests/WorkPlanStudio.Benchmarks/` | BenchmarkDotNet |
| `docs/` | ARCHITECTURE, SCHEDULING, TESTING (EN/DE), AI-ASSISTANT, PERFORMANCE, SECURITY, `adr/` |

## Build, run, test

```bash
# Prerequisites: .NET 10 SDK. To build/run the app you also need:
dotnet workload install wasm-tools

# Run the app  → http://localhost:5235
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Fast tests (no browser, no WASM)
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.WorkingTime.Tests/WorkPlanStudio.WorkingTime.Tests.csproj
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
- …a **working-time rule** → it is a parameter in `WorkingTimeRules` with an entry
  in its `Catalog` (legal reference) and a `Rule_*_Title/Text` pair in both `.resx`
  files; add a test in `WorkingTimelineBuilderTests`.
- …the **chat's questions** → one regex + one answer method in
  `OfflineScheduleAnswerer`, `Chat_*` keys in both `.resx` files, a case in
  `ScheduleChatTests`. Models only rephrase; never let them compute.
- …anything **visible** → run the browser suite; axe must stay at zero
  violations, and refresh the visual baselines with `VISUAL_UPDATE=1` on purpose.
- …a **mutating service method** → it takes the `IPermissionGuard` check first
  and returns `Forbidden`; add the policy to `Permissions` if it is a new kind
  of action.
- …a **dependency** → edit `Directory.Packages.props`.

## Out of scope on purpose

Backward scheduling, gap back-filling and a backend are documented as future
extensions (see `docs/SCHEDULING.md` §10 and the ADRs). Personas are not
security and the chat's recogniser is keyword-based by design. Don't change
these unless asked.
