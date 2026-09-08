# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] — 2026-09-08

### Added

- **Working time as capacity** — a second pure library, `WorkPlanStudio.WorkingTime`:
  shift patterns (continuous, one, two, three shifts), every rule of the German
  Working Hours Act the app applies as a parameter with its legal reference
  (§3 daily cap, §4 breaks, §5 rest per crew across the week wrap, §6 night
  work, §9 Sunday/holiday closure with the boundary shift, §11 free Sundays),
  public holidays computed for all 16 federal states, absences per work center,
  and a timeline that projects onto the engine's calendars. A **Working-time
  page** with a live preview of the week, the rule applications, the holiday
  table and the absences. The Gantt chart shades closed time with its reason and
  shows real dates. ([ADR 0012](docs/adr/0012-working-time-as-capacity.md))
- **Engine calendars** — a phase for calendars that do not start at the horizon,
  tagged blackouts that are never bridged, and a bridgeable gap so an operation
  can pause across a break; utilisation now divides by open time.
- **Personas** — Planner, Supervisor and Guest through the real ASP.NET Core
  authorization pipeline: policies, `AuthorizeView`, a persona menu, read-only
  notices, and an `IPermissionGuard` on every mutating service that returns
  `Forbidden`. ([ADR 0013](docs/adr/0013-personas-through-the-real-authorization-pipeline.md))
- **Schedule chat** — a conversation over the current run answered on the
  device in English and German (bottleneck, late orders and why, one order or
  work center, idle time by reason, the rules in force, a summary) and
  what-ifs that re-run the scheduler. Optional models behind one seam:
  OpenAI-compatible, Anthropic Messages API and Google Gemini, with the key in
  the header each expects, and fallback to the on-device answer on any failure.
  ([ADR 0014](docs/adr/0014-schedule-chat-on-device-first-with-pluggable-models.md))
- **Dark theme** (system, light, dark) applied before Blazor boots, a skip link,
  focus traps in dialogs, `prefers-reduced-motion`, a branded boot screen and a
  dashboard with plant and schedule at a glance.
- **Tests of every kind** — axe-core WCAG 2.2 AA scans of every page in both
  themes and in a dialog, visual regression against per-OS pixel baselines,
  performance budgets, a BenchmarkDotNet project, working-time property tests,
  provider clients over a stubbed transport, bUnit for the chat panel,
  Playwright for the chat in both languages. 452 tests in four projects.
- **CI** — per-assembly coverage gates with badges served from the site, the
  browser suite gating the deploy, a performance workflow (benchmarks and
  Lighthouse CI on the published site), `robots.txt`.

### Changed

- The optional AI narrator now speaks through the same provider seam as the chat
  (`AiScheduleNarrator`); the settings dialog offers a provider select that
  fills endpoint and model. The provider budget is 20 seconds.
- Five accessibility defects found by the first axe scan were fixed: pill and
  Gantt-label contrast, an unfocusable scroll region, a skipped heading level
  and a duplicate landmark.
- The database schema version is 5 (plant settings, shift patterns, absences);
  stored databases from earlier versions enter the export/reset flow.
- GitHub Actions, bUnit and the coverage extension bumped to current versions.

### Removed

- Interview cheat-sheets and hardening notes in a third language; the
  English `docs/INTERVIEW.md` remains.

### Also in this release (landed on `main` between 0.1.0 and 0.2.0)

- **Production orders** that own an immutable routing snapshot, released with a
  real customer due date; `DueDateRule.Explicit` is the default.
  ([ADR 0011](docs/adr/0011-production-orders-own-routing-snapshots.md))
- **Repeating availability calendars and sequence-dependent change-over** by
  operation family in the engine. ([ADR 0010](docs/adr/0010-periodic-calendars-and-setup-families.md))
- **Insertion-neighbourhood local search** and an **exact dispatch-order
  optimizer** for small instances, with optimality tests against brute-force
  enumeration (0.2 % mean gap, 19 of 20 instances exact).
  ([ADR 0008](docs/adr/0008-insertion-neighbourhood.md))
- **Dispatch-rule equivalences reported** instead of hidden.
  ([ADR 0009](docs/adr/0009-report-rule-equivalences.md))
- **Schedule assistant** — a deterministic, on-device explanation of each run
  (bottleneck, why each job is late, one *computed* recommendation) as localized
  EN/DE narration, plus an optional bring-your-own-key narrator behind a
  provider abstraction with fallback. ([ADR 0005](docs/adr/0005-explainable-scheduling-and-optional-ai.md))
- **Property-based tests** (CsCheck) for the engine: precedence, capacity,
  determinism, a makespan lower bound and "never worse than the pure rule".
- Central business validation, SQLite constraints, typed mutation results and
  localized recovery/error states; explicit browser-storage recovery with
  corrupt-payload export, confirmed reset, schema-mismatch protection and
  WAL-safe snapshots. ([ADR 0006](docs/adr/0006-explicit-browser-storage-recovery.md))
- Structured all-or-nothing schedule preparation diagnostics, work-center
  parallel capacity, deterministic scheduling budgets, checked time arithmetic,
  cooperative cancellation and a reproducible performance scenario runner.
- ErrorBoundary recovery, modal dialog/focus/Escape semantics and dynamic
  document language.
- CodeQL (security-extended) and a Quality workflow (formatting, documentation
  links, resource files); one transitive SQLite advisory suppressed with a
  documented justification in `Directory.Build.props`.

## [0.1.0] — 2026-07-08

Initial public release.

### Added

- **Work plans / routings** — create, edit, search and filter by status
  (Draft / Released / Archived), with an operations editor that recalculates
  total time and estimated cost live.
- **Work centers** — master data with hourly rates and cost centers, plus a
  delete guard for centers still referenced by operations.
- **Dashboard** — key figures, a status distribution and recently updated plans.
- **Production scheduling** — a pure, dependency-free finite-capacity engine:
  four due-date rules, six dispatch rules, seeded multi-start plus local-search
  optimisation, a deterministic PRNG, a Gantt chart and tardiness KPIs.
- **Real in-browser database** — EF Core + SQLite compiled to WebAssembly and
  persisted to `localStorage`, with a schema-version guard.
- **Bilingual UI (EN / DE)** — runtime-switchable via `IStringLocalizer`/`.resx`.
- **Four test layers** — engine unit + architecture tests, EF→domain mapper
  tests, bUnit component tests and Playwright end-to-end scenarios.
- **CI/CD** — per-layer test workflows on pull requests and a test-gated
  GitHub Pages deployment.

[Unreleased]: https://github.com/aco993/WorkPlanStudio/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/aco993/WorkPlanStudio/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/aco993/WorkPlanStudio/releases/tag/v0.1.0
