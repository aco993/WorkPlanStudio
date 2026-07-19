# Security posture

WorkPlan Studio has two explicit trust modes:

- **Production server mode** uses ASP.NET Core Identity, owner-scoped PostgreSQL/SQLite persistence and an authenticated API.
- **Offline demo mode** keeps a disposable SQLite snapshot in browser storage. It is for demonstrations, not confidential or shared-machine data.

## Production controls

- Identity cookies are `HttpOnly`, `SameSite=Strict`, secure outside Development and use the `__Host-` prefix.
- Every state-changing API request validates an antiforgery token. Authorization and owner predicates are applied at the API boundary.
- Login attempts lock after five failures; auth, general API and AI routes have separate rate limits.
- Optimistic concurrency versions prevent silent lost updates. Released production orders retain an immutable routing snapshot.
- Audit entries record actor, action, entity and request correlation without recording passwords or provider keys.
- CSP, HSTS, MIME sniffing protection, referrer policy and restrictive permissions policy are emitted by the server.
- Production startup fails if PostgreSQL mode receives a non-PostgreSQL connection string. Registration is disabled by default.
- The container runs as a non-root user, drops Linux capabilities, uses a read-only filesystem and exposes liveness/readiness endpoints.

## Secrets and AI

Production AI calls go through `/api/assistant/narrate`. The provider endpoint, model and key come only from server configuration or secret storage. The endpoint is fixed by the operator, must be HTTPS, has a 20-second timeout and a dedicated per-user rate limit. Only a bounded factual schedule summary is sent.

The legacy BYOK option remains available only in offline demo mode. Browser storage is not a secret vault; do not use valuable credentials there.

## Dependencies

The browser explicitly uses the patched `SQLitePCLRaw.bundle_e_sqlite3` 3.0.4 graph. The previous `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 advisory suppression has been removed. Because `dotnet list package --vulnerable` can exit successfully while merely reporting findings, CI parses its JSON output and fails when any direct or transitive vulnerability is present. `AngleSharp` is also pinned directly above the transitive bUnit advisory fix.

GitHub Actions and container base images are pinned to immutable commit/image digests. Dependabot is configured for NuGet, GitHub Actions and Docker, while CodeQL runs the `security-extended` C# query suite. These automated checks reduce known-risk exposure; they do not replace threat modelling or an independent penetration test.

## Operations

- Terminate TLS at the ingress/reverse proxy and set `ALLOWED_HOSTS` to the public hostname.
- Keep PostgreSQL and OTLP endpoints on private networks.
- Remove bootstrap administrator variables after the first successful start.
- Run and verify encrypted backups regularly; test restore in an isolated environment.
- Rotate database, administrator and AI credentials after suspected exposure.

Report security issues through GitHub private vulnerability reporting. Never put secrets or exploit payloads in a public issue.
