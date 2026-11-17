# 31. Pin script-src to the published page's own inline scripts

- **Status:** Accepted
- **Date:** 2026-11-17
- **Relates to:** [ADR 0030](0030-render-the-first-paint-at-build-time.md), whose
  build step made the published page final enough to hash

## Context

The page's Content-Security-Policy began `default-src 'none'`, but `script-src`
carried `'unsafe-inline'`, and the comment above it gave the reason: Blazor writes
a fingerprinted `<script type="importmap">` whose content changes on every build,
so a hash written into the source goes stale at once, and a static host cannot
mint a nonce. With `'unsafe-inline'`, any inline script or `onclick=` attribute
that reached the DOM would run — the policy kept other origins out, but not an
injection into this one.

That reasoning is about the *source*. Since ADR 0030 the deploy edits the
published `index.html` anyway, and after that edit the page does not change again
until the next deploy. A page that no longer changes can be hashed.

## Decision

After the last step that edits an inline script, the deploy runs
`.github/scripts/pin_inline_scripts.py`. It computes `'sha256-…'` over every inline
`<script>` the page actually carries — the import map, the first-paint script and
the `Blazor.start` call; three — and puts those hashes in `script-src` in place of
`'unsafe-inline'`. Nothing else in the policy moves.

Then the prerender tool runs again with `--verify`: the app is started in Chromium
against the pinned policy, on every prerendered route and in German, and a refused
script is a console error that fails the job. The same two steps run in the
Performance workflow, so Lighthouse measures the pinned page.

The script refuses to guess: no policy, no `script-src`, no `'unsafe-inline'` to
replace or no inline script at all are each an error.

## Consequences

- An injected inline script or event-handler attribute is refused by the browser
  on the published site. `script-src` is `'self' 'wasm-unsafe-eval'` plus three
  hashes.
- **The browser check caught the first version.** The comment that explains this
  policy in `index.html` quoted `<script type="importmap">` literally; a search
  that did not skip comments started there, hashed the comment's tail together
  with the real import map, and the browser refused the map. Blazor then never
  defined itself. The script now removes comments before it looks, and a test
  holds that case.
- `style-src` keeps `'unsafe-inline'`. Blazor sets element styles per render — the
  Gantt bar positions, among others — and those cannot be hashed in advance.
- The source `index.html` still carries `'unsafe-inline'`, so `dotnet run` and the
  browser suite run as before. The policy that ships is the pinned one; the policy
  that develops is not, and the comment in the source says so.
- An edit to an inline script after the pin would be refused by the browser. The
  verify step is after the pin for that reason, and the pin is after the last
  step that touches a script.
