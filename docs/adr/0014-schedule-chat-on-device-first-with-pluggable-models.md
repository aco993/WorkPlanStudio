# 14. A conversation over the schedule: on-device first, models pluggable

- **Status:** Accepted
- **Date:** 2026-09-08
- **Extends:** [ADR 0005](0005-explainable-scheduling-and-optional-ai.md)

## Context

The assistant card narrated a run in three lines and could, with a key, ask
one OpenAI-compatible endpoint to rephrase them. Planners ask follow-up
questions — *why is PO-1003 late*, *what changes if I use SPT*, *why is the
grinder idle on Thursday* — and a portfolio reader wants to see how an AI
feature is engineered, not that an API was called. Two constraints stay:
the app is a static site with no backend and no secret store, and it must
work fully without any key.

Options considered:

1. **Model-only chat.** Every question goes to a provider. No key, no chat;
   and every number in an answer is the model's word, which the page cannot
   check.
2. **Retrieval prompt.** Send the whole schedule as JSON and let the model
   compute. Same key dependency, larger prompts, and arithmetic done by the
   least reliable component in the stack.
3. **On-device answers first, model as a voice.** Recognise the question's
   intent, compute the answer from the same view-model the page renders, and
   only then — if a key is configured — hand the facts plus the on-device
   answer to a model for a conversational reply. Any failure shows the
   on-device answer with a note.

## Decision

Option 3.

- **`OfflineScheduleAnswerer`** is a small intent recogniser (regular
  expressions over English and German keywords) with answers assembled from
  `ScheduleChatContext`: the result on the page, the parameters that
  produced it, the plant's working-time rules and the holidays inside the
  horizon. It is deterministic — the same question on the same schedule
  always yields the same text — and localized through the same `.resx` as
  the page.
- **A what-if is computed, not guessed.** "What if I use SPT?" re-runs the
  scheduler with the alternative rule and compares late orders, tardiness
  and makespan, then says whether that is better.
- **`IChatProvider`** is the seam for models. Three implementations speak
  the three wire formats a user is likely to have a key for: any
  OpenAI-compatible `/chat/completions` endpoint, Anthropic's Messages API
  and Google's Gemini `generateContent`. Each is a thin client over
  `HttpClient` with source-generated JSON, a 20-second budget and the key in
  the header the provider expects — never in a query string.
- **`ScheduleChat`** is the façade the page uses. It keeps the conversation,
  resets it on every new run (an answer about an old schedule would
  mislead), calls the on-device answerer first, and, when a provider is
  configured, sends `ChatFacts.Describe(context)` — the invariant-culture
  facts — plus the on-device answer as the system prompt and the recent turns
  as the conversation. On any provider failure the on-device answer is shown
  with a note naming the reason. The chat therefore always answers.
- The existing narration keeps its place above the thread and uses the same
  provider seam; `OpenAiScheduleNarrator` became `AiScheduleNarrator`.

## Consequences

- ✅ Works with nothing configured, offline, in both languages — the public
  demo needs no key and no network.
- ✅ Numbers in an answer are the page's numbers. A model can rephrase them
  and is told not to invent others; the on-device answer is always one click
  away as the source of truth, and the source of every answer is labelled.
- ✅ Testable without a network at every layer: intents and answers as unit
  tests, the three providers over a stubbed transport (headers, paths,
  message folding), the façade's what-if and fallback, the panel in bUnit,
  and the browser flow in Playwright.
- ✅ Adding a provider is one class; adding a question is one regex and one
  answer method, both covered by tests.
- ➖ The recogniser is keyword-based. A question outside its vocabulary gets
  the help text rather than an answer, unless a model is configured — which
  is precisely the division of labour intended.
- ➖ Browser-to-provider calls need CORS. OpenAI, Anthropic (with its
  explicit direct-browser header), Gemini, OpenRouter and local servers allow
  them; some proxies do not. The settings dialog says so. A production
  deployment would put a backend proxy in front and keep the key there
  ([SECURITY.md](../SECURITY.md)).
