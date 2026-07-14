# ADR 0005: Deterministic explanation first; AI only narrates facts

- Status: Accepted
- Date: 2026-07-09
- Updated: 2026-07-13

## Context

Planners need to understand constraints and lateness. An LLM cannot be the source of scheduling truth and a browser cannot safely hold a production provider key.

## Decision

`ScheduleExplainer` computes a language-neutral `ScheduleExplanation`: KPIs, bottleneck, late jobs and a recommendation based on re-dispatching rules. `RuleBasedNarrator` is the always-available localized default. In server mode, `ScheduleAssistant` sends only bounded computed facts to the authenticated `/api/assistant/narrate` proxy; provider endpoint/model/key are operator-controlled server configuration. Failures fall back to deterministic narration. Offline demo mode may use an explicitly non-production browser BYOK option.

## Consequences

- AI never decides or changes the schedule and cannot invent source KPIs unnoticed.
- Production provider secrets never reach the browser.
- Provider calls are authorized, HTTPS-only, timed out and rate-limited.
- The base product and public demo remain fully usable without an AI provider.
