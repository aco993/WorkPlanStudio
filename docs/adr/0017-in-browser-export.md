# 17. Write the CSV, the workbook and the PDF by hand, in the browser

- **Status:** Accepted
- **Date:** 2026-09-11
- **Related:** [ADR 0001](0001-pure-scheduling-library.md), whose "pure, dependency-free library"
  shape this copies; [ADR 0019](0019-off-thread-scheduling.md), which establishes that this
  application has one thread and no backend to move work to;
  [ADR 0023](0023-accessible-gantt-and-responsive-tables.md), whose accessible-chart rules the
  export control and the printed chart follow

## Context

A planner who has produced a schedule needs to leave the browser with it: send it to the works
manager, take it into a meeting, put the numbers next to last month's in a spreadsheet. Until now the
only way out of the application was a screenshot.

Three formats, because they answer three different questions. A **PDF** is the one that gets printed
and pinned up — it has to carry the chart. A **workbook** is the one that gets re-sorted and summed,
so its cells have to stay typed. A **CSV** is the one that gets fed to whatever the recipient's
system is, and it has to be boring.

The constraint that decides everything else: this is a Blazor WebAssembly application on a static
host. There is no server to render on, and every managed dependency is downloaded by every visitor
before the app starts.

## The options

**QuestPDF / SkiaSharp.** The obvious PDF library, and the reason it is not here is native code.
SkiaSharp for `browser-wasm` has to be relinked into the module, which needs the `wasm-tools`
workload and puts a second native artefact beside the SQLite one the app already relinks. That is
not a size question, it is a risk question: ADR 0019 records that the SQLite native blob alone
already closes off browser threading, because a prebuilt object compiled without `atomics` refuses
to link against shared memory. A second such blob doubles the surface on which the next toolchain
change can go wrong, for a feature that is a few hundred lines of drawing.

**ClosedXML / EPPlus.** ClosedXML pulls OpenXML SDK and a sizeable managed tree for a feature that
needs perhaps a twentieth of it. EPPlus is commercially licensed for non-personal use, which a
public portfolio cannot accept without qualification.

**A server-side renderer.** There is an optional API (ADR 0020), but it is *optional*: the default
deployment is GitHub Pages with no backend at all. An export that only worked in connected mode
would be a feature that disappears in the configuration most people see.

**Writing the three formats by hand.** Bounded feature set, no dependency, testable without a
browser, works offline.

## Decision

**A fourth pure library, `WorkPlanStudio.Export`, with no package references at all.** Same shape as
the scheduling and working-time libraries: plain input records in, `byte[]` out, no Blazor, no EF, no
JS interop, no reference to the app. Everything is a function, so everything is testable by reading
the bytes back.

Measured cost in the published payload: `WorkPlanStudio.Export` is **101 141 bytes uncompressed,
33 665 bytes Brotli**. That is what the export feature costs a first-time visitor.

**One input model, three renderings.** An `ExportReport` — metadata, headline figures, tables of typed
`ExportCell`s, an optional Gantt model — is built once by `ScheduleExportBuilder` and handed to
whichever writer was asked for. The workbook puts each table on its own sheet, the CSV concatenates
them under section headings, the PDF paginates them and draws the chart. None of the writers knows
where the data came from, and the projection is tested against a hand-built schedule with no writer
involved.

**Cells keep their type.** `ExportCell` distinguishes empty, text, number, date and boolean. This is
the whole reason a workbook is worth more than a CSV: a date written as a serial number with a date
format sorts chronologically and a number sums, where text does neither. It costs a serial-number
epoch of 1899-12-30, which reproduces Excel's belief that 1900 was a leap year — deliberately, because
matching Excel is the point.

**Text that a spreadsheet would evaluate is neutralised.** A part description of `=cmd|'/c calc'!A1`
is executable content the moment the file is opened (CWE-1236), and the population that would be
targeted is exactly this one: the planner exports, the works manager opens. `FormulaGuard` prefixes a
leading `=`, `+`, `-`, `@`, tab or carriage return with an apostrophe, which every spreadsheet reads
as "the rest is literal" and does not display. Column headings go through it too — a column called
`-Menge` is a formula to a spreadsheet. Numbers do not: they are written as numbers and never
re-parsed, and an apostrophe in front of one would turn it into text on import.

**The PDF uses only the standard Helvetica faces, and therefore ships its own metrics.** A PDF that
names a standard Type 1 font embeds no font programme, which is what keeps the writer free of a font
parser, a subsetter and any native code. The price is that nothing else knows how wide a string is,
so `StandardFonts` carries Adobe's published AFM advance widths for Helvetica and Helvetica-Bold,
indexed by WinAnsi code. Without that table there is no truncation, no column alignment and no
centring. Text is encoded as WinAnsi (code page 1252), which covers every character the application
produces — umlauts, the sharp s, the euro sign, the en and em dashes, the middle dot the UI uses as a
separator — and a character outside it is replaced visibly rather than dropped silently.

