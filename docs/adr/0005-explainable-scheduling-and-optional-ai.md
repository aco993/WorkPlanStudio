# 5. Deterministic explanation first; AI is an optional narrator

- **Status:** Accepted
- **Date:** 2026-07-09

## Context

A schedule is only useful if a planner understands *why* it turned out the way it
did — which resource is the constraint, why a job is late, what to try next. An
An AI narrator is one possible way to surface that. A direct LLM-only feature would
be a poor fit for this application:
it could hallucinate numbers, it would not work in the public GitHub Pages demo
(no server, no key), and a browser app cannot hold a secret. The app is also a
static WebAssembly site, so any live model call is a *client-side* request to a
third-party endpoint.

## Decision

Separate the **analysis** from its **narration**.

1. The engine produces a **deterministic, structured, language-neutral**
   `ScheduleExplanation` (`ScheduleExplainer`): summary KPIs, the bottleneck work
   center, the worst late jobs with the resource each queued on, and one *computed*
   recommendation (found by re-dispatching the other rules). No prose, no AI.
2. The app narrates that explanation behind one seam, `IScheduleNarrator`:
   - `RuleBasedNarrator` — the **default**. Deterministic, offline, localized
     (EN/DE), needs no key. It is also the demo/test provider and the fallback.
   - `OpenAiScheduleNarrator` — **optional**, bring-your-own-key, for any
     OpenAI-compatible endpoint. It only ever rephrases the computed facts.
3. `ScheduleAssistant` owns provider selection and **falls back** to the
   rule-based text on any AI error. BYOK settings live only in the browser's
   `localStorage`; nothing secret is committed.

## Consequences

- ✅ The public demo works with **zero configuration** — the explanation is always
  there, computed on-device.
- ✅ The AI cannot invent numbers: it is handed the facts and asked to rephrase
  them; the deterministic version is always available for comparison.
- ✅ Provider selection is isolated behind a small interface and failure does not
  remove the deterministic explanation.
- ✅ Everything is testable without a network: the rule-based narrator directly,
  the AI narrator over a stubbed HTTP transport, the fallback via the façade.
- ➖ The BYOK endpoint must permit browser (CORS) requests, which not every
  provider does; this is documented in [AI-ASSISTANT.md](../AI-ASSISTANT.md).
- ➖ Two narrators and a façade are more moving parts than a single call — the
  price of the fallback and the offline default.
