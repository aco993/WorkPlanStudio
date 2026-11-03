# 26. Put the result before the parameters that produced it

- **Status:** Accepted
- **Date:** 2026-11-03
- **Extends:** [ADR 0023](0023-accessible-gantt-and-responsive-tables.md), which made the
  chart operable without a mouse — and left it a screen and a half below the fold on
  the device most likely to have no mouse

## Context

The Scheduling page is the one the README sends a reader to, and the one the
whole project is an argument for. It opened on a nine-field form.

Measured in a browser at 375 × 812, the viewport of a phone that is still common:

| Element | Distance from the top of the document |
| --- | --- |
| Parameters card | 216 px |
| KPI row (makespan, on-time, tardiness, utilisation) | 934 px |
| Gantt chart | 1,181 px |
| Jobs & targets table | 1,684 px |
| Schedule assistant | 2,293 px |

The Gantt began 1.45 screens down. A reader arriving from a link — which is how
nearly all of them arrive — saw a heading, two sentences and a settings form, and
had to decide on the strength of that whether to keep scrolling. The page ran the
schedule automatically on arrival and reported it off-screen.

The desktop layout was never the problem: a 290 px rail on the left and the result
to its right reads correctly at 1,440 px, and that is what the screenshots in the
README show. The defect only existed in the single-column layout below 920 px,
where the grid collapses and markup order becomes screen order.

## Decision

**The result comes first, in the markup, and the parameter rail follows it.** At
desktop width the rail is placed in the right-hand column and stays sticky; below
920 px the grid collapses to one column and the reader gets KPIs, chart, table and
narration before the form.

The obvious alternative was to leave the markup alone and reorder visually with
`order` or explicit grid placement in the narrow breakpoint. That was rejected.
A layout whose visual order and focus order disagree relocates the problem instead
of solving it: a keyboard user tabbing out of the page header would jump to a form
rendered below the fold and then travel back up to the result. WCAG 2.4.3 is about
focus order preserving meaning, and "the controls are somewhere under the output"
does not. Moving the rail in the markup and to the right-hand column is the only
arrangement where markup order, focus order, reading order and visual order agree
at *every* width — left-to-right at desktop and top-to-bottom on a phone are the
same sequence.

A collapsed `<details>` on mobile was also considered and rejected for now: making
it open at desktop width and closed on a phone needs the viewport in managed code,
which means a first render in the wrong state and a visible flip. Nothing stops it
being added later; the ordering fixed here is the part that was wrong.

## Consequences

- The chart is reachable without scrolling on a phone, and the KPI row is the first
  thing under the heading at every width.
- Changing a parameter on a phone now means scrolling past the result to the form.
  The primary action — Generate schedule — is in the page header and did not move,
  so the common path (arrive, read, re-run) is unaffected; the experimental path
  (arrive, tune, re-run) is one scroll longer. That is the right way round: most
  readers never change a parameter, and the ones who do came to.
- The rail changed side at desktop width. The three README screenshots that show the
  Scheduling page were taken with it on the left and are now wrong; the visual
  baselines for that route change with it.
- No change to what any control does, to the search, or to the announcement region.
