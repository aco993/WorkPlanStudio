<!-- Thanks for the contribution! Keep this short. -->

## What & why

<!-- What does this change do, and why? Link any related issue. -->

## How it was tested

- [ ] Engine tests green — `dotnet test tests/WorkPlanStudio.Scheduling.Tests/...`
- [ ] Working-time tests green — `dotnet test tests/WorkPlanStudio.WorkingTime.Tests/...`
- [ ] Mapper & component tests green — `dotnet test tests/WorkPlanStudio.Web.Tests/...`
- [ ] E2E run (if the UI changed) — see `docs/TESTING.md`

## Checklist

- [ ] Builds clean (warnings are treated as errors)
- [ ] Tests added or updated for the change
- [ ] `dotnet format WorkPlanStudio.slnx --verify-no-changes --severity warn` passes
- [ ] Docs / [ADR](../docs/adr) updated if behaviour or a decision changed

---

<details>
<summary>What CI will run, and what has to be green</summary>

| Check | Workflow | What it fails on |
| --- | --- | --- |
| Engine + working-time tests | `ci.yml` | a failing test, or line coverage below 90 % for either library |
| Data, mapper, assistant & component tests | `ci.yml` | a failing test, or line coverage below 65 % for the app |
| Playwright end-to-end, axe, visual regression | `e2e.yml` | a failing flow, any WCAG 2.2 AA violation, a pixel diff above 0.2 %, or a screen with no committed baseline |
| Formatting + documentation integrity | `quality.yml` | `dotnet format` finding a change, a broken relative link or anchor, or the two `.resx` files drifting apart |
| CodeQL C# analysis | `codeql.yml` | a security alert. It does not run on a pull request from a fork, because such a run cannot be granted the permission to upload its result — fork changes are scanned once they land on `main`. |

The Performance workflow (benchmarks, Lighthouse, the wall-clock budget
tripwires) runs on `main`, weekly and on demand. It is deliberately not a
pull-request gate: those measurements depend on a shared runner, and a check
that reddens on runner noise teaches everyone to re-run it rather than read it.

Visual baselines are committed for Linux only and compared by CI; see
[ADR 0021](../docs/adr/0021-visual-baselines-linux-only.md). If you changed
something visible, expect the visual job to fail, then take the `*.actual.png`
files from the run's `e2e-artifacts` and commit them as the new baselines.
</details>
