# Production hardening baseline

Date: 2026-07-12

Baseline commit: `0e1e3c514f35d8479dfe1bdc51905d31de8729b3`

Working branch: `codex/hardening-mid-level-readiness`

This document records the verified state before production-hardening changes. It is an audit trail, not a claim that the baseline was production-ready.

## Verified quality baseline

All commands were run from the repository root with .NET SDK 10.0.301 and the pinned 10.0.300 feature band.

| Check | Command | Result |
| --- | --- | --- |
| Restore | `dotnet restore WorkPlanStudio.slnx --disable-parallel` | Passed |
| Release build | `dotnet build WorkPlanStudio.slnx -c Release --no-restore` | Passed; two expected `WASM0001` warning groups, zero errors |
| Formatting | `dotnet format WorkPlanStudio.slnx --verify-no-changes --no-restore` | Passed |
| Scheduling tests | `dotnet test tests/WorkPlanStudio.Scheduling.Tests/WorkPlanStudio.Scheduling.Tests.csproj -c Release --no-build --no-restore --no-progress` | 83 passed, 0 failed, 0 skipped |
| Web tests | `dotnet test tests/WorkPlanStudio.Web.Tests/WorkPlanStudio.Web.Tests.csproj -c Release --no-build --no-restore --no-progress` | 26 passed, 0 failed, 0 skipped |
| Browser E2E | Local app plus Playwright E2E project | 6 passed, 0 failed, 0 skipped |
| Coverage | Coverlet/Cobertura over the scheduling test project | 97.90% line, 91.61% branch |
| Patch hygiene | `git diff --check` | Passed |
| Direct packages | `dotnet list WorkPlanStudio.slnx package --outdated` | No direct updates available |
| Vulnerabilities | `dotnet list WorkPlanStudio.slnx package --vulnerable --include-transitive` | High-severity transitive advisory in `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (`GHSA-2m69-gcr7-jv3q`) |

The test commands are intentionally sequential. Running multiple .NET test hosts concurrently caused environmental contention during the earlier review and is not evidence of a project test-runner defect.

## Reproduced risks

| Priority | Risk | Reproduction/evidence | Required acceptance criterion |
| --- | --- | --- | --- |
| P0 | Browser data loss after a successful save | Create a work center, reload the browser, and observe that seed data returns without the created row. Persistence currently reads the SQLite file before the active context/connection is disposed. | A save followed by database reinitialization/reload preserves the committed entity; covered by an automated regression test. |
| P0 | Invalid released plan crashes scheduling | The UI accepted lot size `-1`; scheduling threw `ArgumentException` and left the action busy/disabled until reload. | Invalid data is rejected before persistence and before engine invocation; the UI shows localized, actionable errors and always clears busy state. |
| P0 | Partial routing is silently scheduled | A two-operation plan with one inactive work center mapped to a one-step job without a diagnostic. | A plan is either mapped completely or rejected completely with structured plan/operation/reason diagnostics. |
| P0 | Corrupt or incompatible browser storage is unsafe | Storage initialization directly decodes Base64/opens SQLite; schema mismatch is silently ignored and then overwritten by a fresh seeded database. | Invalid Base64, invalid/truncated SQLite, unsupported schema, read failure, write/quota failure, export, and explicit reset have typed outcomes and tests; incompatible data is never silently overwritten. |
| P0 | Unbounded scheduling inputs can freeze the UI | Scheduling parameters have lower-bound UI hints but no centralized upper bounds or cancellation propagation. | Central deterministic limits are enforced at all entry points; long loops observe cancellation; limits have boundary tests and documented rationale. |
| P1 | Capacity is hard-coded | Mapping creates every work center with capacity 1. | Supported parallel capacity is an explicit validated domain/persistence/UI field and is mapped to the scheduling engine. |
| P1 | Accessibility and recovery gaps | The German UI still reports `<html lang="en">`; modal semantics/focus/Escape behavior and a top-level error boundary are missing. | Document language follows culture; modal keyboard behavior is tested; unexpected failures have a recoverable localized boundary. |
| P1 | Assistant configuration is weakly constrained | Any non-empty endpoint/key is accepted and the shared client has no operation timeout. | Endpoint policy and a finite timeout are enforced; cancellation still propagates; credentials are never logged. |

Keyboard-only navigation was attempted manually, but the automation control could not provide reliable focus evidence. It is therefore not recorded as passed; automated focus and keyboard assertions are part of the hardening scope.

## Threat model and trust boundaries

- Form input, imported browser storage, persisted SQLite rows, URL/configuration values, and cancellation signals are untrusted.
- The pure scheduling library must receive only validated, complete jobs expressed in integer seconds.
- Browser storage is durable application state but is not a secret vault. Assistant keys are bring-your-own-key data and require explicit documentation and safe logging behavior.
- Storage write failure must not be presented as a durable save. Schema incompatibility requires a visible export/reset decision.
- Determinism is a product constraint: budgets are count-based, not elapsed-time cutoffs, and the seeded PRNG remains the only randomness source.

## Implementation sequence

1. Add failing regression tests for persistence, validation, all-or-nothing mapping, and storage recovery states.
2. Introduce centralized validation and typed outcomes at UI, service, storage, mapper, and engine boundaries.
3. Make browser persistence occur only after the SQLite connection is closed; add explicit export/reset recovery and an ADR for schema policy.
4. Add bounded scheduling parameters, checked time conversion, cancellation propagation, and reliable UI cleanup/error mapping.
5. Add capacity support, database constraints/indexes, release/deactivation invariants, and remove misleading unsupported UI choices.
6. Add error-boundary, localization, modal keyboard/focus, assistant endpoint/timeout, and security documentation hardening.
7. Add deterministic performance scenarios and architecture/interview documentation.
8. Run the complete release quality gate, inspect the final diff, push the branch, and open a reviewable pull request without merging it.

## Scope decision

Production orders are not added in this hardening pass. The scheduling engine can retain reusable capabilities, but the application will not expose a misleading partial workflow. The limitation and a future migration path will be captured in an ADR.
