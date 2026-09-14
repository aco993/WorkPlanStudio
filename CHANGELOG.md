# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **A work plan that is not there says so.** `/work-plans/9999` — a bookmark from
  before a delete, or a number someone typed — opened an empty editor and admitted
  the plan was gone only after Save had been pressed. The page now says which id it
  could not find and offers the way back, with no form to fill in first.
- **The plan list states the refusal before the click.** Deleting a work plan an
  order was raised from asked "this cannot be undone", took the confirmation, and
  *then* refused. The delete button is disabled with the reason instead, which is
  what the work-centre and cost-centre lists have always done.
- **"No room left" is no longer reported as "something went wrong".** The storage
  layer has always known the difference between a full browser and a broken write;
  the surface threw it away. A quota failure is now its own result status and says
  what happened and what to do: *This browser has no room left for the demo
  database, so the change was undone. Export the data or free some space, then try
  again.* The other storage failures keep the general sentence.
- **The import speaks file, not form.** A CSV row naming a plan number nobody has
  was rejected with "Select a work plan." — there is nothing to select in a file.
  It now says *No work plan has the number WP-9999.* The form's own message, which
  sits beside an actual dropdown, is unchanged.

## [0.3.3] — 2026-09-14

Four findings from driving the published site through all three personas, and
the browser suite taught about the one behaviour that deliberately changed.

### Fixed

- **An unsaved work plan is no longer thrown away in silence.** Typing in the
  editor and then following a link — or the back link, or reloading — discarded
  the edit with no word about it. The page now asks, and the question is
  answerable: *Keep editing* stays with the text still in the box, *Discard
  changes* leaves. What counts as unsaved is the form compared against what was
  loaded, so typing a change and undoing it leaves without a question, and
  **Cancel** still means "throw this away" and asks nothing.
- **A persona that cannot save cannot type either.** A Guest opening a work plan
  got thirty-six live fields, an *Add operation* button and a *Delete* button per
  row — and no Save, because there is none for them. The controls that belong to
  a policy now sit in a disabled `<fieldset>`, so they are inert, out of the tab
  order and announced as disabled, next to the notice that says which persona
  could use them. The same on the plant rules, where the two preview selects stay
  usable because they only change what is drawn.
- **The orders page asks before a one-way door, like every other page.** Deleting
  a draft order removed it on the first click, while work plans, work centers and
  cost centers have always asked. Withdrawing a released order asks too: it drops
  the routing index that made it schedulable and cannot be released again.
- The Gantt legend told the reader to hover for the reason a work center is
  closed. Every segment has carried that reason in its `aria-label` the whole
  time; the sentence was the only mouse-only thing about it.

## [0.3.2] — 2026-09-14

The three follow-ups the 0.3.1 review left open, and the two things they
uncovered on the way.

### Changed

- **The shared domain is a project, not a `<Compile Include>`.** The optional
  backend used to compile `Models/**`, `Validation/**` and four service files out
  of the browser app, because a Blazor WebAssembly project with a relinked native
  SQLite in it cannot be referenced from a server. They now live in
  `WorkPlanStudio.Domain`, which both hosts reference normally. Namespaces are
  unchanged, so no call site moved.
- **The quality gates defend what has been reached.** Coverage thresholds now sit
  about two points under what each assembly measures instead of thirteen — the
  app was gated at 65 % while measuring 79.4 % — and the new domain assembly is
  gated too, so splitting it out did not shrink the guarded surface. Test floors
  sit within about two per cent of the real count instead of a fifth of it:
  `MIN_TESTS_WEB` was 155 against 867 actual, so a suite that lost six hundred
  tests would still have been reported as a success.
- **Property tests draw 50 000 cases, not 100.** CsCheck's default hides a defect
  of the rarity of the § 5 bug released in 0.3.0: it appeared in two runs out of
  two hundred, roughly one counterexample in ten thousand draws, so a
  hundred-draw run had about a one per cent chance of seeing it. At 50 000 that
  is ~99.3 %, measured to cost the four suites that use CsCheck a few seconds
  each.
