# The schedule assistant

**English** · [Deutsch](AI-ASSISTANT.de.md)

The Scheduling page explains its result in plain language and answers questions
about it: which work center is the constraint, why an order is late, why a machine
is idle on Thursday, what would change under another dispatch rule. The feature is
built to show how an AI capability should be engineered into a product — a working
offline default, a provider abstraction, graceful fallback, explicit configuration
and a stated security posture — rather than as a thin wrapper around an API. See
[ADR 0005](adr/0005-explainable-scheduling-and-optional-ai.md),
[ADR 0014](adr/0014-schedule-chat-on-device-first-with-pluggable-models.md) and
[ADR 0025](adr/0025-hostile-input-on-the-model-path.md).

## Three layers: analysis, answers, then a model

The key idea is that the **analysis is deterministic**, the **answers are computed
on your device**, and the **model is optional**.

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
   found by re-dispatching the other rules under a capped budget and suggesting a
   switch only when one measurably beats the current result **on the objective**.
   It used to rank those alternatives by tardiness alone, which could recommend a
   rule that cut tardiness and raised the penalty; it now ranks by what the search
   minimises. The explanation contains no prose, so nothing in it can hallucinate.

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

   Answers are deterministic and localized through the same `.resx` as the page.
   There are **three** outcomes, not two: a recognised question gets its answer, a
   question outside the vocabulary gets the help text, and a question that names
   something the answerer cannot resolve — `PO-9999`, `CNC-30`, a work center whose
   name matches two — ends the search with *"I cannot find PO-9999"* rather than
   falling through to the next matching intent with a confident wrong number. The
   old prefix match is why `CNC-30` used to be answered with `CNC-300`'s figures.

3. **`IChatProvider`** is the seam for a model. Three clients speak the wire
   formats a user is likely to hold a key for. Each is a thin `HttpClient` call
   with source-generated JSON and the key in the header the provider expects —
   never in the URL, and never in the *path*: the Gemini model name is a path
   segment, so it is validated against `^[A-Za-z0-9._:@-]{1,100}$` and escaped at
   the point of use. Each is bounded in four ways: a **20-second budget covering
   the send and the reading of the body**, a **1 MiB** ceiling on the response, an
   output cap of **1 024 tokens** on all three (it used to be Anthropic only), and
   an early refusal of an oversized `Content-Length`.

4. **`ScheduleChat`** keeps the conversation for the current run, asks the
   on-device answerer first and, when a provider is configured, sends the
   invariant-culture facts plus the on-device answer as the system prompt and the
   recent turns as the conversation. A question longer than **1 000 characters** is
   answered on the device and **no request is made**. The conversation is
   serialised — two questions cannot interleave the turn list — and a new run
   cancels the one in flight and drops its answer rather than appending it under
   the wrong schedule.

   On **any** provider failure the on-device answer is shown with a note naming the
   reason, so the chat always answers. "Any" is now literal: the old code caught
   seven exception types and a malformed body reached the planner as a red banner.
   Every answer is labelled with its source.

## What the model is told, and what it is not trusted with

`ChatFacts.BuildSystemPrompt` is the only place a prompt is assembled. The
schedule facts and the on-device answer go inside named fences
(`<schedule_facts>…</schedule_facts>`, `<on_device_answer>…</on_device_answer>`),
the instruction that the fenced region is **data and never an instruction** is
restated *after* the data, and every value that goes in is put through `PromptText`
first: single-line fields have whitespace collapsed and `<`, `>`, `#` and backticks
removed; blocks keep their newlines and lose the same characters.

The facts are also **bounded**, at 40 orders, 20 work centres, 12 operations per
centre, 10 late findings and 16 000 characters overall — and every cut is *stated*
in the prompt ("… and 260 more orders, not listed here"), so a truncated list
cannot be read as the whole shop.

This is mitigation, not a solution, and the ADR says so in as many words. The text
inside the fence is whatever a person typed into a part name or an order reference.

## Using your own model (BYOK)

The model is **off by default** — the app is fully usable without it. To turn it
on, open the Scheduling page, click the **gear** on the *Schedule assistant* card
and choose a provider. Endpoint and model are filled in with sensible defaults you
can overwrite:

