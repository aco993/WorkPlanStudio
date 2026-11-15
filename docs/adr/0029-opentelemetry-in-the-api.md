# 29. OpenTelemetry in the API, exported only when somebody is listening

- **Status:** Accepted
- **Date:** 2026-11-15
- **Relates to:** [ADR 0020](0020-optional-backend-and-real-auth.md), which made the
  backend optional and real

## Context

The optional backend logged through `ILogger` and stopped there. That answers
"what happened" one request at a time, if somebody reads the log. It does not
answer the questions an operator asks first: how long a schedule run takes and
whether the time goes to the database or to the search, how many runs end in a
schedule versus a preparation error, and whether sign-in is being refused more
often than usual.

ASP.NET Core already reports every request as an `Activity` and as
`http.server.request.duration`. What it cannot see is what a request *did*.

## Decision

Use OpenTelemetry — the SDK, the ASP.NET Core instrumentation and the OTLP
exporter, all from the `OpenTelemetry.*` 1.21 packages — for traces, metrics and
logs, with one resource (`service.name = workplan-studio-api`).

**The API's own signals**, named in `Telemetry/ApiTelemetry.cs`:

| Signal | Kind | Tags |
| --- | --- | --- |
| `schedule.run` → `schedule.load`, `schedule.solve` | spans, under the HTTP request span | dispatch rule, orders / work centers / absences read, jobs and operations placed, outcome |
| `workplan.schedule.run.duration` | histogram, seconds | `outcome`: `scheduled`, `not_schedulable`, `invalid_parameters`, `cancelled`, `failed` |
| `workplan.schedule.operations` | counter | — |
| `workplan.auth.sign_in.rejected` | counter | `reason`: `invalid_credentials`, `locked_out` |

The outcome is a tag on the duration, not a separate metric, so a fast
preparation failure is never averaged in with real solves.

**No person in a tag.** No user name, user id or client address goes into a span
attribute or a metric. A refused sign-in is counted by reason — enough to see a
guessing campaign, not enough to make a list of accounts. The log line next to it
keeps the user id it always had; logs go to whatever store the operator chose for
logs, metrics are aggregated and shipped more widely.

**Exported only when configured.** The OTLP exporter is registered when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set and not otherwise. Its default target is
`localhost:4317`; registering it unconditionally would make every instance —
including the test host — retry a collector that does not exist. Without it the
SDK still runs: trace ids reach the logs and in-process listeners see every
instrument.

**Health probes are not traced.** An orchestrator calls `/health` every few
seconds; a trace store full of them is a trace store nobody can read.

The instruments are created from the host's `IMeterFactory`, not a static
`Meter`, so each host — and each test host — owns its own.

## Consequences

- `TelemetryTests` attaches an in-memory exporter to the pipeline the API ships
  with and asserts on what was **exported**: the span tree and its tags, the
  duration by outcome, the refused-sign-in counter carrying no account name, and
  that `/health` is filtered while an ordinary route is not. Removing the filter
  fails the last one; that was checked.
- Verified end to end against a local OTLP/HTTP receiver: one schedule run and one
  refused sign-in produced `/v1/traces` containing `schedule.run`, `/v1/metrics`
  containing `workplan.auth.sign_in.rejected`, and `/v1/logs`.
- `docker-compose.yml` gains an `observability` profile with the .NET Aspire
  dashboard as a stand-alone OTLP receiver and viewer. That profile was written
  without Docker on the authoring machine and has not been run; the OTLP path it
  relies on is the one verified above.
- Three packages more on the server. None reaches the browser app — the
  architecture tests forbid the app from referencing the API.
- Not done: EF Core spans. There is no stable EF Core instrumentation package;
  `schedule.load` covers the reads that matter as one span instead.
