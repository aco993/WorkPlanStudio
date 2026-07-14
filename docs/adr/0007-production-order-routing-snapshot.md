# ADR 0007: Production orders own immutable routing snapshots

- Status: Accepted
- Date: 2026-07-13

## Context

`WorkPlan` is master routing data, not an executable job. Scheduling it directly cannot honestly represent order quantity, release time, customer due time, priority or the routing revision that production actually received.

## Decision

Introduce owner-scoped `ProductionOrder` with quantity, release/due UTC timestamps, priority, status and a serialized immutable `WorkPlanDto` snapshot captured when the order is created. Production scheduling consumes released orders and the snapshot, never the current mutable master routing. The offline Pages demo retains its smaller routing-based example scheduler.

## Consequences

- A later work-plan edit cannot silently change a released order.
- Explicit due-date scheduling now has a real application source.
- Snapshot serialization is deliberately simple and auditable, but schema evolution needs an explicit compatibility strategy before snapshot DTOs change incompatibly.
- Orders, calendars and durable runs require server mode and are not claimed by the static Pages demo.
