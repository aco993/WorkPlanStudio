# Security posture

WorkPlan Studio is a public static demo, not a multi-user production system. It has no server identity, authorization boundary or trusted server-side storage.

## Data and trust boundaries

- Work plans and settings stay in the current browser origin's `localStorage`.
- `localStorage` is readable by JavaScript running on that origin. It is not a secret vault and is inappropriate for shared machines or valuable credentials.
- Database payloads are untrusted on startup. Version, Base64 shape, SQLite header, minimum size, `PRAGMA quick_check` and expected schema access are verified before the UI is enabled.
- Application code issues fixed EF-generated queries; it does not accept SQL from the user.
- Storage incompatibility or corruption never silently reseeds over the old payload. The recovery screen supports export and an explicit two-step reset.

## Optional BYOK narrator

The core application and deterministic explanation work without AI. If enabled:

- the key is stored in browser `localStorage` and is never logged;
- only an absolute HTTPS endpoint is accepted; HTTP is allowed only for loopback development;
- user-info, query and fragment components are rejected to reduce accidental credential routing;
- requests have a 15-second timeout and caller cancellation is propagated distinctly;
- provider failures fall back to rule-based text without exposing raw exception messages;
- only structured schedule facts are sent, not the SQLite database.

A production design should put the provider behind a backend proxy, keep the key in server-side secret storage, enforce tenant authorization and add audit/rate controls.

## Tracked SQLite advisory

`Microsoft.EntityFrameworkCore.Sqlite` 10.0.9 currently brings `SQLitePCLRaw.lib.e_sqlite3` 2.1.11. NuGet audit reports [GHSA-2m69-gcr7-jv3q / CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) as high severity. The advisory affects SQLite before 3.50.2 and describes memory corruption involving excessive aggregate terms. GitHub currently lists affected package versions through 2.1.11 and no patched version on that package line. The [NuGet package page](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.11) also marks 2.1.11 vulnerable and deprecated.

Risk acceptance for this portfolio build:

- exposure is reduced because the app executes fixed EF queries and accepts no arbitrary SQL;
- an attacker would already need control of same-origin storage/script execution to supply a database payload;
- a valid database file alone does not add attacker-controlled aggregate SQL to the application's fixed query set;
- impact is still not claimed to be zero, and imported browser state is treated as untrusted.

The single advisory remains explicitly suppressed in `Directory.Build.props` so all other NuGet advisories still fail audit. Do not force a transitive 3.x override under EF Core 10 without an officially supported and tested dependency combination. Recheck on every EF/SQLite update and remove the suppression as soon as a supported patched chain exists.

Tracking item `SEC-001`: owner is the repository maintainer; review on every Dependabot EF/SQLite update and at least before each tagged release. Exit criterion: `dotnet list WorkPlanStudio.slnx package --vulnerable --include-transitive` no longer reports the advisory with a supported package graph, all SQLite/WASM/E2E tests pass, and the suppression is removed in the same change.

## Reporting

Do not put secrets or exploit payloads in a public issue. Use GitHub's private vulnerability reporting if enabled for the repository; otherwise contact the repository owner privately.
