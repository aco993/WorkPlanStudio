# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/your-username/WorkPlanStudio/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/your-username/WorkPlanStudio/releases/tag/v0.1.0