- Both READMEs advertised the thresholds from before this release and said the
  export library was "not yet wired into CI", which had not been true for a
  while. Every coverage figure in the READMEs and in `TESTING.md`/`.de.md` is now
  what CI measured, with the domain assembly among them.

### Fixed

- **The server accepted plant settings the browser refuses.**
  `PlantSettingsValidator` was the one file that could not be linked — it sat
  beside a service that writes to browser storage — so the API carried a
  hand-kept copy of its bounds, guarded by a test that read the original's
  *source text*. The copy had three rules fewer: no rotation-week range, no
  enum checks, and **no § 5 (2) rule**, so a ten-hour rest went through with no
  sector to justify it. The copy is deleted and the endpoint calls the
  validator; a test now puts that row over the wire and expects the refusal.
- The deployment asserts how many coverage badges arrive, and gating the new
  assembly made it four while the assertion still said three. The guard was
  right and the number was stale; the published site was never affected.

## [0.3.1] — 2026-09-14

A fix release. One statutory rule turned out to be wrong in a case the property
tests only found after 0.3.0 was out, and the dependency floor moved.

### Fixed

- **§ 5 rest is enforced on the ring a repeating week actually is.** A crew's
  shifts repeat weekly, so the first shift of the week is measured against the last
  shift of the week before. The pass that enforces the rest walked that ring once,
  and one walk measures a picture the walk itself invalidates: a shift the delay
  pushes past its own end is *gone*, yet its end was still handed to the shift
  behind it as "when the crew stopped working", and delaying a shift reorders the
  ring while the pass kept walking the old order. With the Sunday boundary moved to
  01:00 (§ 9 (2)), a clipped Sunday night shift left a crew **ten hours** of rest
  where § 5 asks for eleven. `EnforceRest` now sweeps until nothing moves, ignores
  the ends of shifts the delay killed, and checks the settled ring once — dropping
  whatever still starts too soon, which only ever lengthens the rest around it.
  Measured on the suite: **two failures in two hundred runs before, none in two
  hundred after**. Found by `WorkingTimePropertyTests` (CsCheck), which is also why
  a dependency pull request touching nothing but a workflow file was red.
- A component test edited the export form while the page's own first run was still
  in flight, so it asserted on settings that were never run. It now waits for the
  export trigger to be **operable** — the page's own word for "settled" — rather
  than for the button to exist.

### Changed

- Dependencies: the test stack (xunit.v3 MTP 4.0.1, code coverage 18.11.2, CsCheck
  4.9.0, **bUnit 2.11.3**), the Microsoft packages (ASP.NET Core, EF Core,
  localization) to 10.0.12, Swashbuckle's Swagger UI to 10.2.3 — development only,
  it serves the API's documentation page — and `actions/download-artifact` to v8.

## [0.3.0] — 2026-09-14

A hardening and expansion release. The theme is that several claims this project
made about itself were checked against the code that was supposed to support them,
and where the code did not, either the code or the claim changed.

### Added

- **An exact solver, and a real answer about optimality.** A disjunctive-graph
  **branch-and-bound** (`WorkPlanStudio.Scheduling.Exact`) that solves the job shop
  rather than the dispatch-order problem, sharing no code with the heuristic: its own
  placement, relaxation bounds, propagation and search. It honours the whole model —
  releases, parallel capacity, sequence-dependent change-over, calendars with phases
  and bridgeable gaps, blackouts, every due-date rule — and refuses by size rather
  than approximating. It also **emits the instance as a CPLEX LP file** so an outside
  solver can check it. A fixed twenty-instance study is committed, reproducible with
  one command and pinned by `OptimalityStudyTests`. The Scheduling page can ask for
  the proof on the plan currently on screen and reports only what was proved.
  ([ADR 0015](docs/adr/0015-exact-solver.md))
- **CSV import with a dry run that is the default.** Reads what a spreadsheet
  actually writes — UTF-8, UTF-16 and Windows-1252, comma/semicolon/tab detected by
  column-count consistency, mixed line endings — and runs the same code path twice:
  the preview writes nothing, the commit replays that exact plan and refuses if the
  database moved underneath it. Every rejected row carries line, column and reason
  and can be downloaded; the whole file lands as one atomic write or none of it.
  ([ADR 0018](docs/adr/0018-csv-import.md))
