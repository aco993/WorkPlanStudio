# 19. Slice the scheduling run on the one thread there is, rather than pretend to leave it

- **Status:** Accepted
- **Date:** 2026-09-11
- Extends [ADR 0001](0001-pure-scheduling-library.md), which keeps the engine pure and
  synchronous; this record is about the host, not the engine.

## Context

The scheduling run is the application's headline feature and it held the browser's
only thread from the first click to the last. `ProductionScheduleService` called
`new SchedulingEngine().RunCancellable(context, token)` — a synchronous, CPU-bound
call inside an `async` method — so for the whole run the tab could not repaint,
could not deliver a click, and could not observe the `CancellationToken` it had
just been handed. The token was decorative by construction: the only thing that
could have signalled it was the page's own `Dispose`, and nothing can run `Dispose`
while the engine owns the thread.

ADR 0022 made a run three times faster and eight thousand times leaner, which
narrows the window without closing it. A 100-job run is 100 ms on a desktop and a
browser is slower; the form offers up to 200 000 candidate schedules, and the
measurement below puts the seeded instance at the largest setting at about seven
seconds. Seven seconds of a frozen tab, with a button that says "Generating…" and
means it, is not a progress indicator.

So: can the work move off the UI thread for real?

## What was measured

.NET SDK 10.0.301, runtime 10.0.9, Windows 11 26200, Chrome. Three findings, in
the order they arrived.

### 1. The app cannot be built with threading at all

`dotnet publish src/WorkPlanStudio/WorkPlanStudio.csproj -c Release -p:WasmEnableThreads=true`

```
wasm-ld : error : --shared-memory is disallowed by sqlite3.o because it was not
          compiled with 'atomics' or 'bulk-memory' features.
emcc : error : ... wasm-ld ... --import-memory --shared-memory ... failed (returned 1)
error MSB3073: emcc exited with code 1
```

Threading requires a shared `WebAssembly.Memory`, and the prebuilt
`e_sqlite3.a` that `SQLitePCLRaw.lib.e_sqlite3` ships for `browser-wasm` is a
single-threaded object that refuses to link against one. SQLite in the browser is
this application's whole data layer — there is no backend behind it — so the choice
is not "threads or no threads", it is "threads or no database". That decides it on
its own, and the two findings below only confirm it.

### 2. Cross-origin isolation is required, and the pages host cannot send it

A stock `dotnet new blazorwasm` published with `-p:WasmEnableThreads=true` and
served over plain HTTP does not start:

```
MONO_WASM: Assert failed: SharedArrayBuffer is not enabled on this page. Please use
a modern browser and set Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy
http headers.
Uncaught (in promise) Failed to start platform.
```

GitHub Pages, where this application is deployed, cannot set response headers at
all — the same limitation that already keeps `frame-ancestors` out of the app's
`<meta>` CSP. The usual workaround is a service worker that re-issues every
response with the two headers attached, which adds a worker, a registration race on
first load and a second delivery path for every asset, to buy a feature that
finding 1 has already ruled out.

### 3. On this toolchain the threaded runtime does not boot even when it is isolated

The same reference app, served with `Cross-Origin-Opener-Policy: same-origin` and
`Cross-Origin-Embedder-Policy: require-corp` (`self.crossOriginIsolated === true`,
`SharedArrayBuffer` present, 28 logical cores):

```
MONO_WASM: Error in bindings_init Can't find method
  System.Runtime.InteropServices.JavaScript.JavaScriptExports.InstallMainSynchronizationContext
MONO_WASM: mono_wasm_start_deputy_thread_async() failed RuntimeError: unreachable
Uncaught (in promise) Failed to start platform.
```

Trimmed and untrimmed (`-p:PublishTrimmed=false`) alike.

### 4. Payload and startup — the reasons people usually give, and they are not the reason

Published reference app, single-threaded against multi-threaded:

| | single-threaded | `WasmEnableThreads=true` | change |
| --- | ---: | ---: | ---: |
| `wwwroot` uncompressed | 17.17 MB | 17.24 MB | +74 KB (+0.4 %) |
| `wwwroot` Brotli | 3.765 MB | 3.775 MB | +10 KB (+0.3 %) |
| `dotnet.native.wasm` | 2 900 203 B | 2 990 470 B | +90 267 B (+3.1 %) |
| files | 93 | 94 | + `dotnet.native.worker.*.mjs` |
| boot to first render | **490 ms** | never boots (finding 3) | — |

WorkPlan Studio's own published payload, for scale: **18.90 MB uncompressed,
5.60 MB Brotli, 91 files**, of which `dotnet.native.wasm` is 3.78 MB.

The size cost of threading is negligible. It is not what stops us. SQLite is.

## Decision

**The run stays on the UI thread and is sliced, and the application says so.**

