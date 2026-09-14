# 18. A CSV import that refuses to write before you have read the preview

- **Status:** Accepted
- **Date:** 2026-09-11
- **Related:** [ADR 0006](0006-explicit-browser-storage-recovery.md) (the storage budget and the atomic
  single-key write this leans on), [ADR 0011](0011-production-orders-own-routing-snapshots.md) (the
  snapshot an import is not allowed to touch), [ADR 0016](0016-cost-centre-master-data.md) (cost
  centres as entities, which is what makes a work-centre sheet need a lookup)

## Context

Until now the only data in this application was the data it shipped with. A visitor could edit it row
by row, and a planner with a real routing list in a spreadsheet had no way in at all — which makes the
whole thing a demo of scheduling rather than something a shop could put its own work through.

The constraints are unusual and they shape every decision below:

- The entire database is one Base64 value in `localStorage`. That is about 5 MB per origin, so roughly
  3.3 MB of SQLite, for every table together. An import is not "add some rows", it is "spend part of a
  fixed budget".
- There is no server. The parse, the validation and the write all happen on the visitor's main thread,
  in WebAssembly, while the tab has to stay responsive.
- The data has invariants that took real work to establish: a released order's routing snapshot is
  frozen, a work centre a live order depends on cannot be retired, a cost centre is referenced by key
  and not by spelling. A bulk path that quietly skipped them would be a second, weaker application
  sharing one database.
- A CSV carries none of its own metadata. Not its encoding, not its separator, not its number format,
  not its date convention.

## Decision

### The dry run is the default, and it is the same code as the commit

`PlanAsync` parses the file, puts every row through the validators the forms use, resolves it against
the database and returns an `ImportPlan`: how many rows would be added, changed and skipped, every
rejected row with its line, column and reason, and a warning for anything that would alter data that
is already there. It writes nothing. `CommitAsync` takes that plan and applies it.

The two are not two implementations. One method, `ApplyAsync`, does the resolving, and a `write` flag
decides only whether the entities are attached to the context; every decision, every validator call and
every count is the same code either way. A preview computed by separate code is a second implementation
of the import that nothing keeps in step, and the first time the two disagree the user has already
pressed the button.

The commit then compares its own counts with the plan's and refuses, without writing, if they differ —
another tab, or the form on the next screen, may have moved the data while the preview was on screen.

**The cost.** The file is parsed twice (once for the header, once for the dry run) and the rows are
resolved twice (dry run and commit). For a file that fits this application's storage budget that is
tens of milliseconds; it would not be acceptable against a real database, where the preview would have
to be a transaction held open or an optimistic token rather than a re-read.

### One write, not one write per row

The four master-data services each commit and snapshot per call, which is right for a form and wrong
for a file: a thousand rows would be a thousand snapshots of a growing database and, on the row that
fails, five hundred half-imported ones. The import stages everything into one `AppDbContext` and goes
through `DatabaseMutation.RunAsync` once, so it is one `SaveChanges` and one durable write, with the
pre-image restored when either half fails. Measured on 10 000 rows: one call to browser storage.

This is the one place the import does *not* go through the per-entity services. It goes through their
validators, their guards and their commit path — but not their `SaveAsync`, because "save one thing"
is the wrong unit of work for a file.

### Updating is opt-in, and every update is a warning first

By default an existing key is **skipped** and counted. With "update existing rows" ticked it is
overwritten, and every such row appears as a warning in the preview before anything happens. Nothing in
this application changes data that is already there because a file happened to mention it.

The same reasoning gives "create cost centres the file names but the app does not have" its own
opt-in: a work-centre sheet pointing at `CC-8888` is a typo far more often than it is an instruction to
create master data Controlling owns.

### What the format cannot express, and what happens instead

- **Releasing an order.** Imported orders are created as drafts. Release freezes a routing snapshot
  that the shop floor then works to, and a spreadsheet cell cannot carry the judgement that act
  represents. An existing order that is not a draft is rejected outright, with the same message the
  form gives (`Val_OrderNotDraft`), so the snapshot is never touched.
- **Restructuring a routing orders were raised from.** Allowed — the editor allows it too — but never
  quietly: the preview carries the count of affected orders before the button is pressed.
- **Retiring a work centre a released order needs.** Rejected, by the same two questions the
  work-centre form asks.