- **Export to PDF, Excel and CSV**, written by hand with **no package reference**: a
  PDF with its own object table, page tree, WinAnsi text and a vector Gantt; an xlsx
  as OOXML parts zipped in order; a CSV with formula-injection escaping and a BOM.
  Both languages get their own date rule, sheet labels and axis units.
  ([ADR 0017](docs/adr/0017-in-browser-export.md))
- **An optional ASP.NET Core backend with real accounts.** Identity, JWT access
  tokens with rotating refresh tokens stored only as SHA-256 (reuse of a spent token
  revokes the whole family), lockout, rate limiting, real EF Core migrations,
  ProblemDetails, a configured CORS origin list, a start-up that refuses a weak or
  sample signing key, and a Dockerfile. The **same policy table as the client**,
  compiled in by linked source. Off unless `Api:BaseAddress` is configured, which the
  published demo does not do. ([ADR 0020](docs/adr/0020-optional-backend-and-real-auth.md))
- **Cost centres as master data** with a code, a name, a description and referential
  integrity at the database, plus a generic `EntityPicker`, a cost-centre page and a
  cost-centre filter on the work-centre list. ([ADR 0016](docs/adr/0016-cost-centre-master-data.md))
- **A real schema migration.** Schema version 7, upgradable in place from 5 and 6,
  reading the old database through a frozen description of the old shape and writing
  rows back **with their original primary keys** — a released order has frozen
  work-centre ids into its routing snapshot, and a key that moves turns a live order
  into a missing work centre.
- **The ArbZG averaging periods, computed and shown.** § 3 sentence 2 and § 6 (2) are
  no longer honoured only as caps: the page shows a card per crew per averaged
  section with the average per Werktag, the reference period, the first breach date,
  the Werktage divisor and the compensation days owed.
  ([ADR 0024](docs/adr/0024-arbzg-in-the-ui.md))
- **The scheduling run leaves the UI thread's critical path**, sliced across browser
  turns with a real progress bar and a Cancel that leaves the previous schedule
  untouched. The yield is a `MessageChannel` task rather than a timer, because a
  background tab throttles timers and the same run stalled at 25 % for ten seconds.
  ([ADR 0019](docs/adr/0019-off-thread-scheduling.md))
- **A Gantt chart that works without a mouse**: every bar and closed stretch is a
  button with an accessible name, arrow keys walk a lane, up and down change lane, a
  roving tabindex makes the chart two tab stops rather than 137, and a text
  equivalent is always in the DOM behind a "Show as table" toggle.
  ([ADR 0023](docs/adr/0023-accessible-gantt-and-responsive-tables.md))
- **A third local-search acceptance rule, and a measurement that chose the default.**
  `BestInsertion` is 6.8 % better than steepest descent over 5 sizes × 5 instances ×
  3 budgets, and the one size where steepest descent wins every row is reported as
  measured. ([ADR 0022](docs/adr/0022-local-search-acceptance.md))
- German documentation for architecture, performance, security, the assistant and
  contributing, plus a German ADR index and the two sections `SCHEDULING.de.md` was
  missing. The ADR bodies stay English by design, and the index says so.

### Changed

- **The optimality claim.** This project said in eight places that the search "lands
  0.2 % from optimal on average and solves 19 of 20 random instances exactly — a
  tested property, not a claim", and attributed it to `OptimalityTests`. That test
  computed neither figure on no such set, and its oracle enumerated dispatch orders
  through the same dispatcher and the same evaluator the engine uses, so any bug in
  placement or scoring cancelled exactly. The measured numbers are now: **18 of 20**
  exact against the best dispatch order (mean gap 0.27 %) and **7 of 20** exact
  against the true optimum (median 5.34 %). The search was never the problem; the
  dispatch-order model is. Every document now says so, and names the study.