**A seam, so the strategy is one registration.** `IScheduleRunner` takes the
context, an `IProgress<ScheduleRunProgress>` and a `CancellationToken`, and returns
the result. `ProductionScheduleService` depends on it; the page depends on the
service; nothing above the seam constructs a `SchedulingEngine`. The shipped
implementation is `CooperativeScheduleRunner`. A host with threads — a desktop
shell, a server, a browser where finding 1 has been fixed upstream — supplies
another one and nothing else changes.

**The restart is the slice.** `SchedulingEngine.Begin(context)` returns a
`MultiStartRun` that the caller advances one descent at a time. The engine's own
`RunCancellable` is now that object run to completion, so there is exactly one
multi-start loop in the codebase and the sliced run and the straight-through run
cannot drift — a test asserts the two produce the same schedule signature, penalty
and step count. The restart is the natural unit for three reasons: the state that
crosses one is three arrays and a penalty; restart 0 is always the pure rule order,
so a run stopped after any restart is a whole schedule rather than half of one; and
it is where the two numbers a person can read off — descents done, best objective
so far — actually change.

It is not a free choice of granularity. A descent is atomic, so the longest the tab
can be unresponsive is one descent. Measured on the seeded plant at 64 restarts:
**202 ms at worst, about 100 ms typically**, inside a seven-second run.

**How the thread is handed back is a decision in its own right.** The first
implementation used `await Task.Delay(1)`. It works, and then it does not: a nested
`setTimeout` is clamped to about 4 ms, and Chrome throttles a **hidden** tab's
timers to roughly one wake-up a second. Measured, with the tab in the background, a
64-restart run went from 25 % to 27 % **in ten seconds** — a seven-second run turned
into five minutes because the planner switched tabs. A task posted through a
`MessageChannel` port is not a timer and neither clamp applies, so `IScheduleYield`
has two implementations: `BrowserScheduleYield`, which awaits
`workplanYield.next()`, and `TimerScheduleYield`, which is the portable default and
the fallback if the interop call ever fails.

**The wording does not claim a worker.** The progress card reads *"The search runs
on this tab's own thread and hands it back between restarts, so the page stays
usable while it works"* — `Run_SameThreadNote`, in both languages. The class is
called `CooperativeScheduleRunner` and its first doc line is "This is not a worker."

**Accessibility.** The indicator is a `role="progressbar"` with `aria-valuemin`,
`aria-valuemax`, `aria-valuenow` and an `aria-valuetext` that spells the state out
("Restart 24 of 64 — best objective so far 175, lower is better"), labelled by the
visible sentence above it. The results region carries `aria-busy`, and the page's
existing polite status region announces the start of a run, its result, and its
cancellation. Colour is not the only channel — the bar's fill repeats what the
sentence already says. Nothing animates, so `prefers-reduced-motion` has nothing to
suppress. Focus is moved exactly once and only because the control that had it
disappeared: pressing **Cancel** unmounts the Cancel button, so focus is put on
**Generate** rather than dropped on `<body>`.

## Consequences

- ✅ The tab stays usable. Measured in Chrome on the seeded plant at 64 restarts ×
  3 000 steps: the progress bar re-rendered **64 times**, once per restart; a
  mid-run click on "Show as table" flipped `aria-expanded` to `true` and a mid-run
  edit to the Seed field bound — both while the run continued. Longest pause
  between renders **202 ms**.
- ✅ Cancel is real. Pressed at restart 22 of 64 the run stopped, the previous
  schedule and all four KPI cards were byte-identical afterwards, the status region
  said *"The run was cancelled. The previous schedule is unchanged."*, and no error
  banner appeared — a cancellation is an outcome, not a failure.
- ✅ A hidden tab still finishes. With the message-port yield, the same run
  completes in **7.0 s with the tab hidden**, against a stall (two restarts in ten
  seconds) with the timer yield.
- ➖ The slice is a descent, not a frame. At 64 restarts a descent is about 100 ms
  here; a 250-job instance would make it longer, and the page would stutter rather
  than freeze. If that becomes the complaint, the next slice is the local-search
  pass, which needs the same treatment one level down in `LocalSearch.Descend`.
- ➖ Yielding costs wall clock. 64 hand-backs are 64 trips through the browser's
  task queue plus 64 JS-interop round trips. On the seeded instance a run is about
  7 s hidden and 3.6 s visible; the honest reading is that a run is slightly slower
  than it would be if it froze the tab, which is a trade worth making and not a
  free lunch.
- ➖ `MultiStartRun` is new public surface on a library whose smallness is a stated
  value. It is one class and one `readonly record struct`, and it replaced the loop
  inside `SchedulingEngine.Search` rather than joining it.
- ➖ Threading is not closed forever, and the thing to watch is not .NET. It is
  whether `SQLitePCLRaw` ships a `browser-wasm` build of `e_sqlite3` compiled with
  `atomics` and `bulk-memory`. Until it does, finding 1 stands whatever the runtime
  does.
