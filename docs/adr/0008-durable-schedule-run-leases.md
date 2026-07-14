# ADR 0008: Durable schedule-run leases

- Status: Accepted
- Date: 2026-07-14

## Context

The original bounded in-process channel guaranteed a single reader only inside one API process. Running two replicas could therefore execute the same persisted schedule run, while cancellation could reach only the replica that received the HTTP request.

## Decision

The database is the durable queue and coordination boundary. A worker may execute a run only after an atomic conditional update assigns its unique lease owner and expiry. Active workers renew the lease every ten seconds. Another replica may reclaim only an expired lease. Cancellation is persisted and the lease heartbeat propagates it to the executing cancellation token. The local bounded channel remains a low-latency wake-up hint; polling guarantees eventual pickup if that hint is full or delivered to another replica.

This design is implemented with EF Core provider-neutral conditional updates and is exercised with SQLite and PostgreSQL migrations.

## Consequences

- Multiple API replicas can safely share PostgreSQL without duplicate run execution.
- Crashed workers are recovered after the two-minute lease expires.
- Cancellation propagation takes at most one heartbeat interval when routed to another replica.
- Each replica still processes one scheduling run at a time. Horizontal throughput comes from adding replicas; fairness and tenant quotas remain future work.
