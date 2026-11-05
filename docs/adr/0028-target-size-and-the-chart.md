# 28. Measure target size, and except the chart on width only

- **Status:** Accepted
- **Date:** 2026-11-05
- **Extends:** [ADR 0023](0023-accessible-gantt-and-responsive-tables.md), which made every
  Gantt mark a button with an accessible name — and left those buttons three pixels wide

## Context

The accessibility suite scans every route with axe, in both themes and both
languages, and has been green for months. **axe-core has no rule for WCAG 2.2 AA
2.5.8 Target Size (Minimum).** Measured in a browser against the published site
at 375 × 812, 109 interactive elements were under 24 × 24 CSS pixels. Nothing in
the suite had ever looked, and nothing in it could.

Two further facts shaped the answer rather than complicating it.

**The chart's marks are 108 of the 109.** 78 closed stretches at 3–17 px wide and
30 bars at 12–19 px. A bar's width is the operation's duration; a closed
stretch's width is how long the work centre stood still. Widening a sliver to
24 px would not make the chart easier to hit — it would make it state a duration
the schedule does not have.

**Their height was 20 px for no reason at all.** The lane was 26 px and the bar
inset 3 px top and bottom. Nothing about the chart required that number.

**Every scan in the suite ran at 1320 × 980.** A target shrinks when the page
does, so the one width where this criterion bites was the one width never
scanned.

## Decision

**Measure it, in a test, at phone width.** `TargetSizeE2ETests` walks every route
at 375 × 812 and fails with the size and the accessible name of anything under
the floor. The criterion that the automated scan cannot assert is asserted by a
test written for it, rather than described in a document.

**Measure the effective target, not the painted control.** A control wrapped in
its own `<label>` is measured as the label, because clicking the words is what
operates it. Measuring the 13 px box instead reported five defects this
application does not have, and a check that cries wolf is one people learn to
re-run rather than read.

**Except the chart's marks on width, and only on width.** 2.5.8's **Essential**
exception — "a particular presentation of the target is essential ... for the
information being conveyed" — fits a Gantt bar almost to the letter. The
**Equivalent** exception covers it a second time: the same data is a table on the
same page, one visible toggle away, and that table's controls are full size.
Their height is held to 24 px like everything else, so the lane is 32 px now.

**Assert the exception rather than claim it.** A second test checks that the
table exists, that the toggle leading to it is itself a full-size target, and
that the table has rows. An exception claimed without checking is a suppression
with better manners, and this repository already learned that lesson once with
the pinned axe violations.

**Scan the phone layout with axe too.** Separate from target size: below 920 px
the navigation becomes a drawer, tables scroll inside their cards and the
schedule stacks. That tree had never been scanned. It comes back clean, which is
worth knowing rather than assuming.

## Consequences

- Eight controls outside the chart gained height: a card's "View all" (51 × 20),
  a work-plan number in a table cell (44 × 13), the import page's example
  download (304 × 20), three column sort buttons (29–48 × 15) and a checkbox
  label that fitted on one line (305 × 19). In every case only the height was
  wrong, so only the height is set and the text keeps its size.
- `.btn` gained `min-height: 24px` on the base rule rather than on the one
  variant that was short, so a future size modifier cannot drop a button back
  under the floor.
- The Gantt lane is six pixels taller. Seven lanes, so the chart grew by 42 px.
- The browser suite grew by 25 tests and about a minute.
- The visual baselines for the routes whose controls changed height move with
  this, and are regenerated on Linux the usual way (ADR 0021).
- **What this does not claim:** the chart's marks are still 3 px wide and still
  hard to hit with a finger. The exception says that is allowed, not that it is
  pleasant. The table is the answer for a touch user, and making that table
  easier to reach than a toggle under the chart is work this ADR does not do.
