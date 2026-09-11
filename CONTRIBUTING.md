# Contributing

**English** · [Deutsch](CONTRIBUTING.de.md)

This is a portfolio project, but it is built like a real one — so contributions
(and curious readers) are very welcome. This guide is the short version; see the
[README](README.md), [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md),
[`docs/SCHEDULING.md`](docs/SCHEDULING.md) and [`docs/TESTING.md`](docs/TESTING.md)
for the detail.

## Build & test

```bash
# Prerequisites: the .NET 10 SDK, and (only to build or run the browser app) the WASM workload:
dotnet workload install wasm-tools

# Run the app
dotnet run --project src/WorkPlanStudio/WorkPlanStudio.csproj

# Run the fast tests (no browser, no WASM)
dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj
dotnet test tests/WorkPlanStudio.WorkingTime.Tests/WorkPlanStudio.WorkingTime.Tests.csproj
dotnet test tests/WorkPlanStudio.Export.Tests/WorkPlanStudio.Export.Tests.csproj
dotnet test tests/WorkPlanStudio.Api.Tests/WorkPlanStudio.Api.Tests.csproj

# The web suite references the Blazor app, so building it compiles the app
dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj
```

The browser suite (Playwright flows, axe accessibility scans, visual baselines) is
described in [`docs/TESTING.md`](docs/TESTING.md).

## Before you push — run what CI runs

Every one of these is a gate on a pull request, and all four run locally:

```bash
dotnet format WorkPlanStudio.slnx --severity warn --verify-no-changes   # 25 enforced style rules
python3 -m unittest discover -s .github/scripts -p 'test_*.py'          # the checks' own tests
python3 .github/scripts/check_doc_links.py                              # every relative link *and* anchor
python3 .github/scripts/check_resources.py                              # both .resx parse and match key for key
```

`dotnet format WorkPlanStudio.slnx --severity warn` (without `--verify-no-changes`)
fixes most of what the first one reports.

## How the code is organised

- `src/WorkPlanStudio.Scheduling` — the **pure** scheduling engine, including the
  exact branch-and-bound solver in `Exact/`. It must not reference Blazor, EF Core,
  JS interop or WebAssembly; an architecture test enforces this. Keep new algorithm
  code here and unit-test it directly.
- `src/WorkPlanStudio.WorkingTime` — the **pure** working-time library: shift
  patterns, the ArbZG rules as parameters, holidays, the timeline that becomes a
  machine calendar, and the compliance evaluation. Same boundary rule as the engine.
- `src/WorkPlanStudio.Export` — the **pure** CSV, xlsx and PDF writers. It has no
  package reference at all, and that is the point; adding one needs an ADR.
- `src/WorkPlanStudio` — the Blazor app. `ScheduleMapper` is the boundary that turns
  EF entities into engine inputs (and the one place `decimal` becomes integer
  seconds); `ShopCalendar` turns plant settings into calendars.
- `src/WorkPlanStudio.Contracts` / `src/WorkPlanStudio.Api` — the optional backend
  and the DTOs it shares with the client. **The API compiles `Models/**`,
  `Validation/**` and four `Services/*.cs` files out of the browser app by linked
  source**, so giving anything in those folders a dependency on Blazor, EF Core or
  JS interop breaks a project you did not touch.
- `tests/` — engine, working time, export, web, backend, the browser suite, the
  shared problem generator and the benchmarks.

## Conventions

- **Style** lives in [`.editorconfig`](.editorconfig) and is a real gate: 25 rules at
  warning level, enforced by `dotnet format --severity warn` in CI. Private `const`
  and `static readonly` fields are PascalCase, every other private field is
  `_camelCase`, and `readonly` is required on a field that is never reassigned.
- **The build treats warnings as errors** — keep it clean.
- **Package versions** are managed centrally in
  [`Directory.Packages.props`](Directory.Packages.props); add the version there and
  reference the package without a version in the `.csproj`.
- **Add or update tests** for any behaviour change, and prefer a test that fails
  without the change. A test that cannot fail is worse than no test, because it
  reads like coverage.
- **Record significant decisions** as a new ADR in [`docs/adr/`](docs/adr/README.md),
  and add a row to both the English and the German index.
- **UI strings are localised** — add the key to both `SharedResource.resx` and
  `SharedResource.de.resx`, in the same position. `check_resources.py` fails the
  build if the two key sets differ.
- **Never state a measured number you did not measure.** Every figure in the
  documentation names the artefact that produces it — a benchmark, a test, a tool
  command. If you cannot produce it, describe the behaviour instead.

## Touching the browser suite

- A new E2E class takes `IClassFixture<PlaywrightFixture>` and **no** `[Collection]`
  attribute; the classes run in parallel, each with its own browser.
- A new route added to `AccessibilityE2ETests.Routes` must be axe-clean in English
  light, English dark **and** German — that is three more tests, and best-practice
  rules count.
- A new screen in `VisualRegressionTests.Matrix` needs a committed **Linux** baseline.
  It cannot be bootstrapped from CI, and a missing baseline fails the build by design.
  Refresh baselines deliberately with `UPDATE_VISUAL_BASELINES=1` on Linux; on
  Windows and macOS the visual tests skip (see
  [ADR 0021](docs/adr/0021-visual-baselines-linux-only.md)).
- Every bUnit component test derives from `AppBunitContext`. Never find an element
  and trigger an event on it in two separate statements — wrap both in
  `cut.InvokeAsync(...)`, or you reintroduce a stale-handler race.

## Pull requests

[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) lists every
check and what each one fails on. A change that alters visible behaviour should say
how it was exercised — a browser, a test, a command — rather than that it "should
work".
