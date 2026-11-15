# 30. Render the first paint at build time, and hand it over to the live app

- **Status:** Accepted
- **Date:** 2026-11-16
- **Relates to:** [ADR 0027](0027-pay-the-cold-start-once.md), which made the
  second visit free and gated the cold-start bytes; [ADR 0019](0019-off-thread-scheduling.md),
  which explains why the runtime cannot be made smaller by moving work off the thread

## Context

Lighthouse, desktop preset, in the Performance workflow on `main` at `0f77493`
(run 43765145877, two runs per page):

| Page | Performance | FCP | LCP | TBT | Speed Index | CLS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `/` | 35–37 | 0.3 s | 7.8–7.9 s | 3.3–3.6 s | 3.6–5.8 s | 0 |
| `/schedule` | 35–36 | 0.3 s | 7.8 s | 3.6–3.7 s | 5.1–5.3 s | 0.001 |
| `/working-time` | 35 | 0.3 s | 7.8 s | 3.4 s | 6.3 s | 0.05 |

Everything a reader came for — the KPIs, the chart — paints after the .NET
runtime has downloaded and started, SQLite has opened and the demo plant has been
seeded. The score was published, explained and left alone, because every lever
inside the runtime had been measured and found wanting
([PERFORMANCE.md](../PERFORMANCE.md#four-levers-measured-none-shipped)).

The levers outside it had not been tried. The usual answer, server prerendering,
needs a server, and GitHub Pages is static. But a first visit always starts from
the same demo plant: what it will see is known when the site is built.

## Decision

Render it when the site is built, show that rendering at once, and hand over to the
live application when it is ready.

**At build time** `tools/WorkPlanStudio.Prerender` runs after `dotnet publish`, in
both the deploy and the Performance workflows. It serves the published folder,
renders the dashboard, the schedule and working time in Chromium, in English and
in German, waits until nothing is loading, and writes each page into `index.html`
as a `<template>` — ids and every attribute that points at one removed, since the
live page is about to carry the same ones. Six pictures, 38 KB gzipped on the wire.

**In the browser** an inline script (`first-paint.js`), running before the first
paint, picks the picture for this route and language, shows it, and hides the boot
screen. When the live page has replaced the boot screen *and* has nothing left to
load, it removes the picture and shows the page in one step — so the reader never
sees picture, loader, page.

Three rules keep the picture honest:

- **Only a first visit sees it.** A browser that already holds a database holds its
  own data; a picture of the demo plant would be a picture of somebody else's
  numbers. The script checks storage and steps aside.
- **It is inert.** A picture of a button must not look as if it took the click. A
  status line outside the inert part says the application is starting and carries
  the download percentage.
- **It cannot hide a failure.** If the live page never settles, it is shown after
  30 seconds in whatever state it is in.

**The tool verifies its own output** in the same run and fails the job otherwise:
at `DOMContentLoaded` the picture is up and the boot screen is not; later the
picture is gone, the templates are gone and the live page is visible; there are no
console errors; the German picture is German; a returning visitor never sees it.

## Consequences

Same workflow, same runner type, after (run 43775424282, two runs per page):

| Page | Performance | FCP | LCP | TBT | Speed Index | CLS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `/` | **70** | 0.3 s | **0.3–0.5 s** | 3.0–3.2 s | **0.3 s** | 0 |
| `/schedule` | **70** | 0.3 s | **0.3–0.5 s** | 2.9–3.1 s | **0.3 s** | 0 |
| `/working-time` | **70** | 0.3 s | **0.3–0.5 s** | 2.7–2.8 s | **0.3 s** | **0** |

- **It found a real defect.** The first measurement left `/working-time` at 46: its
  0.05 layout shift (present before this change too) moved a paragraph after the
  hand-over, which Lighthouse then counted as a 6.1 s LCP. A layout trace at the
  Lighthouse viewport put the cause in `EditableArea`, which rendered *nothing*
  while the persona was being read, so the whole rules form arrived a frame late.
  It now renders the same controls disabled for that moment. The app has no layout
  shift left on any measured page.
- **Total blocking time is untouched**, and is now nearly all of what the score
  loses: it is the WebAssembly module compiling and the runtime starting, on the
  one thread ([ADR 0019](0019-off-thread-scheduling.md)). The picture does not make
  the app interactive sooner; it makes the wait look like the page instead of a
  spinner. The status line says so.
- **Cold start +34 KB** by the transfer budget (7,508 → 7,542 KB), inside the
  existing 7,650 KB budget.
- **The picture can be older than the data it shows by one deploy.** It is the demo
  plant as of the build; the live page replaces it seconds later with the same
  plant computed in the browser. Text that depends on today's date can differ for
  that moment; the layout does not.
- **The browser suite does not exercise it**: E2E runs against `dotnet run`, which
  has no prerender step. The tool's own verification is the test, and it runs in
  every deploy.
- Routes outside the three keep the boot screen. Adding one is an argument to the
  tool and about 15–60 KB raw per language.
