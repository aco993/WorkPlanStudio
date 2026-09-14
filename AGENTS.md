# AGENTS.md

Context for AI coding agents (OpenAI Codex, Cursor, Claude, …) working in this
repository. Read this first — it is the fastest way to be productive here without
breaking anything. Humans: this is also a good three-minute orientation.

## What this project is

**WorkPlan Studio** is a .NET 10 **Blazor WebAssembly** portfolio app for
manufacturing routings (work plans), the work centers and cost centers they run on,
production orders with frozen routings, and a finite-capacity **production
scheduler**. The scheduler, the working-time rules and the export writers are
*pure, dependency-free* libraries; the Blazor UI, EF Core + SQLite (compiled to
WebAssembly, running in the browser) and localisation sit around them. There is an
**optional** ASP.NET Core backend that is off unless configured. Full picture:
[README.md](README.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Golden rules — do not break these

1. **The libraries stay pure.** `src/WorkPlanStudio.Scheduling`,
   `src/WorkPlanStudio.WorkingTime` and `src/WorkPlanStudio.Export` must not
   reference Blazor, EF Core, JS interop, SQLite or WebAssembly. `ArchitectureTests`
   fails the build if they do, and the export project has no package reference at
   all. Put algorithm code here. ([ADR 0001](docs/adr/0001-pure-scheduling-library.md))
2. **Time is integer seconds inside the engine.** The only `decimal`→seconds
   conversion is `ScheduleMapper.ToSeconds` (banker's rounding), and a source scan
   keeps it the only one. Floating point exists in the objective and nowhere in the
   placement. ([ADR 0002](docs/adr/0002-integer-seconds-time.md))
3. **One time model.** Plant-local wall clock, `DateTimeKind.Unspecified`, stated by
   `PlantTime`. Nothing in the app may call `ToLocalTime` or `ToUniversalTime` — a
   test enforces it. `CreatedUtc`/`ModifiedUtc` are audit stamps and never planning
   inputs.
4. **Determinism.** No `System.Random`, `DateTime.Now` or `Math.Random` in the
   engine — use `DeterministicRandom`. Same seed ⇒ same schedule.
   ([ADR 0004](docs/adr/0004-deterministic-prng.md))
5. **The build treats warnings as errors**, and `.editorconfig` is a real gate: 25
   rules at warning level, enforced by `dotnet format --severity warn` in CI.
6. **UI strings are localised.** Add every resource key to **both**
   `src/WorkPlanStudio/Resources/SharedResource.resx` *and* `…SharedResource.de.resx`;
   `check_resources.py` fails the build if the key sets differ.
7. **Every mutating service method asks `IPermissionGuard` first** and returns
   `Forbidden` otherwise. The set is discovered by reflection — a method that can
   change something returns `ApplicationResult<T>` — so a new one is covered
   automatically and cannot be forgotten.
   ([ADR 0013](docs/adr/0013-personas-through-the-real-authorization-pipeline.md))
8. **Never state a measured number you did not measure.** Every figure in the
   documentation names the artefact that produces it. This rule exists because the
   project's most-repeated claim was once attributed to a test that did not compute
   it, and that single sentence put every other number under suspicion.
9. **Record significant decisions** as a new ADR in `docs/adr/`, with a row in both
   the English and the German index.

## Map of the repo

| Path | What |
| --- | --- |
| `src/WorkPlanStudio.Scheduling/` | the pure engine (Inputs, Parameters, Core, Evaluation, Outputs, `SchedulingEngine.cs`) |
| `src/WorkPlanStudio.Scheduling/Exact/` | the disjunctive branch-and-bound that proves the optimum, plus the LP writer ([ADR 0015](docs/adr/0015-exact-solver.md)) |
| `src/WorkPlanStudio.Scheduling/Explain/` | the deterministic `ScheduleExplainer` (structured, language-neutral) |
| `src/WorkPlanStudio.WorkingTime/` | the pure working-time library (shift patterns, ArbZG rules, holidays, `WorkingTimelineBuilder` → `MachineCalendar`, `WorkingTimeCompliance`) |
| `src/WorkPlanStudio.Export/` | the pure CSV, xlsx and PDF writers — no package reference |
| `src/WorkPlanStudio.Contracts/` | DTOs and policy names shared with the optional API |
| `src/WorkPlanStudio.Domain/` | entities, validation rules, the policy table and the EF→engine mapping — referenced by the app **and** by the API, so a rule cannot mean two things on two hosts. No persistence in here. |
| `src/WorkPlanStudio.Api/` | the optional backend (Identity, JWT, EF migrations, minimal API) |
| `src/WorkPlanStudio/` | the Blazor app (Models, Data, Validation, Services, Components, Layout, Pages, Resources, wwwroot) |
| `src/WorkPlanStudio/Services/ScheduleMapper.cs` | the EF→engine boundary (the one `decimal`→seconds spot) |
| `src/WorkPlanStudio/Services/ShopCalendar.cs` | plant settings + work centers + absences → one timeline per work center |
| `src/WorkPlanStudio/Services/Auth/` | personas: `DemoAuthenticationStateProvider`, `Permissions` (the policy table), `IPermissionGuard` |
| `src/WorkPlanStudio/Services/Scheduling/` | `IScheduleRunner` (the sliced run), `IScheduleYield`, `IOptimalityProver` |
| `src/WorkPlanStudio/Services/Import/` | the CSV import: parser, dialect detection, plan/commit ([ADR 0018](docs/adr/0018-csv-import.md)) |
| `src/WorkPlanStudio/Services/Assistant/` | narration + `Chat/` (on-device answerer, `ScheduleChat`, `IChatProvider` with three clients) ([docs](docs/AI-ASSISTANT.md)) |
| `src/WorkPlanStudio/Data/SchemaUpgrades.cs` | the schema version and the upgrade steps — **read `CurrentVersion`, never a literal** |
| `tests/WorkPlanStudio.Scheduling.Tests/` | engine unit, property, optimality study, exact solver, architecture, budgets |
| `tests/WorkPlanStudio.WorkingTime.Tests/` | holidays, rules, averaging, DST, timeline invariants, budgets |
| `tests/WorkPlanStudio.Web.Tests/` | SQLite, schema upgrades, mapper, authorization, import, bUnit, assistant |
| `tests/WorkPlanStudio.Api.Tests/` | HTTP integration against a real SQLite file |
| `tests/WorkPlanStudio.Export.Tests/` | the produced bytes, read back |
| `tests/WorkPlanStudio.E2E/` | Playwright flows, axe, visual baselines (`visual-baselines/linux/`) |
| `tests/WorkPlanStudio.Scheduling.Testing/` | the one problem generator and the twenty optimality instances — use it rather than writing a fourth copy |
| `tests/WorkPlanStudio.Benchmarks/` | BenchmarkDotNet |
| `tools/WorkPlanStudio.Scheduling.Scenarios/` | reproducible scenarios: `scenarios`, `acceptance`, `allocation`, `budget`, `optimality`, `exact`, `exactwall`, `exactbudget`, `milp` |
| `docs/` | ARCHITECTURE, SCHEDULING, TESTING, AI-ASSISTANT, PERFORMANCE, SECURITY — each with a `.de` twin — plus `adr/` |

## Build, run, test

```bash
# Prerequisites: .NET 10 SDK. To build or run the browser app you also need:
dotnet workload install wasm-tools

# Run the app  → http://localhost:5235
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Build everything
dotnet build WorkPlanStudio.slnx -c Release

# Run one suite. The test projects are self-hosting executables, which is faster
# than the SDK path and prints "Total: N, Errors: 0, Failed: 0".
./tests/WorkPlanStudio.Scheduling.Tests/bin/Release/net10.0/WorkPlanStudio.Scheduling.Tests.exe
./tests/WorkPlanStudio.WorkingTime.Tests/bin/Release/net10.0/WorkPlanStudio.WorkingTime.Tests.exe
./tests/WorkPlanStudio.Web.Tests/bin/Release/net10.0/WorkPlanStudio.Web.Tests.exe
./tests/WorkPlanStudio.Api.Tests/bin/Release/net10.0/WorkPlanStudio.Api.Tests.exe
./tests/WorkPlanStudio.Export.Tests/bin/Release/net10.0/WorkPlanStudio.Export.Tests.exe
```

Before you finish, run what CI runs:

```bash
dotnet format WorkPlanStudio.slnx --severity warn --verify-no-changes
python3 -m unittest discover -s .github/scripts -p 'test_*.py'
python3 .github/scripts/check_doc_links.py
python3 .github/scripts/check_resources.py
```

E2E (Playwright) needs the app running and a browser — see
[docs/TESTING.md](docs/TESTING.md).

## Conventions

- **Central Package Management** — add NuGet versions to `Directory.Packages.props`;
  reference packages *without* a version in the `.csproj`.
- **Shared build settings** live in `Directory.Build.props`.
- **Style** is in `.editorconfig`. Private `const` and `static readonly` are
  PascalCase, every other private field is `_camelCase`, and `readonly` is required
  on a field that is never reassigned.
- **XML docs on public API explain the *why*** — the trade-off, the reason a value is
  what it is — and never restate the signature.

## If you change…

- …**scheduling behaviour** → add or extend engine tests and update
  `docs/SCHEDULING.md` **and** `docs/SCHEDULING.de.md`. If you change search quality,
  re-run `tools/… -- exact` and update the numbers in ADR 0015, the READMEs,
  ARCHITECTURE and SCHEDULING — they are the same measurement quoted in several
  places, and `OptimalityStudyTests` will fail first.
- …the **schedule explanation or the chat** → the engine explanation stays
  deterministic (a model only *rephrases* facts, never computes them); anything sent
  to a model goes through `ChatFacts.BuildSystemPrompt`, which fences and bounds it.
  Never commit an API key. See `docs/AI-ASSISTANT.md` and ADR 0025.
- …the **EF model** → bump nothing by hand: add a step to `Data/SchemaUpgrades.cs`
  and raise `CurrentVersion` there. **And add an EF migration to the API**, because
  `WorkPlanStudio.Api` compiles `Models/**` into itself and EF Core 10 throws
  `PendingModelChangesWarning` from `MigrateAsync` when the snapshot is stale — all
  88 API tests fail otherwise.
- …a **UI string** → update both `.resx` files, in the same position.
- …a **working-time rule** → it is a parameter in `WorkingTimeRules` with an entry in
  its `Catalog` (legal reference) and a `Rule_*_Title/Text` pair in both `.resx`
  files; add a test in `WorkingTimelineBuilderTests`.
- …the **chat's questions** → one regex and one answer method in
  `OfflineScheduleAnswerer`, `Chat_*` keys in both `.resx` files, a case in
  `ScheduleChatTests`. A reference the answerer recognises but cannot resolve must end
  the search, not fall through to the next intent.
- …anything **visible** → run the browser suite. axe must stay at zero violations in
  English light, English dark **and German**; a new route in
  `AccessibilityE2ETests.Routes` is three more tests. Refresh visual baselines
  deliberately with `UPDATE_VISUAL_BASELINES=1`, **on Linux** — those are the only
  baselines that exist ([ADR 0021](docs/adr/0021-visual-baselines-linux-only.md)).
- …a **mutating service method** → it takes the `IPermissionGuard` check first and
  returns `Forbidden`; add the policy to `Permissions` if it is a new kind of action.
  The architecture test will find it whether you remember or not.
- …a **bUnit test** → derive from `AppBunitContext`, and never find an element and
  trigger an event on it in two statements.
- …a **dependency** → edit `Directory.Packages.props`, and think twice before adding
  one to a pure library. The export project exists to demonstrate that PDF and xlsx
  did not need one.

## Out of scope on purpose

Backward scheduling and gap back-filling are documented as future extensions
(`docs/SCHEDULING.md` §10) — and §6c now measures what the second one would be worth,
which is a lot. Personas are not security, the chat's recogniser is keyword-based, the
connected mode pulls one way only, and the app cannot be built with threads while
SQLite is linked into the module. Don't change these unless asked; each one has an ADR
behind it.
