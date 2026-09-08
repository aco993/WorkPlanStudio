# 12. Model working time and German labour law as capacity, in a second pure library

- **Status:** Accepted
- **Date:** 2026-09-08
- **Extends:** [ADR 0010](0010-periodic-calendars-and-setup-families.md), which left holidays, absences and per-week variation out

## Context

ADR 0010 gave the engine a repeating availability calendar and listed what it
could not express: public holidays, absences, any variation from one week to
the next. The app never used it — every work center was scheduled around the
clock, seven days a week, which no German plant does. The Arbeitszeitgesetz
(ArbZG) limits the working day, mandates breaks and rest, and closes Sundays
and public holidays; which days are holidays depends on the federal state.

A shift plan that ignores those rules produces a schedule that cannot be run.
The question was where the rules live and how they reach the engine.

## Decision

**A second pure library, `WorkPlanStudio.WorkingTime`.** The engine stays a
generic scheduler on an abstract second axis; it knows nothing about
Sundays, states or §4. The working-time model knows all of that and nothing
about Blazor, EF Core or the clock. It references the engine only to hand over
a calendar. An architecture test pins both boundaries.

**Rules are hard cuts on capacity, applied in a fixed order and recorded.**
A shift instance is clipped for the Sunday rest (§9), capped for the working
day or the night cap (§3, §6), delayed until its crew has had its rest (§5),
and then has its breaks carved out (§4). Every cut becomes a
`RuleApplication` — rule, shift, day, before, after — so the UI can say
"§3: the day shift on Monday was cut from 11 h to 10 h" instead of silently
producing less capacity. Every statutory number is a parameter of
`WorkingTimeRules`, with the section it comes from in a catalogue.

**Public holidays are computed, not tabled.** Easter by the anonymous
Gregorian algorithm, the movable feasts from it, the fixed ones per state,
including the partial ones a plant opts into. Golden tests pin every state
against the published 2026 tables.

**Two engine additions carry it.** A calendar *phase* lets a weekly pattern be
written from Monday 00:00 while the horizon starts on a Wednesday morning.
*Blackouts* are absolute, tagged closed intervals — holidays and absences — on
top of the repeating pattern; they are finite, but the pattern is not, so
placement stays total. Breaks would have split every shift into windows too
short for a long operation, so the dispatcher may now *pause* an operation
across a gap no longer than the longest break and resume it: the machine
waits for the crew, the part is not scrapped. Shift ends, rest periods and
blackouts are never bridged. Utilisation divides by open time, not by the
makespan, so a machine that ran every staffed hour reads 100 %.

**The app owns the settings.** One `PlantSettings` row (state, the §3/§6
extensions, the §10 exceptions, the §9 (2) boundary shift, the §5 rest), a
shift-pattern preset per work center, and absences per work center. The
mapper builds one timeline per work center per run, hands the engine the
calendar and hands the page the annotated segments for the Gantt shading.

## Consequences

- ✅ The schedule is one a German plant could run. The Gantt shows why a
  machine is idle — break, off shift, Sunday, Fronleichnam, spindle service —
  and the Working-time page explains every cut the rules made.
- ✅ Each rule is a parameter, each parameter is explained in the UI, and each
  is covered by a test: unit tests per section, property tests that the
  limits hold for random patterns and that placed work only ever sits on
  open time, golden holiday tables per state.
- ✅ The engine remained generic. Nothing in `WorkPlanStudio.Scheduling` says
  "Sunday".
- ➖ Wall-clock only: the model has no time zone and no daylight-saving
  transition. A plant's local day is the axis, which is right for one plant
  and wrong for two.
- ➖ The §3 and §6 averaging periods are honoured as caps, not tracked as
  averages over 24 weeks or a month. A plant using the extension every day
  is not flagged.
- ➖ An operation longer than the longest shift (breaks bridged) can never be
  placed. It is reported as a rejected order rather than split across days,
  because the model still has no preemption beyond breaks.
- ➖ Three presets, no custom shift editor. Custom patterns are one form away;
  the model already accepts any set of shifts and crews.
