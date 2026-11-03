# 27. Pay the cold start once, and say what it is doing while it happens

- **Status:** Accepted
- **Date:** 2026-11-03
- **Relates to:** [ADR 0019](0019-off-thread-scheduling.md), which measured why the
  search cannot leave the one thread — the same constraint that makes the download
  what it is

## Context

The architecture this project exists to demonstrate — a relational database
compiled into the page — has one bill, and it is the first load. Measured against
the published site: **95 requests, 7.4 MB transferred, 20.2 MB decoded**, and
Lighthouse scores the page load 36. The README states this plainly and the
Performance workflow reports it rather than gating it.

Three things about that bill were worse than they had to be.

**It was paid again on every visit.** There was no service worker, so a reader who
came back the next day fetched the whole runtime again. For a portfolio piece this
is the common case, not the rare one: the same person opens the link, closes it,
and opens it again when they have time.

**It included globalization data for scripts the app cannot select.** The project
set `BlazorWebAssemblyLoadAllGlobalizationData=true` to get correct German
formatting, which ships the complete ICU table — 455 KB of the download. The UI
offers exactly two cultures and both are in the EFIGS shard.

**The slowest part of it was silent.** The progress ring measures the download. It
reached 100 % and then the page sat unchanged for seconds while the runtime started
and SQLite initialised, and then the static boot screen was replaced by a bare
28 px spinner on an empty page while the database was opened. The part of start-up
with no feedback at all was the part that looked like a hang.

## Decision

**A service worker caches the published build; the SDK writes the asset list.**
`ServiceWorkerAssetsManifest` emits every published file with its fingerprinted
name and integrity hash, so a renamed or added asset cannot be left out — the
failure mode of every hand-maintained precache list. Registration happens on
`load`, not during boot, so the precache does not compete with the download it
exists to make unnecessary; and it resolves against `document.baseURI`, because
`register()` resolves against the document, which on a repository sub-path would
otherwise ask for the worker inside `/work-plans/`. Development gets an inert
worker that caches nothing, substituted at publish — so the browser suite always
measures the build under test, never a cached one.

**One ICU shard instead of the complete table.** Measured from the publish output:
`icudt_EFIGS.dat` is 537.9 KB raw, 214.9 KB gzipped, 140.6 KB Brotli, against
455 KB transferred for the full table. The loader picks the shard at run time from
the browser's own languages, so a reader whose browser asks for something outside
EFIGS gets the CJK or no-CJK shard instead — this narrows the download, not who the
app works for. Verified in the browser: German renders `175,1 h` and `Di 2.6`, and
the working-time page its sixteen federal states, with the shard loaded.

**The culture is handed to the runtime before it boots.** This is what the shard
costs, and it is the whole reason the full table was there. `Program.cs` used to
read the remembered language over JS interop and assign
`DefaultThreadCurrentCulture` — a culture change *during* start-up, which Blazor
refuses outright when the complete table is absent. The app does not get past the
boot screen; the error names the property and recommends turning it back on. The
supported route is `Blazor.start({ applicationCulture })` with `autostart="false"`,
which is what index.html now does, reading the remembered culture from
localStorage against a two-name whitelist. By the time managed code runs,
`CurrentCulture` is already right and nothing has to change it. This fits the
design: a language switch has always been a forced reload (see
`Components/CultureSelector.razor`), so the culture was only ever decided once per
page load anyway.

**The boot screen names the phase instead of repeating the number.** Download,
then "starting the .NET runtime" once the ring reads 100 %, then "opening the
in-browser database" — the last one rendered by `App.razor`, because by then the
static boot screen has been replaced by the application. The first two are
pre-runtime strings and cannot come from `IStringLocalizer`; they follow the
existing `data-de` convention in index.html. The bare spinner is gone: the
rendered state wears the same mark and the same note slot, so the hand-over is
invisible.

## Consequences

- A second visit is served from the cache. The first is unchanged apart from the
  shard: still 7.4 MB, still Lighthouse 36, and this ADR does not claim otherwise.
- About 240 KB off the first load on GitHub Pages, which gzips on the fly.
- The published site grows by roughly 1.5 MB of files — all three shards ship and a
  given visitor requests one. A static host is paid for in bytes transferred, so
  that is the right side to spend on.
- `worker-src 'self'` and `manifest-src 'self'` had to be added to the policy. Both
  fall back to `default-src 'none'`, which refuses them with no console error that
  names the policy: a registration that never resolves and a browser that never
  offers to install. The policy is otherwise unchanged.
- One more inline script, for `Blazor.start`. The policy already permits inline
  script for Blazor's own fingerprinted importmap, so this costs no new relaxation.
- The app is installable, and has a manifest, icons and a maskable icon. Those and
  the link-preview card come from one generator, `tools/brand-assets/render.py`, so
  the mark has one definition rather than five exported files.
- A cached build is only replaced when the asset manifest version changes, which it
  does on every publish. The stale cache is deleted on activate, so a reader is
  never served half of one build and half of another.

## Addendum, 2026-11-03: the precache failed in production and nowhere else

The worker was verified against a local published build - 106 entries cached,
then 93 of 93 requests served from the cache on a repeat visit, deep link
included. It still did not cache a single file on GitHub Pages.

The deploy edits `index.html` after publish, twice: `<base href>` for the
repository sub-path, and the absolute link-preview URLs. The SDK had already
recorded a SHA-256 for it. `cache.addAll` is all-or-nothing, so that one stale
hash rejected the install, the registration was discarded, and what remained was
**an empty cache carrying the correct version name** - the signature of a worker
that ran and then threw away its own work. Measured on the published site:

    fetch(new Request('index.html', { integrity: <hash from the manifest> }))
    -> TypeError: Failed to fetch

Nothing in CI could have caught this. The failure lives in the gap between
`dotnet publish` and `upload-pages-artifact`, which no test runs through, and
the browser symptom is indistinguishable from a sandbox refusing to register a
worker. It was found by checking the claim against the deployed site rather than
against the build that produced it.

`.github/scripts/reseal_sw_assets.py` recomputes the hash of the files the
deploy says it edited, and **fails the deploy if any other file moved**. The
asymmetry is deliberate: re-hashing the whole manifest would make it agree with
whatever is on disk, and a manifest that cannot disagree with the disk has
stopped being an integrity manifest.
