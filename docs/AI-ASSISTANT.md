# The schedule assistant

The Scheduling page can explain its result in plain language: which work center is
the constraint, why a job is late, and what to try next. The feature is built to
show how an AI capability should be engineered into a product — abstraction, a
working offline default, graceful fallback, configuration and a clear security
posture — rather than as a thin wrapper around an API. See
[ADR 0005](adr/0005-explainable-scheduling-and-optional-ai.md) for the decision.

## Two layers: analysis, then narration

The key idea is that the **analysis is deterministic** and the **AI is optional**.

```
SchedulingResult ──▶ ScheduleExplainer ──▶ ScheduleExplanation   (engine, pure)
                                              │  structured, language-neutral
                                              ▼
                                        IScheduleNarrator          (app)
                                        ├─ RuleBasedNarrator   ← default, offline
                                        └─ OpenAiScheduleNarrator ← optional, BYOK
                                              │
                                              ▼
                                        ScheduleAssistant          (façade: pick + fallback)
```

1. **`ScheduleExplainer`** (in the pure engine) turns a run into a structured
   `ScheduleExplanation`: summary KPIs, the bottleneck work center, the worst late
   jobs each with the resource it queued on, and one **computed** recommendation —
   found by quickly re-dispatching the other rules under a capped budget and only
   suggesting a switch when one measurably beats the current result. It is
   deterministic and contains no prose, so nothing here can hallucinate.

2. **`IScheduleNarrator`** turns that structure into text:
   - **`RuleBasedNarrator`** — the default. Localized (EN/DE), instant, offline,
     needs no key. It is also the narrator used by the demo and the tests, and the
     fallback when AI fails.
   - **`OpenAiScheduleNarrator`** — optional. Sends the *facts* to an
     OpenAI-compatible `/chat/completions` endpoint and returns the model's prose.

3. **`ScheduleAssistant`** is the façade the page uses. It picks the AI narrator
   when one is configured and **falls back** to the rule-based text on any error,
   surfacing a note so the user knows what happened.

## Using your own model (BYOK)

The AI narrator is **off by default** — the app is fully usable without it. To turn
it on, open the Scheduling page, click the **gear** on the *Schedule assistant*
card and fill in:

| Field | Example |
| --- | --- |
| Endpoint | `https://api.openai.com/v1` (any OpenAI-compatible base URL) |
| Model | `gpt-4o-mini` |
| API key | your key |

Then use **Enhance with AI** on the assistant card.

> **CORS.** Because the app is a static site with no backend, the request goes
> **from your browser** to the endpoint. The endpoint must therefore allow browser
> (CORS) requests — e.g. OpenRouter, a local model server (Ollama / LM Studio) or a
> small proxy you control. Providers that only accept server-to-server calls will
> be blocked by the browser; the assistant then falls back to the rule-based text.

## Security

- The API key is stored **only** in this browser's `localStorage` and is sent
  **only** to the endpoint you configure. It is never logged, and no key is ever
  committed to the repository.
- Because the key lives in the browser, don't enable AI on a shared computer.
- The data sent is the small set of schedule facts (KPIs, work-center names, plan
  numbers) — no personal data.

## Testing

The whole feature is testable without a network:

- `RuleBasedNarrator` is exercised directly (deterministic output, tones).
- `OpenAiScheduleNarrator` runs against a **stubbed `HttpMessageHandler`** — the
  test asserts the request carried the key and the computed facts, and parses a
  canned model response; a `500` asserts it throws.
- `ScheduleAssistant` is tested for all three paths: not-configured, AI healthy,
  and AI failing (fallback with a note).
- A bUnit test renders the panel and checks the AI action appears only once a
  provider is configured.

See [TESTING.md](TESTING.md) for the overall strategy.
