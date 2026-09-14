# 24. Put the Arbeitszeitgesetz on the screen, and only as far as it is computed

- **Status:** Accepted
- **Date:** 2026-09-11
- **Extends:** [ADR 0012](0012-working-time-as-capacity.md), which made the statute
  capacity and left the page showing a fraction of it

## Context

The working-time library grew past the page. It now computes, per crew, the
werktäglich average § 3 sentence 2 makes the ten-hour day conditional on, the
first date a plan breaches it, and the whole days that have to come out to
bring it back; it counts the free Sundays a declared rota actually leaves
against § 11 (1) rather than answering 0 or 52; and it gained three rules —
§ 5 (2) compensating rest, the § 9 (2) multi-shift precondition and the § 11
Ersatzruhetag. None of it reached a reader. Worse, the three new rules had no
resource keys, so the rule catalogue rendered `Rule_ReplacementRestDay_Title`
to a German employer, and `IStringLocalizer` reports that as success.

The gap ran the other way too. § 5 (2) reserves the shortened ten-hour rest for
hospitals and care, hospitality, transport, broadcasting, agriculture and animal
husbandry, **and** owes a twelve-hour compensating rest for every shortening
taken. The page offered it as a number between 10 and 11, the validator allowed
either, and a CHECK constraint agreed. A machine shop — this application's own
domain — could take an entitlement it does not have, in three clicks, with
nothing on screen to say so.

This is the page a German employer reads most carefully. A compliance surface
that shows only the flattering half of a statute is worse than one that shows
none of it, because the missing half is the obligation.

## Decision

**Every statutory claim on screen cites its paragraph and is a number the code
computed.** The averaging card shows, per crew and per section, the average
werktäglich hours against the eight-hour reference, the period that was
measured, the Werktage it was divided by, the first date the average is
breached and the days still owed. Nothing on it is estimated, rounded to a
friendlier figure, or carried over from the README.

**The preview is materialised over twenty-six weeks, not over one.** A weekly
pattern can only ever *project* an average; § 3 sentence 2, § 6 (2) and § 11 are
duties about a period, and a period exists on a calendar with public holidays in
it. Twenty-six weeks is the longer of the two reference periods the subsection
offers, so whichever the plant declares, the number is measured rather than
projected — and the breach date is a real date instead of the far end of a
projection. The range starts on the first Monday of the year the holiday table
already shows, so the two halves of the page talk about the same year and the
render stays deterministic.

**The § 5 (2) entitlement is gated in four places, not hidden in one.** The
ten-hour option does not exist in the select until a sector is named; choosing
"no sector" puts the value back to eleven and says so in a live region; the
validator refuses the combination; and `CK_PlantSettings_RestSector` refuses it
in the table. A permission that is only hidden is still granted — to an import,
to the API, to a hand-edited payload — and the form is the one layer that never
sees any of those. When a sector *is* named, the control says what the choice
costs: another rest of the same crew extended to at least twelve hours within a
calendar month or four weeks.

**The rota is a setting, because without it § 11 (1) can only accuse.** A weekly
pattern says whether a crew works Sundays, never how often; the honest reading
of "works Sundays, no rota declared" is that the same crew works all fifty-two
of them, which flags every lawful § 10 plant for ever. `SundayRotationWeeks`
appears with the § 10 permission it belongs to, and four crews taking one Sunday
in four then read as thirty-nine free Sundays against the fifteen the section
requires — lawful, and reported as lawful.

**The preview can ask what Saturday and Sunday operation would cost.** None of
the four shipped shift presets can breach the § 3 average or work a Sunday, so
without this the averaging card and the free-Sunday count would be decoration:
correct, and never able to say anything but "kept". The operating-day selector
extends the selected pattern's days inside the preview only. It is honest about
being a preview — it persists nothing and names no work centre — and it is the
only way the page can show a planner what the section actually does.

**The state is a word before it is a colour, everywhere.** "eingehalten" /
"überschritten" is the cell's text; the green or red pill only repeats it. The
worst average and its verdict also go into a polite live region, and the two
conditional controls announce themselves when the condition that reveals them
changes.

**Resource keys the model derives are pinned by a test, not by review.** A
theory enumerates every key the page can build out of an enum member — the rule
catalogue, the segment kinds, the sectors, the reference periods, the Sunday
statuses, the holiday names of all sixteen states — and asserts both languages
resolve it. It is the only defence that scales, because the failure mode is a
render, not an exception.

## Consequences

- ✅ A plant running seven eight-hour days now reads, in German: *9,39 h von
  höchstens 8 h — überschritten*, first exceeded on 21 June 2026, 24 days still
  to be compensated, measured over 138 Werktage. Verified in a browser in both
  languages. The same plan previously reported nothing at all.
- ✅ The three rules the library added have words, and the two Brandenburg
  holidays the calendar gained — Ostersonntag and Pfingstsonntag — have names.
  Both were found by the completeness theory rather than by reading the page.
- ✅ A returning visitor's database survives: schema 7 has an upgrade step from
  6 beside the one from 5, and a ten-hour rest stored before the sector column
  existed comes back as eleven rather than failing the whole upgrade on the new
  CHECK.
- ➖ **The preview's operating-day selector shows a pattern no work centre can
  have.** `ShiftPatterns` offers four presets and a work centre picks one of
  them by key; a pattern extended to Saturday or Sunday is not among them and
  cannot be saved. The honest fix is a pattern the plant can define and a work
  centre can point at, which is master data and belongs to another stream. Until
  then the selector is labelled as a property of *this preview*, and the label is
  carrying weight.
- ➖ **§ 5 (2)'s compensation window is per repeating week, and the subsection
  says calendar month or four weeks.** The library says so at the declaration;
  the page inherits it. In practice no shipped preset shortens a rest below
  eleven hours at all — every inter-shift gap is fifteen hours or more — so the
  `RestCompensation` application is reportable and not currently reachable. The
  page offers the entitlement correctly and would report the obligation the
  moment a pattern used it.
- ➖ **The averaging is shown for the preview pattern, not for the plant.** Every
  work centre has its own pattern and its own absences, and the schedule page
  runs them all; this card reads one pattern at a time and no absences. A
  plant-wide view needs the per-centre timelines the mapper already builds, and
  the mapper is another stream's file.
- ➖ **Three settings columns landed on an entity the API compiles into itself**,
  so the server needed a migration it did not ask for. The API's own
  `PlantSettings` mapping still carries the schema-6 CHECK constraints: the
  browser refuses an unjustified ten-hour rest and the server does not. The
  endpoint should call `PlantSettingsValidator` — now in `Validation/` beside the
  other three, which is the move its duplicated copy asks for in its own doc
  comment — instead of the copy it keeps.
- ➖ **The committed visual baselines for `/working-time` are invalid.** The page
  gained a card, two selects and a conditional field. They have to be recaptured
  on Linux, in all three of the combinations the matrix covers.
- ➖ The page reads "the reference period" as a plant-wide setting, which is what
  § 3 sentence 2 is. A works agreement can set different periods for different
  groups; nothing here can express that, and nothing here claims to.
