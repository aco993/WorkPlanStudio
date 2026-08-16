# 11. Schedule production orders that own an immutable routing snapshot

- **Status:** Accepted
- **Date:** 2026-08-16
- **Supersedes:** [ADR 0007](0007-defer-production-order.md), which deferred exactly this
- **Origin:** ported from the `codex/production-platform` branch (PR #10), reworked to need no server

## Context

The app scheduled **work plans** directly. A work plan is master data: it
describes how a part is made in general, and the editor lets anyone change it at
any time. Scheduling it directly has two consequences that are hard to defend.

First, an edit silently rewrites work that is already on the shop floor. Change
an operation's run time and every schedule that ever contained that routing
changes with it, including for jobs that were released last week to a different
specification.

Second, there was no source of a customer due date, so every target had to be
*derived* from processing time — TWK, NOP, SLK, CON. `DueDateRule.Explicit`
existed in the engine but the UI hid it, because nothing could supply the date it
consumes. ADR 0007 recorded that honestly and deferred the fix.

## Decision

Introduce `ProductionOrder`: a quantity of a part, a release date, a customer due
date and a priority. **Releasing an order captures the routing as a serialized
snapshot**, and the scheduler reads that snapshot — never the live plan.

The plan-based mapping was **removed**, not left beside the new one. Two ways to
schedule, one of which is known to be wrong, is worse than one.

Deliberately different from the originating branch:

- **No owner, no row version.** Those exist there because the platform is
  multi-tenant and concurrent. This app has one user and one browser.
- **The snapshot is a blob, not copied rows.** It is never queried, only
  replayed. Copied rows invite someone to "fix" them later, which is the thing
  the snapshot exists to prevent.
- **A format version rides along**, so a snapshot written by an older build is
  recognised rather than mis-deserialized into something plausible but wrong.

## Consequences

- ✅ Editing a work plan cannot change an order already released from it. Proved
  by test and in a browser: raising an operation's run time from 0.8 to 66
  minutes on a released plan left the schedule at 52.2 h, unchanged.
- ✅ `DueDateRule.Explicit` works and is now the default. Targets can come from
  the customer instead of always being derived from the work content.
- ✅ Quantity moves to the order, where it belongs. A work plan's lot size is a
  costing default; how many to build is a property of the order.
- ✅ Release and due dates give the engine real `ReleaseSeconds`, so FIFO stops
  being degenerate — jobs no longer all arrive at second 0.
- ➖ One more entity, one more page, and a second status concept. "Released" now
  means something for both plans and orders, which needs care in the copy.
- ➖ A snapshot can name a work center that has since been deactivated or
  deleted. That is reported as a rejected order rather than silently repaired —
  the planner has to decide.
- ➖ No re-release. Changing what an order will be built to means cancelling it
  and raising a new one, which is deliberate but blunt.
- ➖ Storage grows: each order carries a copy of its routing. Irrelevant at demo
  scale, a real consideration against the localStorage budget at scale.
