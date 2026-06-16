# 3. Ship forward scheduling only (no backward pass)

- **Status:** Accepted
- **Date:** 2026-06-16

## Context

A natural feature request is *backward* (due-date-anchored) scheduling: start from
each job's target date and work backwards. Under **shared finite capacity**,
however, two jobs scheduled backward from their due dates contend for the same
machine slots, and resolving those collisions correctly requires either a second,
reverse-time scheduler or iterated forward/backward passes. A naive backward pass
that ignores contention produces **infeasible** plans (machine over-capacity,
overlapping operations).

## Decision

Ship **forward scheduling only**. Due dates still drive the due-date dispatch rules
(EDD, Critical Ratio) and every lateness / tardiness KPI, so the targets earn their
keep. There is no `ScheduleDirection` enum — a dead, never-handled option would
just invite confusion.

## Consequences

- ✅ One clean scheduler whose every output is feasible by construction.
- ✅ Simpler code and a smaller, fully-covered test surface.
- ✅ No risk of emitting an infeasible plan from a half-built backward mode.
- ➖ No backward pass today; it is recorded as a possible future extension in
  [`docs/SCHEDULING.md`](../SCHEDULING.md).
