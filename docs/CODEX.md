# Reviewing this repo with an AI agent (Codex / ChatGPT)

This repository is set up so an AI coding agent can review it, help you publish it
and explain it to you. The agent picks up [`AGENTS.md`](../AGENTS.md) (at the repo
root) for context automatically. Below are **ready-to-paste prompts** — copy one
into Codex (or ChatGPT with the repository open / files attached) and send it.

> Tip: run the agent **from the repository root** so it can see `AGENTS.md`,
> `README.md` and `docs/`.

---

## Prompt 1 — Review the repository

```text
You are reviewing the "WorkPlan Studio" repository: a .NET 10 Blazor WebAssembly
app with a pure, finite-capacity production-scheduling engine. Read AGENTS.md and
README.md first, then docs/SCHEDULING.md and docs/TESTING.md.

Do the following and report back:
1. Build it and run the tests. State exactly what passed/failed.
   - dotnet build WorkPlanStudio.slnx
   - dotnet test tests/WorkPlanStudio.Scheduling.Tests/...
   - dotnet test tests/WorkPlanStudio.Web.Tests/...
2. Review the code for correctness bugs, edge cases, security and .NET 10 best
   practices. Pay special attention to the scheduling engine
   (src/WorkPlanStudio.Scheduling) and the EF→domain boundary (ScheduleMapper).
3. Sanity-check the claims: is the engine really dependency-free? Is it
   deterministic? Is coverage as stated?
4. Assess GitHub-readiness (README, docs, CI workflows, licence, community files).
5. Give a single prioritised list of findings — "must-fix" vs "nice-to-have" —
   each with the file path and a concrete suggestion. Be specific; don't pad.
```

## Prompt 2 — Help me publish it to GitHub

```text
Walk me through publishing this repository to GitHub from scratch, step by step,
assuming I have git installed and a GitHub account. See docs/PUBLISHING.md and
adapt it to my machine.

Give me the exact commands to: initialise git, make the first commit, create the
repository and push. Then:
- confirm the repository and live-demo URLs in the README match my GitHub account;
- tell me how to enable the GitHub Pages live demo;
- tell me how to confirm the CI and Deploy workflows went green.
Stop and wait for me after each major step.
```

## Prompt 3 — Explain this project to me

```text
I'm comfortable with code but newer to .NET/Blazor. Explain "WorkPlan Studio" to me
in plain language:
1. What the app does and who it's for.
2. How the pieces fit together — the pure scheduling engine vs the Blazor app vs
   the four test layers — and why it's split that way.
3. The 3–4 things a senior reviewer or hiring manager would find most impressive,
   and where to point to them.
Then give me a "10-minute tour": the exact files to open, in order, to understand
the project, with one sentence on each.
```

## Prompt 4 — Suggest the next feature

```text
Based on docs/SCHEDULING.md section 10 ("Scope & possible extensions") and the
ADRs in docs/adr, propose the single most valuable next feature for this project.
Explain why, sketch the design (which files change, which stay untouched), and how
you'd test it — without breaking the golden rules in AGENTS.md.
```

---

### After the agent is done

If it suggests changes, ask it to make them on a branch and open a pull request —
the repository ships a PR template and CI that will run the test layers on it.
