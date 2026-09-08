# The schedule assistant

The Scheduling page explains its result in plain language and answers questions
about it: which work center is the constraint, why an order is late, why a
machine is idle on Thursday, what would change under another dispatch rule. The
feature is built to show how an AI capability should be engineered into a
product — a working offline default, a provider abstraction, graceful fallback,
explicit configuration and a clear security posture — rather than as a thin
wrapper around an API. See [ADR 0005](adr/0005-explainable-scheduling-and-optional-ai.md)
and [ADR 0014](adr/0014-schedule-chat-on-device-first-with-pluggable-models.md).

## Three layers: analysis, answers, then a model

The key idea is that the **analysis is deterministic**, the **answers are
computed on your device**, and the **model is optional**.

```
SchedulingResult ──▶ ScheduleExplainer ──▶ ScheduleExplanation        (engine, pure)
                                              │  structured, language-neutral
                                              ▼
                                        IScheduleNarrator               (app)
                                        ├─ RuleBasedNarrator        ← default, offline
                                        └─ AiScheduleNarrator       ← optional, BYOK
                                              ▼
                                        ScheduleAssistant               (façade: pick + fallback)

ScheduleResult + parameters + rules ──▶ ScheduleChatContext            (one per run)
                                              │
                                              ▼
                                        OfflineScheduleAnswerer         ← intents EN/DE, deterministic
                                              │  + what-if re-run of the scheduler
                                              ▼
                                        ScheduleChat                    (façade: conversation + fallback)
                                              │
                                              ▼
                                        IChatProvider
                                        ├─ OpenAiCompatibleChatProvider  /chat/completions
                                        ├─ AnthropicChatProvider         /v1/messages
                                        └─ GeminiChatProvider            models/{model}:generateContent
```

1. **`ScheduleExplainer`** (in the pure engine) turns a run into a structured
   `ScheduleExplanation`: summary KPIs, the bottleneck work center, the worst late
   jobs each with the resource it queued on, and one **computed** recommendation —
   found by quickly re-dispatching the other rules under a capped budget and only
   suggesting a switch when one measurably beats the current result. It contains
   no prose, so nothing here can hallucinate.

2. **`OfflineScheduleAnswerer`** answers a question from `ScheduleChatContext` —
   the result on the page, the parameters that produced it, the plant's
   working-time rules and the public holidays inside the horizon. A small intent
   recogniser understands English and German phrasings of:

   | Ask about | Example |
   | --- | --- |
   | the bottleneck | *Which work center is the bottleneck?* · *Welcher Arbeitsplatz ist der Engpass?* |
   | late orders and why | *Which orders are late, and why?* |
   | one order | *How is PO-1003 doing?* — its steps with real dates and pauses |
   | one work center | *Tell me about CNC-300* — busy time, utilisation of open time, closed stretches |
   | idle time | *Why are the machines idle at times?* — closed time by reason, holidays named |
   | the rules | *Which working-time rules apply?* — the ArbZG settings in force |
   | a what-if | *What if I use SPT?* — **re-runs the scheduler** with that rule and compares |
   | a summary | *How does the schedule look?* |

   Answers are deterministic and localized through the same `.resx` as the page;
   a question outside the vocabulary gets the help text.

3. **`IChatProvider`** is the seam for a model. Three clients speak the wire
   formats a user is likely to hold a key for. Each is a thin `HttpClient` call
   with source-generated JSON, a 20-second budget, and the key in the header the
   provider expects — never in the URL.

4. **`ScheduleChat`** keeps the conversation for the current run (a new run
   resets it), asks the on-device answerer first and, when a provider is
   configured, sends the invariant-culture facts (`ChatFacts.Describe`) plus the
   on-device answer as the system prompt and the recent turns as the
   conversation. The model is instructed to use only those facts. On **any**
   provider failure the on-device answer is shown with a note naming the reason,
   so the chat always answers. Every answer is labelled with its source.

## Using your own model (BYOK)

The model is **off by default** — the app is fully usable without it. To turn it
on, open the Scheduling page, click the **gear** on the *Schedule assistant* card
and choose a provider. Endpoint and model are filled in with sensible defaults
you can overwrite:

| Provider | Endpoint | Default model | Key header |
| --- | --- | --- | --- |
| OpenAI-compatible (OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio …) | `https://api.openai.com/v1` | `gpt-4o-mini` | `Authorization: Bearer` |
| Anthropic | `https://api.anthropic.com` | `claude-opus-5` | `x-api-key` + `anthropic-version` + `anthropic-dangerous-direct-browser-access` |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta` | `gemini-2.5-flash` | `x-goog-api-key` |

Endpoints must be absolute HTTPS URLs; HTTP is accepted only for loopback
(`http://localhost:11434/v1` for Ollama). Then ask in the chat, or use **Enhance
with AI** for the narration.

> **CORS.** Because the app is a static site with no backend, the request goes
> **from your browser** to the endpoint, so the provider must allow browser
> requests. OpenAI, Anthropic (with the direct-browser header the client sends),
> Gemini, OpenRouter and local model servers do; some proxies do not. A blocked
> call falls back to the on-device answer with a note.

## Security

- The API key is stored in this browser's `localStorage`. That storage is **not a
  secret vault** and can be read by script running on the same origin. It is never
  logged or committed, and it is sent only to the endpoint you configured.
- Endpoints must be absolute HTTPS URLs (HTTP only for loopback); URLs with
  embedded credentials, query strings or fragments are rejected.
- Provider calls have a 20-second budget. Caller cancellation stays distinct;
  a timeout or failure yields a localized fallback without raw exception text.
- Because the key lives in the browser, don't enable a model on a shared
  computer. A production deployment should use a backend proxy and a
  server-side secret store — see [SECURITY.md](SECURITY.md).
- The data sent is the small set of schedule facts (KPIs, work-center names,
  order references, dates, the plant's rule settings) — no personal data.

## Testing

The whole feature is testable without a network:

- `OfflineScheduleAnswerer`: intents in English and German, answers carrying the
  schedule's own numbers, the what-if comparison verdicts, and determinism.
- The three providers run against a **stubbed `HttpMessageHandler`** — the tests
  assert the path, the headers (including Anthropic's version and direct-browser
  headers), the JSON shape, Anthropic's turn folding, and that an empty model
  answer is an error rather than a blank bubble.
- `ScheduleChat`: no-key path, what-if re-running the scheduler with the
  alternative rule, the model path carrying the facts, the fallback with a note,
  reset, and cancellation.
- bUnit renders the panel: suggestions, a clicked suggestion answered on-device,
  a typed question, clearing, and the settings dialog switching endpoint and
  model with the provider.
- Playwright drives the real page: a suggested question, a typed what-if, and the
  same in German.

See [TESTING.md](TESTING.md) for the overall strategy.
