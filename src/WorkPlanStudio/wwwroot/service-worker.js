// The development service worker: deliberately inert.
//
// It exists so that `navigator.serviceWorker.register` resolves during
// `dotnet run` instead of 404-ing, and so that the install prompt can be
// exercised locally - but it caches nothing. A caching worker on the dev server
// is how an afternoon disappears into a stale .wasm: you edit a component, the
// browser serves yesterday's build from the cache, and nothing you try to the
// source has any effect.
//
// The published build replaces this file wholesale with
// service-worker.published.js (see WorkPlanStudio.csproj), which is the one that
// actually caches. The browser suite therefore runs against this inert worker,
// which is what keeps the E2E and visual tests measuring the build under test.

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', () => self.clients.claim());
self.addEventListener('fetch', () => { });