- **Zero allocation per scored candidate.** The search scores into a workspace it
  reuses for the whole run and materialises one `Schedule` instead of one per
  candidate. The medium benchmark went from about a gigabyte to **91.7 KB for the
  whole run** and from 281 ms to 91 ms. `PERFORMANCE.md`'s "about 0.5 MB per
  candidate" was 7.6× wrong even before that — it divided by the per-restart budget
  rather than by the candidates — and every figure in that document has been
  re-derived from the committed benchmarks.
- **A guard on every mutating service method is now a closed set**, discovered by
  reflection from the codebase's own discriminator rather than a hand-written list —
  which found one mutating method the old list had missed. `BrowserDatabase`'s reset
  and import ask the same guard.
- **One time model.** `ProductionOrder.ReleaseUtc`/`DueUtc` are `ReleaseLocal`/`DueLocal`
  and are plant-local wall clock; `PlantTime` states the contract and a source-level
  test asserts nothing in the app converts.
- **The engine refuses what it used to accept**: duplicate job or work-centre ids, a
  job with no target date, a negative release, a non-finite weight, a same-family
  change-over, and a step longer than its duration bound — which used to overflow
  into a negative penalty the search then minimised towards. `SchedulingParameterLimits`
  also bounds the **product** of the multi-start and local-search budgets at 200 000
  candidates; the two factors were bounded independently, so a form could ask for
  1.28 million.
- **Public holidays are year-aware.** `GermanHolidays` tables the law rather than the
  dates over 1990–2200: Reformationstag nationwide in 2017 only, Buß- und Bettag
  nationwide through 1994 and Saxon after, Berlin's Tag der Befreiung in 2020 and
  2025, Brandenburg's Ostersonntag and Pfingstsonntag.
- **§ 3 is capped per crew calendar day**, not per shift label; the § 5 rest is
  measured from the greatest end rather than the last start; and the 10-hour rest of
  § 5 (2) is gated on a declared sector and states the 12-hour compensating rest it
  incurs.
- **An emptied shift pattern is a closed plant, not a 24/7 machine.** `WeekCapacity`
  is a closed sum type, so the ambiguous state cannot be constructed.
- **The assistant survives a hostile response.** All three provider clients are
  null-safe against malformed bodies, bounded by a 1 MiB response ceiling and an
  output cap, and covered by one 20-second budget that includes reading the body.
  Every provider failure — not a list of seven exception types — falls back to the
  on-device answer. The Gemini model name is validated and escaped, because it is a
  path segment. The API key is stored per provider, is never read back into the
  settings dialog, and can be forgotten in one click.
  ([ADR 0025](docs/adr/0025-hostile-input-on-the-model-path.md))
- **The on-device answerer admits what it did not understand.** A recognised but
  unresolvable reference gets "I cannot find PO-9999" instead of falling through to a
  different intent; the old prefix match answered `CNC-30` with `CNC-300`'s figures.
- **The explainer ranks its recommendation by the objective**, not by tardiness alone,
  so it can no longer suggest a rule that cuts tardiness and raises the penalty.
- **Every date, number and duration goes through one culture-aware formatter.**
  `6/4/2026` is gone; currency keeps its cents and moves its symbol by culture.
- **Wide tables scroll instead of being clipped**, inside a labelled, focusable
  region.
- **Contrast tokens were measured and given margin**, including a red that was a
  3.95:1 outright AA failure in the light theme that nothing had checked.
- **The E2E suite runs its classes in parallel** — 135 s to about 75 s while growing
  from 49 tests to 76 — addresses controls by role, accessible name or label rather
  than by CSS class and position, and waits on conditions instead of sleeping.
- **axe now runs in German too**, and the dialog scan uses the same tag set as the
  page scans instead of a lower bar.
- **CI/CD**: `deploy.yml` calls `ci.yml` and `e2e.yml` instead of copying them, so
  six coverage thresholds live in one place; every job has a timeout; every action is
  pinned to a commit SHA; every checkout sets `persist-credentials: false`; the job
  that runs third-party test code no longer holds deployment credentials; the Pages
  deployment no longer cancels in progress; CodeQL no longer breaks on fork pull
  requests; and the two inline Python scripts became real files with their own unit
  tests, with the link checker now validating `#anchor` as well as the file.
