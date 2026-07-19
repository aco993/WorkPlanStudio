# Production operations runbook

This runbook is the executable baseline for the hosted WorkPlan Studio platform. It does not replace an organisation's incident-management, legal, privacy or regulated-system procedures.

## Required production topology

- At least two API/worker replicas behind a health-checking load balancer.
- One managed PostgreSQL primary with point-in-time recovery and tested backups.
- A shared, durable Data Protection key store. Losing it invalidates cookies and password-reset tokens.
- An SMTP provider with authenticated TLS delivery, bounce monitoring and a verified sender domain.
- Central OpenTelemetry traces/metrics/logs and alerts for readiness, HTTP 5xx, queue age, failed runs and SMTP failures.
- A single migration job before rolling out application replicas; application containers should normally use `Database__ApplyMigrationsOnStartup=false`.

## Deployment and rollback

1. Back up PostgreSQL and record the migration/version currently deployed.
2. Generate and review both provider migration scripts in CI.
3. Apply the PostgreSQL script once using a dedicated migration identity.
4. Roll out one canary replica, verify `/health/live` and `/api/health/ready`, then roll out the remaining replicas.
5. Verify that all replicas share the same Data Protection key ring and database.
6. Roll back application code if health/error SLOs regress. Database rollback requires a reviewed compensating migration or restore; never run migration `Down` blindly on production data.

## Worker failover drill

1. Queue a non-trivial schedule and record its run ID.
2. Query `ScheduleRuns` and record `LeaseOwner`, `LeaseExpiresUtc`, `AttemptCount` and status.
3. Terminate the owning replica without graceful shutdown.
4. Confirm another replica claims the run only after lease expiry, `AttemptCount` increments once, and no second completed result is written.
5. Send a cancellation request through a different replica and confirm the database records `CancellationRequestedUtc` before the worker stops.
6. Record recovery time. The current lease configuration implies an upper failover bound of roughly two minutes plus polling and scheduling time.

The automated PostgreSQL lease suite proves one-winner claims, expired-lease takeover and stale-owner completion fencing on PostgreSQL. The broader API suite covers heartbeat/cancellation behaviour. The drill above proves the deployment/network/orchestrator layer and must be repeated in each target environment.

## Backup and restore drill

- Run the repository backup script or managed PostgreSQL snapshot/PITR procedure.
- Restore to an isolated database, start the same application version, and require readiness plus a representative authenticated read.
- Confirm identity records, routing snapshots, schedule history and audit entries.
- Record measured RPO/RTO and evidence location. A backup that has not been restored is not accepted as evidence.

## Security and account recovery

- Password-reset tokens expire after one hour, are single-use through ASP.NET Core Identity, and must be delivered only through configured SMTP.
- The request endpoint always returns the same response to resist account enumeration; SMTP failures are server-side errors/alerts, not user-visible identity signals.
- Rotate SMTP and assistant credentials through the platform secret manager. Never place them in Compose files or repository secrets in plaintext.
- On suspected compromise: disable affected users, rotate secrets and Data Protection keys when session invalidation is required, preserve audit/log evidence, and follow the organisation's incident process.

## Scheduled evidence

`.github/workflows/production-evidence.yml` configures a weekly four-hour, rate-controlled health/readiness soak and an OWASP ZAP passive baseline. Pull requests use a five-minute gate; manual runs accept configurable duration and target RPS. The previous unbounded worker loop was replaced because it could overload the hosted runner and lose evidence near the end of a long run. A complete scheduled artifact must be inspected before claiming that a four-hour run passed. The ZAP baseline is DAST evidence, not an independent penetration test.
