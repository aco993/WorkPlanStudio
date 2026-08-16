# 10. Model calendars as a repeating period, and setup by family

- **Status:** Accepted
- **Date:** 2026-08-16
- **Origin:** ported from the `codex/production-platform` branch (PR #10), reworked

## Context

Two constraints were listed as deliberately absent: a working-day calendar, and
sequence-dependent setup times. Both are ordinary in manufacturing scheduling,
and their absence made the engine's realism claim thinner than it needed to be.

The obvious model for a calendar is a list of available windows. It has a flaw:
the list is finite, so a schedule that runs past the last window has nowhere to
go. The originating branch threw an `InvalidOperationException` at that point —
from inside the dispatcher, which the search calls thousands of times, so one
unplaceable operation would abort an entire run instead of rejecting one
candidate order.

## Decision

**Calendars repeat.** A work center declares its available windows *within one
period* plus the period length. `[08:00, 16:00)` over a 24-hour period is a day
shift, and the calendar is total — placement always succeeds, so the dispatcher
has no failure mode.

**Operations are not preemptable**, so a step must fit entirely inside one
window. This is validated when the `SchedulingContext` is constructed, worst-case
change-over included. Moving the check to construction means the hot loop cannot
throw, and an impossible instance is reported as an input error the caller can
act on.

**Setup is keyed by family**, not by operation. Steps declare a family; work
centers declare a transition matrix. Same family, unlisted transition, and the
first operation on a slot all cost nothing.

**Slot choice is by earliest finish**, not earliest free clock. Change-over makes
those differ: a slot that frees later but already ran this family can finish
sooner than one free now that needs a setup.

## Consequences

- ✅ Both constraints are now real and visible. On a four-job instance the
  calendar takes makespan from 9 h to 33 h; a 2-hour change-over costs 6 hours
  when families alternate against 2 hours when grouped.
- ✅ The dispatcher has no exceptional path. Feasibility is a construction
  invariant, which is what lets the search run without defensive handling.
- ✅ The transition matrix is flattened into a lookup once per context rather
  than scanned per placement — it is queried on every slot of every step of
  every candidate order.
- ✅ Both features default to "no constraint", so existing instances are
  unaffected; a regression test pins that.
- ➖ The calendar is *uniform*: no public holidays, no half-days, no per-week
  variation. Exceptions to the pattern need a different model.
- ➖ No preemption means a step longer than the longest window is simply
  rejected. For an 8-hour shift, a 9-hour operation is unschedulable rather
  than split across two days.
- ➖ `MachineCapacity` now carries three concerns (concurrency, calendar, setup).
  Splitting them would be cleaner if either grows further.