**Content streams are left uncompressed.** Flate would shrink the file; the documents here are tens
of kilobytes, and an uncompressed stream is one a reviewer and a test can read.

**The cross-reference table is recorded, never computed.** A PDF is a graph of objects addressed by
byte offset and a reader trusts those offsets absolutely; one byte out produces a file that opens in
one viewer and fails in the next, with no diagnostic. The offsets are taken from the output stream's
own position as the bytes are written, and a test parses the table back out of the finished file and
asserts that every entry lands exactly on the object header it claims.

**One language per document, and no date a reader could parse two ways.** Both shipped languages
render a purely numeric short date and they disagree about it: `11/09/2026` is 11 September to a
British reader and 9 November to an American one. The screen already refuses that (see
`Services/Format.cs`); an export needs it more, because it is printed, mailed and read against a paper
travelling card by someone who did not produce it. `ExportDates` therefore spells the month, keeps
the 24-hour clock in both languages, and puts the weekday in front of a compact axis label.
The culture is a parameter of the report, never the ambient one, so an export cannot depend on which
thread produced it. The few words the writers supply themselves — the PDF page number, the workbook's
summary sheet and its column headings, the CSV's boolean literals — are passed in from the resource
file for the same reason: a German document with an English sheet tab is not a German document.

This is a fix, not a preference. The first working version rendered its axis labels as `1.6. 06:00`
and its header stamp as `11/09/2026` inside an otherwise English report — one German, one ambiguous.

**The bytes reach the browser as a stream, not as base64.** `FileDownloadService` hands a
`DotNetStreamReference` to `download.js`, which calls `arrayBuffer()` on it. Base64 through interop
inflates the payload by a third and marshals it twice, once encoding in .NET and once decoding in
JavaScript, which for a multi-megabyte workbook is a visible pause on the one thread there is. The
object URL is revoked after the click — in a later task, because Safari dispatches the download
asynchronously and revoking in the same turn cancels it — since a blob URL pins its blob for the life
of the document.

**The control is a menu button, and it is operable without a mouse.** Three formats in a page header
is noise and only one is wanted at a time. It is a real `button` with `aria-haspopup="menu"` and
`aria-expanded`, over a `role="menu"` panel of `role="menuitem"` buttons — not a `<details>`, which
gives no control over arrow-key movement. Enter, Space or Down opens at the first item, Up opens at
the last, Home and End jump, Escape closes and returns focus to the trigger, and so does choosing a
format. The busy state is announced, the finished file is named in an `aria-live="polite"` region,
and a failure is a `role="alert"`. It sits beside **Generate** on the scheduling page, because that is
where the plan is made, and it is unavailable while a run is in flight — the schedule it would write
is about to be replaced. It carries the parameters of the run **on screen**, not the ones currently in
the form, which may have been edited since.

**Nothing about the export is gated.** The scheduling page is readable by every persona and nothing on
it is behind a policy, so the export is not either: it contains exactly what is already on the screen.
A test pins that down, so it stays a decision rather than an oversight.

## Consequences

- ✅ The feature works offline, on a static host, with no backend and no native code. Nothing about
  the deployment changed.
- ✅ It is testable without a browser. 148 tests in `WorkPlanStudio.Export.Tests` read the produced
  bytes back: the workbook is unzipped and its grid reconstructed, the PDF's cross-reference table is
  parsed and every offset checked against the object it points at, and CsCheck properties push
  generated text through the CSV grammar and the shared-string table.
- ✅ The export is evidence, not a screenshot. It carries the dispatch rule, the target-date rule, the
  seed, the multi-start and local-search settings and the steps actually taken, so a reader who
  disagrees with the plan can reproduce it.
- ➖ The feature set is bounded, on purpose. The workbook has no charts, no themes, no pivot caches and
  no conditional formatting. The PDF has two typefaces, no images, no bookmarks, no tagged structure
  tree — it declares `/MarkInfo << /Marked false >>` rather than claiming an accessibility it does not
  deliver. Anything beyond that is new code here, not a library option.
- ➖ Two date formatters now exist. `ExportDates` is a deliberate copy of `Services/Format`, because
  the library may not reference the app. A test in the web suite asserts that the two agree for every
  shipped culture, so the copy cannot drift unnoticed — but it is a copy, and a third consumer would be
  the moment to extract it.
- ➖ WinAnsi is a ceiling. A part description in Greek or Chinese renders as `?` in the PDF, where the
  CSV and the workbook carry it correctly as UTF-8. Lifting it means embedding a font programme with a
  CMap, which is the dependency this record exists to avoid.
- ➖ Excel's own leap-year bug is now reproduced on purpose in `XlsxWriter`. A date before 1900-03-01
  cannot be a correct serial number, so such a cell falls back to ISO text: unsortable, but not
  silently wrong.
