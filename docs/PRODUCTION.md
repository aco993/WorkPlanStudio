# Production runbook

## First deployment

1. Copy `.env.example` to `.env` and replace the database password, allowed host and one-time bootstrap administrator password.
2. Put a TLS reverse proxy or ingress in front of port 8080.
3. Run `docker compose up --build -d`.
4. Confirm `GET /health/live` and `GET /api/health/ready` return success.
5. Sign in as the bootstrap administrator, then remove both bootstrap variables from `.env` and restart the API.

The compose profile applies PostgreSQL migrations at startup. For controlled deployments, set `Database__ApplyMigrationsOnStartup=false` after running the generated migration script in the release pipeline.

Authentication Data Protection keys are persisted in the `data-protection-keys` volume, so sessions survive container replacement. Back up this volume with the database, restrict host access to it, and protect the key ring with a certificate or managed key service when deploying beyond a single trusted host.

## Backup and restore

Create a PostgreSQL custom-format backup:

```powershell
./scripts/backup.ps1
```

Restore only during a maintenance window, after taking a fresh backup:

```powershell
./scripts/restore.ps1 -BackupFile ./backups/workplanstudio-YYYYMMDD-HHMMSS.dump -ConfirmProductionRestore
```

Always test backups by restoring into an isolated database. Encrypt and copy verified backups off-host according to the organization's retention policy.

## Monitoring

- Liveness: `/health/live`
- Readiness including database/migrations: `/api/health/ready`
- Traces and metrics: set `OTEL_EXPORTER_OTLP_ENDPOINT` to an internal OTLP collector.
- Alert on repeated 5xx responses, readiness failures, queue failures, database saturation and authentication lockouts.

## Rollback

Keep the previous container image and a pre-migration backup. Application rollback is safe only when the previous binary understands the current schema; otherwise restore the corresponding backup in a maintenance window.
