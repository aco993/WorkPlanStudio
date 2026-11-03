// The published service worker: the whole application, cached for the next visit.
//
// Why this is worth having here specifically. The download is a .NET runtime plus
// SQLite relinked into WebAssembly - measured on the published tip: 95 requests,
// 7.4 MB transferred, 20.2 MB decoded. That is the cost of the architecture and
// it is not going away, but it is a cost the second visit has no reason to pay.
//
// The asset list is not written by hand. `ServiceWorkerAssetsManifest` in the
// .csproj makes the SDK emit service-worker-assets.js at publish time with every
// published file, its fingerprinted name and its integrity hash, so a renamed or
// added asset cannot be forgotten here - which is the failure mode of every
// hand-maintained precache list.
//
// What this worker does NOT do: it never serves a navigation from the network
// first. A Blazor WebAssembly application is a single index.html plus immutable
// fingerprinted assets, so cache-first is correct, and the version in the cache
// name changes on every publish - the old cache is deleted on activate, and the
// next load gets the new build whole rather than a mix of two.

self.importScripts('./service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'workplanstudio-offline-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// What is worth storing. The .wasm pattern is unanchored on purpose: in .NET 10
// the assemblies themselves ship as fingerprinted .wasm files, and `.dat` covers
// the ICU shard the app loads for German formatting.
const offlineAssetsInclude = [
    /\.wasm/, /\.dat$/, /\.blat$/, /\.pdb$/,
    /\.html$/, /\.js$/, /\.json$/, /\.webmanifest$/, /\.css$/,
    /\.woff2?$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.svg$/, /\.ico$/
];

// The worker must not cache itself, and appsettings.json configures the optional
// backend: a deployment that points the app at a server should be able to change
// that without waiting for a new build to roll the cache.
const offlineAssetsExclude = [
    /^service-worker\.js$/,
    /^appsettings\.json$/,
    /^appsettings\.sample\.json$/
];

// Everything resolves against the worker's own scope rather than the document,
// because the site is served from a repository sub-path on GitHub Pages: at
// /WorkPlanStudio/work-plans/1 a document-relative 'index.html' would resolve
// inside /work-plans/ and quietly cache nothing.
const base = self.registration.scope;

function shouldCache(url) {
    const relative = url.href.startsWith(base) ? url.href.slice(base.length) : url.pathname;
    return offlineAssetsInclude.some(pattern => pattern.test(relative))
        && !offlineAssetsExclude.some(pattern => pattern.test(relative));
}

async function onInstall() {
    const assets = self.assetsManifest.assets
        .filter(asset => shouldCache(new URL(asset.url, base)))
        // `integrity` is what makes this a precache rather than a hopeful copy:
        // a proxy that rewrites one of these files fails the request instead of
        // seeding the cache with it.
        .map(asset => new Request(new URL(asset.url, base), { integrity: asset.hash, cache: 'no-cache' }));

    await (await caches.open(cacheName)).addAll(assets);
    // Take over as soon as the new build is cached; the stale cache is dropped in
    // onActivate, so there is no window where half of each build is being served.
    await self.skipWaiting();
}

async function onActivate() {
    const names = await caches.keys();
    await Promise.all(names
        .filter(name => name.startsWith(cacheNamePrefix) && name !== cacheName)
        .map(name => caches.delete(name)));
    await self.clients.claim();
}

async function onFetch(event) {
    if (event.request.method === 'GET') {
        // Every route is the same document; the router does the rest. Without
        // this a deep link while offline is a browser error page.
        const isNavigation = event.request.mode === 'navigate';
        const request = isNavigation ? new Request(new URL('index.html', base)) : event.request;
        const cached = await (await caches.open(cacheName)).match(request);
        if (cached) { return cached; }

        // A navigation that is not in the cache yet (first visit, or a cache the
        // browser evicted) must still work offline-less rather than fail hard.
        if (isNavigation && !navigator.onLine) {
            const fallback = await caches.match(new URL('index.html', base));
            if (fallback) { return fallback; }
        }
    }

    return fetch(event.request);
}