- **`.editorconfig` went from 4 enforced rules to 25**, after fixing a naming rule
  that claimed every private field including `const` and `static readonly` ones and
  reported 54 violations of a convention the project does not have.
- `docs/INTERVIEW.md` is **deleted**. It was a rehearsal script that handed a reader
  the prepared answers, and it did not survive its own grep: it named an interface
  that does not exist, said "three interfaces" and "two projects" when there were
  eight and three, and contradicted itself on test counts inside one file. The
  engineering content it held has a home — limitations in the README, decisions in
  the ADRs, the WAL story in ADR 0006.

### Fixed

- **Browser storage is atomic.** One JSON value under one key, read back before
  success is reported; a quota failure is a typed persistence failure rather than a
  silent loss; the WAL checkpoint runs only when the journal really is WAL and a busy
  checkpoint fails the snapshot instead of being discarded; the schema probe touches
  all eight `DbSet`s instead of one of six; and concurrent persists are serialised.
- **One commit path for every mutation**, capturing the pre-image and restoring it
  when either the write or the snapshot fails.
- **Money survives the round trip.** `decimal(10,2)` columns had NUMERIC affinity and
  stored prices as doubles; they are `TEXT` with `CAST(... AS REAL)` inside every
  range check.
- **Deleting a work plan an order came from** returns a typed conflict instead of
  throwing an unhandled `DbUpdateException` at the error boundary, and a work centre a
  **released order** still needs can no longer be retired by putting its plan back to
  Draft.
- **Validation issues can no longer have nowhere to render.** A page declares the
  fields it draws an error slot for, and anything else lands in a summary that takes
  focus — five previously silent failures now render, tied to their control.
- **Numeric fields no longer keep their old value silently** when cleared; an empty
  box parses to null and becomes a visible "required".
- **A missing visual baseline fails** instead of being written and passing green, and
  the committed file names must be exactly the screen matrix, so a renamed route
  cannot silently lose its guard. Windows baselines, which nothing ever compared, are
  removed. ([ADR 0021](docs/adr/0021-visual-baselines-linux-only.md))
- **The Gantt range is clamped to the timeline**, so a makespan reaching past the
  400-day lookahead no longer throws.
- **A `role="img"` on the weekly preview** made assistive technology discard all seven
  days and announce only the widget's own name; the strip is now hidden and a table
  beside it lists each day's segments in words.
- **The chat's first answer was never announced** — the live region was created
  together with its first two turns; it is now rendered before the first turn.
- **Dialog focus returns to the control that opened it on every close path**,
  including Cancel and Save, which never reached the close handler.
- An operation longer than any open window of its work centre, or longer than the
  engine's duration bound, is refused with its own sentence rather than a generic
  banner or an exception.

### Security

- The tracked SQLite advisory **GHSA-2m69-gcr7-jv3q / CVE-2025-6965 no longer applies**:
  EF Core 10.0.11 brings `SQLitePCLRaw` 2.1.12, and `dotnet list package --vulnerable
  --include-transitive` against that graph with **no suppression at all** reports
  nothing. The `NuGetAuditSuppress` line is removed: a restore with the audit fully
  strict and nothing suppressed is clean, so **SEC-001 is closed** on all three of its
  conditions. See [docs/SECURITY.md](docs/SECURITY.md).
- A root `SECURITY.md` where GitHub looks for it, a `CODEOWNERS` file, and an issue
  template routing a security report to a private advisory.

### Measured

- **1 649 unit and integration tests** (294 scheduling / 254 working time / 865 app /
  88 backend / 148 export) and **76 browser tests**, of which ten are the Linux-only
  pixel comparisons. Up from 452 at 0.2.0.
- **Coverage**: engine 96.57 % lines / 90.45 % branches, working time 94.31 % /
  90.29 %, app 79.89 % / 71.28 %, backend 70.19 % / 65.84 %, export 98.45 % /
  90.34 %. The export suite is measured but not yet wired into CI.
- **Lighthouse** on the published site, desktop preset: performance 36, accessibility
  100, best practices 100, SEO 100, on all three measured routes.
