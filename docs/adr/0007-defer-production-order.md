# ADR 0007: Defer ProductionOrder and constrain scheduling scope

- Status: Accepted
- Date: 2026-07-12

## Context

`WorkPlan` is a routing/master template, while a real schedulable job normally carries order quantity, release time, customer due time, priority, state and a routing revision. The pure engine supports an explicit due second, but the application model has no per-order due date. Exposing “Explicit” in the UI therefore implied a feature the application could not supply honestly.

## Decision

Do not add a partial `ProductionOrder` workflow during hardening. Hide the Explicit due-date rule in the application UI and document that each released work plan is currently one demonstration scheduling job. Retain the engine capability because it is valid reusable domain functionality and is covered by unit tests.

## Consequences

The demo remains coherent and smaller. It does not support customer orders, multiple orders per routing, order-specific quantities/releases/due dates or routing revision snapshots. A future `ProductionOrder` feature must include persistence, schema transition, UI/validation, order lifecycle and tests together; the scheduler should then consume orders rather than master plans.