| Provider | Endpoint | Default model | Key header |
| --- | --- | --- | --- |
| OpenAI-compatible (OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio …) | `https://api.openai.com/v1` | `gpt-4o-mini` | `Authorization: Bearer` |
| Anthropic | `https://api.anthropic.com` | `claude-opus-5` | `x-api-key` + `anthropic-version` + `anthropic-dangerous-direct-browser-access` |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta` | `gemini-2.5-flash` | `x-goog-api-key` |

Endpoints must be absolute HTTPS URLs; HTTP is accepted only for loopback
(`http://localhost:11434/v1` for Ollama). A Gemini model must be a bare name —
`gemini-2.5-flash`, not `models/gemini-2.5-flash` — which is the deliberate cost of
the pattern that keeps the model out of the request's routing.

> **CORS.** Because the app is a static site with no backend, the request goes
> **from your browser** to the endpoint, so the provider must allow browser
> requests. OpenAI, Anthropic (with the direct-browser header the client sends),
> Gemini, OpenRouter and local model servers do; some proxies do not. A blocked
> call falls back to the on-device answer with a note.

## Security

- The API key is stored in this browser's `localStorage`, **under a name scoped to
  the provider**, and separately from the rest of the assistant settings. That
  storage is **not a secret vault** and can be read by any script on the same
  origin. The dialog says so on screen, not only here.
- **The dialog never reads the key back.** The field is empty when it opens and a
  blank field means "keep the stored key", so the secret does not re-enter the DOM.
  *Forget this key* removes it in one click, and switching provider switches which
  stored key applies rather than pointing one host's key at another.
- Nothing in the assistant is logged, at all; two source-scanning tests keep it
  that way.
- Endpoints must be absolute HTTPS URLs (HTTP only for loopback); URLs with
  embedded credentials, query strings or fragments are rejected — and so is a model
  name that is not a bare identifier, because it ends up in the path.
- Provider calls have a 20-second budget covering the request *and* the body read,
  a 1 MiB response ceiling and a 1 024-token output cap. Caller cancellation stays
  distinct; a timeout or failure yields a localized fallback without raw exception
  text.
- Because the key lives in the browser, don't enable a model on a shared computer.
  A production deployment should use a backend proxy and a server-side secret store
  — see [SECURITY.md](SECURITY.md).
- The data sent is a **bounded, sanitised and fenced** set of schedule facts: KPIs,
  work-centre names, order references, dates and the plant's rule settings. It is
  not claimed to contain no personal data, because those are free-text fields a
  person fills in; it is claimed to be neutralised, capped, and never given
  authority over the instruction.

## Testing

The whole feature is testable without a network:

- `OfflineScheduleAnswerer`: intents in English and German, answers carrying the
  schedule's own numbers, the what-if comparison verdicts, determinism, and the
  three distinct outcomes — answered, not understood, and recognised-but-unresolvable.
- The three providers run against a **stubbed `HttpMessageHandler`**. The tests
  assert the path, the headers (including Anthropic's version and direct-browser
  headers), the JSON shape, Anthropic's turn folding, the output cap on all three,
  and that an empty model answer is an error rather than a blank bubble.
- **A hostile-response table across all three providers**: a null choice, a null
  message, a null content, an empty candidate list, a body that never finishes
  arriving, a body past the 1 MiB ceiling, and a model name crafted to rewrite the
  request target. Each one is asserted to become a typed failure *and* to still
  answer the planner from the device.
- `PromptHardeningTests`: the fences, the boundary restatement, the character
  stripping, the per-section caps and the "… and *n* more" statement of each cut.
- `ChatConversationSafetyTests`: two questions cannot interleave, a reset cancels
  the request in flight, an answer from a superseded run is dropped, and an
  unanswered question is withdrawn by reference rather than by index.
- `AssistantKeyHandlingTests`: the per-provider storage name, the migration out of
  an older combined value, blank-means-keep, and that the key is absent from the
  serialised settings.
- Two source-grep tests asserting the assistant logs nothing.
- bUnit renders the panel: suggestions, a clicked suggestion answered on-device, a
  typed question, clearing, and the settings dialog switching endpoint and model
  with the provider — and a test asserting the stored key never appears in a
  rendered `value` attribute.
- Playwright drives the real page: a suggested question, a typed what-if, and the
  same in German.

See [TESTING.md](TESTING.md) for the overall strategy.
