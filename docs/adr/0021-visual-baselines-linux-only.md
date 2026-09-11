# 21. Maintain visual baselines for one operating system, and fail on a missing one

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

The visual-regression suite screenshots nine screen/profile combinations and
compares them pixel-wise against committed baselines. Fonts, hinting and
anti-aliasing differ enough between operating systems that one baseline cannot
serve several, so the baselines were kept per OS, and the test wrote a baseline
and passed whenever one was missing.

Two things followed from that, and both were wrong.

A **missing baseline could never fail**. Rename a route from `/working-time` to
`/shift-rules`, or a profile from `desktop` to `wide`, and the file name changes,
no baseline exists, one is written, the test is green — and that screen is now
guarded by nothing, with no signal anywhere that its guard disappeared. The one
failure mode a visual-regression suite exists to catch was the one it could not
report.

And the committed `visual-baselines/windows/` set — nine binary files —
**was never compared by any automation**. CI runs `ubuntu-latest` only, so only
the Linux set was ever a gate. The contributor docs nevertheless instructed
everyone to refresh both sets after a visible change, with no mechanism to
detect that they had not. Nine PNGs of maintenance with no consumer, and a
second reference that quietly diverged from what CI actually checks.

## Decision

**One maintained operating system: Linux, the one CI runs.**
`visual-baselines/linux/` is committed and compared; `visual-baselines/windows/`
is deleted. On any other operating system the visual tests **skip with a reason
that names this ADR**, rather than compare against an unverified reference or
bootstrap a new one. The maintained set is a list in `VisualRegressionTests`, so
adding macOS or Windows back is a two-line change — but the list is documented
as meaning "an OS with a CI leg", and adding a folder without a leg reintroduces
exactly the dead weight this decision removes.

A Windows CI leg was the alternative and was rejected: the committed Windows
baselines were produced on a developer's Windows 11 desktop, and a
`windows-latest` runner ships a different font set, so the leg would have been
red from its first run. A gate that must be regenerated before it can ever pass
is not a gate.

**A missing baseline fails.** The screenshot is still written — to
`E2E_ARTIFACTS`, where CI uploads it — and the message says which file to commit.
Only an explicit `UPDATE_VISUAL_BASELINES=1` (legacy alias `VISUAL_UPDATE=1`)
writes into the committed folder, so an environment variable leaking into CI can
no longer turn the whole suite into a no-op that reports green.

**The baseline set is an inventory, not a lookup.** One test asserts that the
committed file names are exactly the screen matrix — so a renamed screen fails
twice (no baseline for the new name, an orphan under the old one) and a baseline
for a screen that no longer exists fails too.

**On failure all three images travel together.** Expected, actual and diff are
written to the artifact bundle, so a reviewer can compare them without fetching
the baseline out of git.

## Consequences

- ✅ Renaming a screen can no longer silently drop its visual guard.
- ✅ Every committed baseline is compared by CI on every run. There is no
  second, unverified reference to keep in step.
- ✅ A leaked `UPDATE_VISUAL_BASELINES` cannot mask a regression: it is checked
  only where the write happens, and the inventory test still runs.
- ➖ A developer on Windows or macOS no longer gets a local visual signal; they
  see ten explicit skips and rely on the pull-request run. That is the price of
  not keeping a reference nothing verifies, and the skip says so out loud.
- ➖ Refreshing baselines after an intended visual change now means either a
  Linux machine or downloading the actual images from the failed CI run and
  committing them. Slower than regenerating locally, and correct more often.