- **Anything relational other than by business key.** A file names a cost centre by code and a routing
  by plan number. It cannot express a key, an ordering across sheets, or a deletion — this import adds
  and updates, and never removes.

### The guesses are made explicitly and shown

Encoding and separator are detected (UTF-8 with and without a mark, UTF-16 either way, Windows-1252;
`,`, `;` and tab), reported on screen with the row count, and overridable. Windows-1252 is decoded by a
32-entry table in this repository rather than by taking a dependency on
`System.Text.Encoding.CodePages`, which exists to carry the full NLS tables and would enlarge a payload
every visitor downloads.

Numbers and dates are **not** parsed with `CurrentCulture`. The same file must import the same way for
everyone, and the person who exported it is rarely the person importing it. The rules are fixed and
written down:

- A single separator in a decimal is always the decimal point: `1,5` and `1.5` are both one and a half.
  Thousands separators are recognised only when both kinds appear (`1.234,56`) or one repeats
  (`1.234.567`). So `1,234` is 1.234 and not 1234 — resolving it the other way would silently multiply
  an hourly rate by a thousand, and that is the worse failure.
- A whole number has no fractional part, so there the rule reverses: a separator is grouping, and
  `1.23` is refused rather than rounded.
- Exactly two date shapes are read, `31.12.2026` and `2026-12-31`, each optionally with a time.
  `01/02/2026` is refused by name, because it is two different dates in two conventions and a due date
  six months wrong is worse than a due date missing.

### No generic import library

`CsvHelper` would have given the parser for nothing. It was not taken:

- Everything that is actually hard here is the half a library does not do — the dry run, the
  entity-specific resolution, the invariants, the storage budget, the bilingual messages keyed to a
  column, the grouping of a work plan across rows.
- Its convention-based mapping is the opposite of what this needs. The mapping must be *shown* and
  *corrected*, because a header is written by a person and `Stückzeit` is not a property name.
- It is a WebAssembly payload every visitor downloads, for a parser that is 145 lines of code here, and
  it would take a per-row exception model into a page that must never abandon a file half-read.

The judgement is the usual one: take the library when the library is the problem. Here the parser was
the easy part.

## Consequences

- ✅ Nothing is written before the visitor has seen, by line number, what would change. The preview
  and the commit cannot drift, because they are one code path.
- ✅ An import either applies fully or not at all, and a failed durable write puts the database file
  back. Forced in a test rather than argued: the storage layer is made to refuse mid-import and the
  database is asserted unchanged.
- ✅ Every rule the forms enforce is enforced here, because the same validators and the same permission
  guard are called. A guest cannot import; a supervisor can import orders and not master data, exactly
  as on the pages.
- ✅ A rejected row is actionable: line, column, reason, and a downloadable CSV of all of them to fix
  in the spreadsheet. That report is also the one file this feature writes, so a cell beginning `=`,
  `+`, `-` or `@` is prefixed with an apostrophe — the import evaluates nothing, but a report that
  handed the formula back live would have made the round trip the attack.
- ➖ **The storage check is an estimate.** The real figure is only known once SQLite has written its
  pages, which is after the point of no return, so the pre-check multiplies the file's text by a
  deliberately pessimistic factor. Measured on 10 000 cost centres: it estimated 1 804 962 characters
  against a real stored payload of 1 141 420 — over by 58 %, which is the safe direction. An estimate
  that under-read would let an import start that cannot finish.
- ➖ **The file is held in memory as bytes.** The parse itself streams — the decoder drives a state
  machine a buffer at a time and exactly one record exists as strings at any moment — but the header
  inspection and the dry run must read the identical file, so the bytes are kept. The 8 MB cap is what
  bounds that, and it is stated on screen with its reason.
- ➖ **A half-finished import does not exist, but a half-*fixed* file does.** Rejected rows are not
  imported and there is no partial mode; the visitor fixes them in the spreadsheet and imports again,
  which will skip everything that landed the first time. That is only comfortable because updating is
  opt-in and the skip count is shown.
- ➖ **The mapping is remembered per browser and per entity kind**, keyed by header name rather than
  column position. That survives a column being inserted, and it does not survive a column being
  *renamed* — which then falls back to the guess, silently. The mapping screen is always shown, so it
  is visible rather than hidden, but it is a place a careless visitor can go wrong.
- ➖ One more screen to keep bilingual, accessible and consistent, and a fifth entry in the navigation.
