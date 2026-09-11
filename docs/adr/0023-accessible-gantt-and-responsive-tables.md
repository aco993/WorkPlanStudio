# 23. Make the chart operable without a mouse and stop the cards clipping the tables

- **Status:** Accepted
- **Date:** 2026-09-11
- **Extends:** [ADR 0012](0012-working-time-as-capacity.md), which put the reason for
  every idle machine on the chart — and then only in a `title` attribute

## Context

A UI audit measured two defects that between them made the application's two
signature surfaces unusable on anything but a desktop with a pointing device.

**The chart was mouse-only, and said so.** Every Gantt bar and every closed
stretch was a `<div>` with a `title`. The operation number, the setup time, the
paused time, the real start and end timestamps and the reason a machine stood
still existed nowhere else on the page. `title` is not shown on touch, cannot be
reached by keyboard and is announced inconsistently by screen readers. The
chart's own legend read *"Shaded: work center closed — hover for the reason"*.
ADR 0012's whole point — that the plan explains itself — held for one input
device.

**The cards clipped the tables.** `.card { overflow: hidden }` existed to clip
children to the card's border radius. A data table dropped into a card is wider
than a phone, so the card cut it off: not scrolled away, *gone*. Measured at
360 px, `/work-plans` needed 994 px and showed 325. The Actions column is the
last one on every list in this app, so the first thing amputated was Edit and
Delete — the application became read-only on a phone by accident. The same clip
took 26 px off a 1280 px German laptop. And because the card clipped,
`document.documentElement.scrollWidth` stayed equal to `clientWidth`, which is
exactly the assertion the mobile end-to-end test makes: the guard rail was
certifying the defect as the fix.

## Decision

**Every mark on the chart is a `<button>` with the full explanation as its
accessible name, reached through a roving tabindex.** The 137 marks of the demo
schedule collapse to one tab stop; the arrow keys walk it — left and right along
one machine's day in start-time order, up and down between machines at the same
position — Home and End jump to the ends of a lane, Enter and Space read the
mark in focus into an `aria-live` region below the chart, and Escape clears it.
The live region is rendered empty from the first paint, because a live region
inserted together with its content is announced by no screen reader.

The arrow keys' default action is cancelled from JavaScript, not with Blazor's
`@onkeydown:preventDefault`: that attribute is fixed per render rather than per
key, so using it would also swallow Tab and trap the reader inside the chart.

**The chart also exists as a table.** One row per bar and per closed stretch —
work centre, detail, start, end, duration — always in the DOM, visually hidden
until a "Show as table" toggle reveals it. A picture is not a format; the
placement of every operation and the reason behind every gap live in text too.
The same reasoning replaced `role="img" aria-label="Weekly pattern"` on the
working-time preview, a role that told assistive technology to ignore all seven
days it draws and announce the widget's own name instead.

**A `TableScroll` component is the scroll container, and a card that still holds
a table directly scrolls rather than clips.** `.card { overflow: hidden }` stays
for the radius, but `.card:has(> .data-table)` switches to
`overflow-x: auto; overflow-y: hidden`, so the fix reaches the pages that have
not adopted the component yet as well as those that have. The container is a
labelled `role="region"` with `tabindex="0"`: a scroll container only a pointer
can move is the same defect wearing a scrollbar. The affordance is a right-edge
shade painted with `background-attachment: local`, which appears only while the
content actually overflows — no script and no resize observer. The header row and
the first column stay put while it scrolls.

**One culture-aware formatter, and no numeric-only dates.** `Format` is now the
only place a number, duration, money amount or moment becomes text. Both shipped
languages write a purely numeric short date and disagree about it: `6/4/2026` is
6 April to an American reader and, read as `4.6.2026`, 4 June to a German one.
Every date the app renders therefore carries a month name. Money keeps its cents
and takes its symbol position from the culture; percentages take their spacing
from the culture rather than from the call site.

## Consequences

- ✅ The chart answers the README's question — *why did CNC-300 stand still on
  Thursday?* — from a keyboard, on a touch screen and through a screen reader.
  Verified in a browser: 137 marks, two tab stops, arrow keys moving the roving
  focus without scrolling the chart, Escape clearing the explanation.
- ✅ On `/work-plans` at 375 px the card now scrolls 856 px of table inside
  341 px of card, the Delete button reaches the viewport, and the document still
  does not scroll sideways. At 320 px — the WCAG 1.4.10 reflow threshold, and the
  400 % zoom equivalent of a 1280 px screen — no route loses content.
- ✅ Colour stopped being the only channel: a job's dot carries its letter, and a
  `forced-colors: active` block keeps the eight job hues and the weekly preview
  while switching the hatched closed stretches to border styles, which survive a
  high-contrast theme when a background image does not.
- ➖ The two end-to-end assertions that read `documentElement.scrollWidth <=
  clientWidth` still pass, because the *document* genuinely does not scroll — but
  they no longer test anything. They have to be inverted to assert that each
  table's container is scrollable and that the last action button can be brought
  into view. That project belongs to another stream; the change is written out in
  this stream's report.
- ➖ Every committed visual-regression baseline is invalidated. The token
  contrasts moved (the reds failed AA on their own tinted ground at 3.95:1), the
  culture selector grew to meet the 24 px target size, and dates render with a
  month name. The baselines have to be recaptured, and while that is being done
  they should also be captured in German, which is the language the clipping
  defect was worst in and the one language the baseline was never taken in.
- ➖ `.card:has(> .data-table)` is the weaker half of the table fix: the card
  header scrolls along with the table. It is a stopgap for the four list pages
  another stream owns, and should be deleted once they wrap their tables in
  `TableScroll`.
- ➖ The roving focus is moved by element id through JavaScript rather than by
  `ElementReference`. Keeping a reference per mark in step with a list that is
  rebuilt on every run would be more code and more ways to be wrong; the cost is
  that focus movement is not observable from a bUnit render, so the tests assert
  the roving `tabindex` and the interop call instead of the browser's focus.