- Numbers, the machines they were measured on and the commands that reproduce them
  are in [docs/PERFORMANCE.md](docs/PERFORMANCE.md) and
  [docs/TESTING.md](docs/TESTING.md).

### Known limitations introduced or confirmed by this release

- The heuristic is a median **5.3 %** from the true optimum, and gap back-filling —
  the thing that would close it — is a redesign rather than a tuning exercise.
- The exact solver has a cliff at about twenty-one operations, and size is not the
  predictor.
- Proving optimality freezes the tab for up to its two-second budget.
- No external MILP solver was run against the emitted LP files.
- Connected-mode master-data editing is not wired into the pages; the pull is one-way.
- Mutation testing remains blocked upstream (Stryker does not support the Microsoft
  Testing Platform), and no mutation score is claimed.

## [0.2.0] — 2026-09-08

### Added

- **Working time as capacity** — a second pure library, `WorkPlanStudio.WorkingTime`:
  shift patterns (continuous, one, two, three shifts), every rule of the German
  Working Hours Act the app applies as a parameter with its legal reference
  (§3 daily cap, §4 breaks, §5 rest per crew across the week wrap, §6 night
  work, §9 Sunday/holiday closure with the boundary shift, §11 free Sundays),
  public holidays computed for all 16 federal states, absences per work center,
  and a timeline that projects onto the engine's calendars. A **Working-time
  page** with a live preview of the week, the rule applications, the holiday
  table and the absences. The Gantt chart shades closed time with its reason and
  shows real dates. ([ADR 0012](docs/adr/0012-working-time-as-capacity.md))
- **Engine calendars** — a phase for calendars that do not start at the horizon,
  tagged blackouts that are never bridged, and a bridgeable gap so an operation
  can pause across a break; utilisation now divides by open time.
- **Personas** — Planner, Supervisor and Guest through the real ASP.NET Core
  authorization pipeline: policies, `AuthorizeView`, a persona menu, read-only
  notices, and an `IPermissionGuard` on every mutating service that returns
  `Forbidden`. ([ADR 0013](docs/adr/0013-personas-through-the-real-authorization-pipeline.md))
- **Schedule chat** — a conversation over the current run answered on the
  device in English and German (bottleneck, late orders and why, one order or
  work center, idle time by reason, the rules in force, a summary) and
  what-ifs that re-run the scheduler. Optional models behind one seam:
  OpenAI-compatible, Anthropic Messages API and Google Gemini, with the key in
  the header each expects, and fallback to the on-device answer on any failure.
  ([ADR 0014](docs/adr/0014-schedule-chat-on-device-first-with-pluggable-models.md))
- **Dark theme** (system, light, dark) applied before Blazor boots, a skip link,
  focus traps in dialogs, `prefers-reduced-motion`, a branded boot screen and a
  dashboard with plant and schedule at a glance.
- **Tests of every kind** — axe-core WCAG 2.2 AA scans of every page in both
  themes and in a dialog, visual regression against per-OS pixel baselines,
  performance budgets, a BenchmarkDotNet project, working-time property tests,
  provider clients over a stubbed transport, bUnit for the chat panel,
  Playwright for the chat in both languages. 452 tests in four projects.
- **CI** — per-assembly coverage gates with badges served from the site, the
  browser suite gating the deploy, a performance workflow (benchmarks and
  Lighthouse CI on the published site), `robots.txt`.

### Changed

- The optional AI narrator now speaks through the same provider seam as the chat
  (`AiScheduleNarrator`); the settings dialog offers a provider select that
  fills endpoint and model. The provider budget is 20 seconds.
- Five accessibility defects found by the first axe scan were fixed: pill and
  Gantt-label contrast, an unfocusable scroll region, a skipped heading level
  and a duplicate landmark.
- The database schema version is 5 (plant settings, shift patterns, absences);
  stored databases from earlier versions enter the export/reset flow.
- GitHub Actions, bUnit and the coverage extension bumped to current versions.

### Removed

