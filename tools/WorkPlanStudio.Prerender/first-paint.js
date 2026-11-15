// Inlined into the published index.html by WorkPlanStudio.Prerender, right
// after app.js, so it runs before the first paint and needs no request of its
// own. See docs/adr/0030.
//
// A first visit used to see the boot screen for the seconds it takes to
// download and start the .NET runtime and open the database: Lighthouse put the
// largest contentful paint at 7.8 s. The page those seconds lead to is known at
// build time - a first visit always starts from the same demo plant - so the
// build renders it once, in a real browser, and this shows that rendering at
// once and hands over to the live application when it is ready.
//
// Three rules keep the picture honest:
//   - Only a first visit sees it. A browser that already holds a database holds
//     its own data, and a snapshot of the demo plant would be a picture of
//     somebody else's numbers.
//   - It is inert until the hand-over: a picture of a button must not look as if
//     it took the click. A status line outside the inert part says what is
//     still loading.
//   - It is replaced in one step, when the live page has nothing left to load,
//     so the reader never sees picture, loader, page.
(function firstPaint() {
    const holder = document.getElementById('prerendered');
    if (!holder) { return; }

    const app = document.getElementById('app');
    const discard = () => {
        holder.remove();
        document.querySelectorAll('template[data-prerender-route]').forEach((t) => t.remove());
    };

    let storedDatabase = false;
    try {
        storedDatabase = window.localStorage.getItem('workplanstudio.db') !== null
            || window.localStorage.getItem('workplanstudio.db.version') !== null;
    } catch {
        // Storage unavailable: the app will say so itself; show it the plain way.
        storedDatabase = true;
    }
    if (storedDatabase) { discard(); return; }

    const basePath = new URL(document.baseURI).pathname;
    const path = window.location.pathname;
    const route = (path.startsWith(basePath) ? path.slice(basePath.length) : '').replace(/\/+$/, '');
    const culture = window.blazorCulture ? window.blazorCulture.resolved() : 'en-US';
    const template = Array.from(document.querySelectorAll('template[data-prerender-route]'))
        .find((t) => t.dataset.prerenderRoute === route && t.dataset.prerenderCulture === culture);
    if (!template) { discard(); return; }

    const status = holder.querySelector('.prerender-status');
    if (status && culture === 'de-DE' && status.dataset.de) { status.textContent = status.dataset.de; }
    holder.querySelector('.prerender-snapshot').appendChild(template.content.cloneNode(true));
    holder.hidden = false;
    app.hidden = true;

    // Live and settled: Blazor has replaced the static boot screen, and nothing
    // on the page is still loading. The schedule page runs the scheduler after
    // its first render; handing over at the first render would show its spinner
    // where the picture already showed the chart.
    const settled = () => app.firstElementChild !== null
        && !app.querySelector('.boot, .spinner, .loading-row, progress, [role=progressbar]');

    let observer = null;
    const handOver = () => {
        if (observer) { observer.disconnect(); }
        app.hidden = false;
        discard();
    };
    observer = new MutationObserver(() => { if (settled()) { handOver(); } });
    observer.observe(app, { childList: true, subtree: true, attributes: true });

    // A start that never settles - a storage failure, a crash - must not be
    // hidden behind a picture of a working application. After this long the
    // live page is shown whatever state it is in.
    window.setTimeout(handOver, 30000);
})();
