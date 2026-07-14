# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Hosted production API with Identity cookies, owner-scoped authorization,
  antiforgery validation, rate limiting, audit records and health checks.
- Shared Domain/Contracts projects, PostgreSQL persistence with separate migrations,
  and `ProductionOrder` with immutable routing snapshots.
- Machine calendars, downtime, setup transitions and persisted background schedule runs.
- OpenTelemetry, Docker Compose, backup/restore scripts and API integration tests.

- Central business validation, SQLite constraints, typed mutation results and
  localized recovery/error states.
- Explicit browser-storage recovery with corrupt-payload export, confirmed reset,
  schema-mismatch protection and WAL-safe snapshots.
- Structured all-or-nothing schedule preparation diagnostics and work-center
  parallel capacity.
- Deterministic scheduling budgets, checked time arithmetic, cooperative
  cancellation and a reproducible performance scenario runner.
- ErrorBoundary recovery, modal dialog/focus/Escape semantics and dynamic document
  language.

- **Schedule assistant** — a deterministic, on-device explanation of each
  scheduling run (the bottleneck work center, why each job is late, and one
  *computed* recommendation), rendered as localized EN/DE narration. Plus an
  optional **bring-your-own-key** AI narrator behind a provider abstraction that
  falls back to the built-in explanation on any error. The key is stored only in
  the browser. See [`docs/AI-ASSISTANT.md`](docs/AI-ASSISTANT.md) and
  [ADR 0005](docs/adr/0005-explainable-scheduling-and-optional-ai.md).
- **Property-based tests** (CsCheck) for the scheduling engine: hundreds of
  randomly generated problems assert the invariants — precedence, capacity,
  determinism, a makespan lower bound and "never worse than the pure rule".

### Changed

- Server scheduling now consumes real orders with explicit release/due dates;
  the static demo retains its smaller routing-based scenario.
- Production AI keys moved behind an authenticated, timed and rate-limited server proxy.
- Upgraded the native SQLite bundle to 3.0.3 and removed the previous advisory suppression.

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

[Unreleased]: https://github.com/aco993/WorkPlanStudio/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/aco993/WorkPlanStudio/releases/tag/v0.1.0