- Interview cheat-sheets and hardening notes in a third language; the
  English `docs/INTERVIEW.md` remained until 0.3.0, which deleted it.

### Also in this release (landed on `main` between 0.1.0 and 0.2.0)

- **Production orders** that own an immutable routing snapshot, released with a
  real customer due date; `DueDateRule.Explicit` is the default.
  ([ADR 0011](docs/adr/0011-production-orders-own-routing-snapshots.md))
- **Repeating availability calendars and sequence-dependent change-over** by
  operation family in the engine. ([ADR 0010](docs/adr/0010-periodic-calendars-and-setup-families.md))
- **Insertion-neighbourhood local search** and an **exact dispatch-order
  optimizer** for small instances, with optimality tests against brute-force
  enumeration of all `n!` dispatch orders (0.2 % mean gap to the best dispatch
  order, 19 of 20 instances matched). *Corrected in 0.3.0: that reference is the
  search's own ceiling, not the optimum of the scheduling problem — see
  [ADR 0015](docs/adr/0015-exact-solver.md).*
  ([ADR 0008](docs/adr/0008-insertion-neighbourhood.md))
- **Dispatch-rule equivalences reported** instead of hidden.
  ([ADR 0009](docs/adr/0009-report-rule-equivalences.md))
- **Schedule assistant** — a deterministic, on-device explanation of each run
  (bottleneck, why each job is late, one *computed* recommendation) as localized
  EN/DE narration, plus an optional bring-your-own-key narrator behind a
  provider abstraction with fallback. ([ADR 0005](docs/adr/0005-explainable-scheduling-and-optional-ai.md))
- **Property-based tests** (CsCheck) for the engine: precedence, capacity,
  determinism, a makespan lower bound and "never worse than the pure rule".
- Central business validation, SQLite constraints, typed mutation results and
  localized recovery/error states; explicit browser-storage recovery with
  corrupt-payload export, confirmed reset, schema-mismatch protection and
  WAL-safe snapshots. ([ADR 0006](docs/adr/0006-explicit-browser-storage-recovery.md))
- Structured all-or-nothing schedule preparation diagnostics, work-center
  parallel capacity, deterministic scheduling budgets, checked time arithmetic,
  cooperative cancellation and a reproducible performance scenario runner.
- ErrorBoundary recovery, modal dialog/focus/Escape semantics and dynamic
  document language.
- CodeQL (security-extended) and a Quality workflow (formatting, documentation
  links, resource files); one transitive SQLite advisory suppressed with a
  documented justification in `Directory.Build.props`.

## [0.1.0] — 2026-07-08

Initial public release.

### Added

- **Work plans / routings** — create, edit, search and filter by status
  (Draft / Released / Archived), with an operations editor that recalculates
  total time and estimated cost live.
- **Work centers** — master data with hourly rates and cost centers, plus a
  delete guard for centers still referenced by operations.
- **Dashboard** — key figures, a status distribution and recently updated plans.
- **Production scheduling** — a pure, dependency-free finite-capacity engine:
  four due-date rules, six dispatch rules, seeded multi-start plus local-search
  optimisation, a deterministic PRNG, a Gantt chart and tardiness KPIs.
- **Real in-browser database** — EF Core + SQLite compiled to WebAssembly and
  persisted to `localStorage`, with a schema-version guard.
- **Bilingual UI (EN / DE)** — runtime-switchable via `IStringLocalizer`/`.resx`.
- **Four test layers** — engine unit + architecture tests, EF→domain mapper
  tests, bUnit component tests and Playwright end-to-end scenarios.
- **CI/CD** — per-layer test workflows on pull requests and a test-gated
  GitHub Pages deployment.

[Unreleased]: https://github.com/aco993/WorkPlanStudio/compare/v0.3.3...HEAD
[0.3.3]: https://github.com/aco993/WorkPlanStudio/compare/v0.3.2...v0.3.3
[0.3.2]: https://github.com/aco993/WorkPlanStudio/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/aco993/WorkPlanStudio/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/aco993/WorkPlanStudio/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/aco993/WorkPlanStudio/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/aco993/WorkPlanStudio/releases/tag/v0.1.0
