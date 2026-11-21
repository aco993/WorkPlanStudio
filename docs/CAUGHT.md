# Caught

**English** · [Deutsch](CAUGHT.de.md)

This project was built with AI assistance, and the
[disclosure in the README](../README.md#ai-assisted-development-disclosure) says so.
What matters about that is not how much was generated but what happened when the
generated thing — or the author — was wrong. This page is the record: each entry is a
real defect or a wrong claim, what it would have cost, **what caught it**, and where
the fix and its evidence are. Nothing here was found by reading a diff and nodding.

The pattern is the point. Almost every catch came from an instrument built to prove
the opposite: a browser check written to show a change was safe, a measurement taken
to confirm a number, a CI floor meant to notice a suite shrinking.

## Caught by a check that was meant to prove it worked

| What was wrong | What it would have cost | Caught by | Where |
| --- | --- | --- | --- |
| The deploy's script-hash step hashed a **comment** that quoted `<script type="importmap">` together with the real import map | the browser refuses the map; the published site never starts | the in-browser verification that runs after the pin | [ADR 0031](adr/0031-pin-inline-scripts-by-hash.md), [#110](https://github.com/aco993/WorkPlanStudio/pull/110) |
| Lazy-loading `System.Private.Xml` (436 KB, never executed) | the app cannot open its database: `SqliteConnection`'s vtable needs `DataTable`, which needs XML | a browser test written to prove the assembly is never requested | [PERFORMANCE.md](PERFORMANCE.md#four-levers-measured-none-shipped) |
| The service worker cached **nothing** in production while caching everything locally | the "second visit is free" claim false on the only site anyone sees | checking the published site, not the build | [ADR 0027](adr/0027-pay-the-cold-start-once.md), [#85](https://github.com/aco993/WorkPlanStudio/pull/85) |
| The performance-budget gate passed when every budget test was **skipped** | a tripwire that can never fire | reading the run that should have exercised it | [#94](https://github.com/aco993/WorkPlanStudio/pull/94) |

## Caught by CI, not by a person

| What was wrong | What it would have cost | Caught by | Where |
| --- | --- | --- | --- |
| Splitting the schedule page ([#97](https://github.com/aco993/WorkPlanStudio/pull/97)) moved the chat reset *after* the run: for one await the suggestions were clickable against the old context | a question asked as a run lands is silently wiped | the chat tests failing on three unrelated pull requests in one night | [#111](https://github.com/aco993/WorkPlanStudio/pull/111) |
| Dependabot grouped `Microsoft.AspNetCore.Authorization` **11.0.0** (a .NET 11 package) with ten 10.0.x patches | the API fails at start-up | all 76 hosted API tests red on the dependency PR | [#112](https://github.com/aco993/WorkPlanStudio/pull/112) |
| The persona switch was named "Working as" while it showed "Planner" — WCAG 2.5.3, level A, on every route | voice-control users cannot name the control | a newer axe in a Dependabot PR failing 38 of 76 browser tests | [#83](https://github.com/aco993/WorkPlanStudio/pull/83) |
| The fix for NVDA hearing "Working asPlanner" was an `aria-label` — which no longer contained the visible text (the avatar's initial joined to the role) | WCAG 2.5.3 broken again, on every route, by the fix for a screen reader | axe's `label-content-name-mismatch` in the browser suite, 38 scans red | [#119](https://github.com/aco993/WorkPlanStudio/pull/119) |

## Caught by measuring instead of assuming

| What was wrong | What it would have cost | Caught by | Where |
| --- | --- | --- | --- |
| The app's only layout shift. **First guess wrong:** it was not the late Save button but `EditableArea` rendering nothing while the persona was read | 0.05 CLS and a 6.1 s LCP on `/working-time` | a second Lighthouse run that did not move, then a frame-by-frame layout trace | [ADR 0030](adr/0030-render-the-first-paint-at-build-time.md), [#105](https://github.com/aco993/WorkPlanStudio/pull/105) |
| A proof verdict outlived the schedule it judged | "Proved optimal" shown beside a plan nobody had proved anything about | a test written while extracting the component, failing on the old page | [#97](https://github.com/aco993/WorkPlanStudio/pull/97) |
| The engine's headline result was measured against the **wrong reference** (best dispatch order, not the optimum) | a claim about optimality that the code did not support | an independent exact solver written to check it | [ADR 0015](adr/0015-exact-solver.md) |
| Only 43.8 % of the dashboard's text was 12 px or larger on a phone | unreadable labels for most people who open a portfolio link | the first mobile Lighthouse run | [#113](https://github.com/aco993/WorkPlanStudio/pull/113) |
| 51 unnamed images, "Edit, Edit, Edit", a spoken "plus" — none of it an axe violation | noise for anyone using a screen reader | reading the accessibility tree route by route | [#117](https://github.com/aco993/WorkPlanStudio/pull/117) |
| What NVDA said, which the tree did not show: every download percentage announced, "Working asPlanner", every icon button read twice | a screen-reader user hears noise for the whole first visit | NVDA 2026.3 itself, its debug log recording every utterance | [#119](https://github.com/aco993/WorkPlanStudio/pull/119) |
| Turning off time-zone data saved 75 KB | export timestamps and "today" in UTC: yesterday's date near midnight | thinking through what reads `TimeZoneInfo.Local` before shipping | [PERFORMANCE.md](PERFORMANCE.md#four-levers-measured-none-shipped) |

## Caught in the author's own instruments

The tools that catch defects have defects too, and those are recorded the same way.

| What was wrong | Caught by |
| --- | --- |
| The target-size test measured a 13 px checkbox instead of the label that receives the click, reporting five defects that did not exist | comparing its output with the page |
| A tab-order audit stopped at the first repeated name ("Edit") and reported five stops where there were twenty-six | the counts being implausibly low |
| The documentation's test counts had drifted (867 claimed, 986 run) and the CI floors were up to 8 % below the truth | running every suite before writing a number ([#102](https://github.com/aco993/WorkPlanStudio/pull/102)) |
| A new Gantt test clicked the zoom while the page's first run was still finishing; now and then the click was lost | main going red on the merge that added it, after passing on the pull request ([#122](https://github.com/aco993/WorkPlanStudio/pull/122)) |

## How to read this

Every entry names its evidence: a pull request, an ADR, or a measurement in the docs,
each with the test or run that shows it. The project's rule is that no number is stated
that was not measured ([AGENTS.md](../AGENTS.md), golden rule 8); this page holds the
same standard for mistakes. When the next one is found, it goes here.
