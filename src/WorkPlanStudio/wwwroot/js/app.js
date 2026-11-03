// Small JS interop surface used by the app.
// 1) blazorCulture   – remembers the chosen UI language.
// 2) workplanDb       – persists the in-browser SQLite database to localStorage.
// 3) workplanSettings – small key/value store for app settings (e.g. the optional
//                       AI assistant configuration). Values stay in this browser.

window.blazorCulture = {
    get: () => window.localStorage['BlazorCulture'],

    // The culture the .NET runtime boots in, decided before it starts.
    //
    // It is decided here rather than in Program.cs because the application no
    // longer carries the complete ICU table - it fetches one regional shard - and
    // Blazor refuses a culture change during start-up in that configuration. The
    // supported way round it is to hand the culture to Blazor.start(), which this
    // feeds (see index.html); by the time managed code runs, CurrentCulture is
    // already right and nothing has to change it.
    //
    // The two names are a whitelist, not a formality. This value comes out of
    // localStorage, which anyone can write, and a culture outside the shipped
    // shard would be a start-up failure rather than a wrong date.
    resolved: function () {
        const stored = window.localStorage['BlazorCulture'];
        return stored === 'de-DE' || stored === 'en-US' ? stored : 'en-US';
    }
};

// A language switch needs a reload, and a reload can be refused - the work-plan
// editor holds one while it asks about unsaved changes, and "keep editing" means
// the switch does not happen. So the switch is asked for in the address and
// remembered here, by the page that really did start in it: a reload that never
// happened leaves nothing behind. Writing it before the reload used to leave the
// app in three minds at once - storage said German, the buttons said English, the
// text stayed English - and then changed the language on some later visit the
// reader had cancelled.
(function rememberTheLanguageThisPageStartedIn() {
    const url = new URL(window.location.href);
    const asked = url.searchParams.get('culture');
    if (!asked || !/^[a-z]{2}-[A-Z]{2}$/.test(asked)) { return; }

    window.localStorage['BlazorCulture'] = asked;
    url.searchParams.delete('culture');
    window.history.replaceState(null, '', url.toString());
})();

// The boot screen and the framework's error bar are painted before the .NET
// runtime exists, so IStringLocalizer cannot reach them; they were the last
// untranslated strings in the app. The remembered language is in localStorage
// before anything renders, so the swap happens here, from `data-de` attributes
// that keep both languages next to each other in index.html.
(function localiseStaticChrome() {
    const culture = window.localStorage['BlazorCulture'] || '';
    if (!culture.startsWith('de')) { return; }
    document.documentElement.lang = 'de';
    document.querySelectorAll('[data-de]').forEach(el => el.textContent = el.getAttribute('data-de'));
})();

// The progress ring measures one thing: how much of the application has been
// downloaded. It reaches 100 % and then the page sits unchanged for seconds while
// the .NET runtime starts and relinked SQLite initialises - the slowest part of a
// cold start is the part with no feedback at all, which reads as a hang.
//
// Blazor publishes its own progress as a custom property on <html>, so the moment
// it reads 100 % is observable without taking over the loader (autostart="false"
// plus a hand-rolled Blazor.start() would be a lot of surface to own for one
// sentence of text). A warm cache is the awkward case: the loader can finish
// before it ever publishes a number, and polling for a value that never arrives
// would leave the note claiming to download through the whole runtime start. So
// an empty reading that stays empty counts as done too.
(function nameTheBootPhases() {
    const note = document.querySelector('.boot-note');
    if (!note) { return; }

    const german = (window.localStorage['BlazorCulture'] || '').startsWith('de');
    const runtime = note.getAttribute(german ? 'data-phase-runtime-de' : 'data-phase-runtime');
    if (!runtime) { return; }

    const startedAt = performance.now();
    const timer = setInterval(() => {
        // The node goes away when Blazor renders the app over the boot screen.
        if (!note.isConnected) { clearInterval(timer); return; }

        const raw = getComputedStyle(document.documentElement)
            .getPropertyValue('--blazor-load-percentage').trim();
        const downloaded = parseFloat(raw) >= 100;
        const neverCounted = raw === '' && performance.now() - startedAt > 1500;
        if (!downloaded && !neverCounted) { return; }

        clearInterval(timer);
        note.textContent = runtime;
    }, 120);
})();

// Register the service worker, and register it late.
//
// Two details that are easy to get wrong here and silent when wrong:
//
// `document.baseURI`, not a bare relative path. register() resolves against the
// *document* URL rather than <base href>, so on a deep link such as
// /WorkPlanStudio/work-plans/1 a bare 'service-worker.js' asks for
// /WorkPlanStudio/work-plans/service-worker.js - a 404, and a worker that only
// ever installs for visitors who arrived at the root.
//
// On `load`, not immediately. The worker precaches the whole published asset
// list; starting that while the runtime is still downloading puts the precache
// in competition with the boot it is meant to speed up. By `load` the
// application has what it needs and the connection is free.
(function registerServiceWorker() {
    if (!('serviceWorker' in navigator)) { return; }

    window.addEventListener('load', () => {
        navigator.serviceWorker
            .register(new URL('service-worker.js', document.baseURI))
            // A failed registration is not a failed application: the site works
            // exactly as it did before, it just pays full price again next time.
            // Worth a console line, not worth anything the reader has to see.
            .catch(error => console.warn('Service worker registration failed:', error));
    });
})();

window.documentLanguage = {
    set: (value) => document.documentElement.lang = value
};

// Dialog focus. `close` deliberately takes no element: it is called from the
// component's render loop *after* the dialog has been removed from the DOM (a
// dialog closed by Cancel or Save never goes through the ✕ path), and an
// ElementReference to a detached node is no longer resolvable. A stack of
// { dialog, previous } pairs keeps the trap removable and the return target known
// without needing the element back from .NET.
window.workplanModal = {
    stack: [],
    open: function (dialog) {
        this.stack.push({ dialog: dialog, previous: document.activeElement });
        const target = dialog.querySelector('input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), a[href]');
        (target || dialog).focus();
        window.workplanFocusTrap.install(dialog);
    },
    close: function () {
        const entry = this.stack.pop();
        if (!entry) { return; }
        window.workplanFocusTrap.remove(entry.dialog);
        if (entry.previous && document.contains(entry.previous)) {
            entry.previous.focus();
        }
    }
};

window.workplanSettings = {
    keyFor: (name) => 'workplanstudio.settings.' + name,
    get: function (name) { return window.localStorage.getItem(this.keyFor(name)); },
    set: function (name, value) { window.localStorage.setItem(this.keyFor(name), value); },
    // A real removal, not an empty string written over the entry: "forget this
    // key" should leave nothing behind for the next reader to find.
    remove: function (name) { window.localStorage.removeItem(this.keyFor(name)); }
};

window.workplanDb = {
    storageKey: 'workplanstudio.db',
    // Only read, never written: the pre-atomic layout kept the version here.
    legacyVersionKey: 'workplanstudio.db.version',

    // Returns { data, version } or null when nothing has been stored yet.
    //
    // The payload and its version used to be two separate setItem calls. If the
    // second one hit the quota the data key held a current payload while the
    // version key was gone, load() reported version 0, and the app parked the
    // user on "unsupported schema" for a database that was perfectly fine - with
    // reset, i.e. total data loss, as the only way out. One JSON value under one
    // key makes the write atomic; the legacy two-key layout is still read so an
    // existing browser keeps its data.
    load: function () {
        const raw = window.localStorage.getItem(this.storageKey);
        if (raw === null) {
            return null;
        }
        if (raw.length > 0 && raw[0] === '{') {
            try {
                const parsed = JSON.parse(raw);
                if (parsed && typeof parsed.data === 'string' && Number.isInteger(parsed.version)) {
                    // A payload written before revisions existed reads as 0, which is
                    // what an existing browser should see: the first write stamps 1.
                    return { data: parsed.data, version: parsed.version, revision: parsed.revision || 0 };
                }
            } catch {
                // Fall through: treat it as the legacy Base64 layout.
            }
        }
        const version = parseInt(window.localStorage.getItem(this.legacyVersionKey) || '0', 10);
        return { data: raw, version: version, revision: 0 };
    },

    // What is in storage right now, without decoding the payload.
    currentRevision: function () {
        const raw = window.localStorage.getItem(this.storageKey);
        if (raw === null || raw.length === 0 || raw[0] !== '{') { return 0; }
        try {
            return JSON.parse(raw).revision || 0;
        } catch {
            return 0;
        }
    },

    // Reports the outcome instead of throwing, so a full quota becomes a typed
    // result the UI can explain rather than an opaque JSException.
    // expectedRevision is the revision this write is based on; -1 writes
    // unconditionally, which is what replacing the whole database means.
    //
    // Reading, comparing and writing happen in one synchronous block on purpose.
    // Every tab holds the whole database in memory and writes the whole of it
    // back, so without this the last writer won and the loser was never told:
    // two tabs quietly deleted each other's *saved* work, in both directions,
    // while the losing tab went on showing the change that was already gone.
    save: function (base64, version, expectedRevision) {
        const current = this.currentRevision();
        if (expectedRevision >= 0 && current !== expectedRevision) {
            return {
                ok: false,
                reason: 'stale',
                message: 'storage is at revision ' + current + ', this write was based on ' + expectedRevision
            };
        }

        const revision = current + 1;
        const payload = JSON.stringify({ data: base64, version: version, revision: revision });
        try {
            window.localStorage.setItem(this.storageKey, payload);
        } catch (error) {
            const quota = error && (error.name === 'QuotaExceededError'
                || error.name === 'NS_ERROR_DOM_QUOTA_REACHED'
                || error.code === 22 || error.code === 1014);
            return { ok: false, reason: quota ? 'quota' : 'error', message: String(error && error.message || error) };
        }

        // Read back before declaring success: a storage layer that silently
        // truncates must not be reported as a durable save.
        const written = window.localStorage.getItem(this.storageKey);
        if (written !== payload) {
            return { ok: false, reason: 'error', message: 'the stored payload did not match what was written' };
        }

        window.localStorage.removeItem(this.legacyVersionKey);
        return { ok: true, revision: revision };
    },

    // The browser offers this for nothing: a `storage` event fires in every OTHER
    // tab of the origin whenever one of them writes. Listening turns "your save
    // was refused, reload" from a surprise into something the reader was already
    // told about.
    watch: function (handler) {
        window.addEventListener('storage', (event) => {
            if (event.key === this.storageKey) {
                handler.invokeMethodAsync('DatabaseChangedElsewhere');
            }
        });
    },

    clear: function () {
        window.localStorage.removeItem(this.storageKey);
        window.localStorage.removeItem(this.legacyVersionKey);
    },

    export: function (base64, version) {
        const payload = JSON.stringify({ version: version, data: base64 }, null, 2);
        const blob = new Blob([payload], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = 'workplanstudio-browser-database-v' + version + '.json';
        link.click();
        URL.revokeObjectURL(url);
    },

    // The other half of export, which the app shipped without: a file the user
    // downloaded was previously readable by nothing at all.
    pickImport: function () {
        return new Promise((resolve) => {
            const input = document.createElement('input');
            input.type = 'file';
            input.accept = 'application/json,.json';
            input.addEventListener('cancel', () => resolve(null));
            input.addEventListener('change', async () => {
                const file = input.files && input.files[0];
                if (!file) { resolve(null); return; }
                try {
                    const parsed = JSON.parse(await file.text());
                    resolve(parsed && typeof parsed.data === 'string' && Number.isInteger(parsed.version)
                        ? { data: parsed.data, version: parsed.version }
                        : { data: '', version: -1 });
                } catch {
                    resolve({ data: '', version: -1 });
                }
            });
            input.click();
        });
    }
};

// Closes a <details> menu from Blazor (after a choice, or on Escape) and returns
// focus to its summary so keyboard users are not dropped on the page body.
window.workplanMenu = {
    close: function (details) {
        if (!details) { return; }
        details.removeAttribute('open');
        const summary = details.querySelector('summary');
        if (summary) { summary.focus(); }
    }
};

// Theme: the user's choice is 'system' (default), 'light' or 'dark'. The
// effective theme is stamped on <html data-theme> before Blazor boots so the
// first paint is already right, and re-stamped when the OS preference changes
// while in system mode.
window.workplanTheme = {
    key: 'workplanstudio.settings.theme',
    media: window.matchMedia('(prefers-color-scheme: dark)'),
    get: function () {
        const stored = window.localStorage.getItem(this.key);
        return stored === 'light' || stored === 'dark' ? stored : 'system';
    },
    set: function (mode) {
        if (mode === 'system') { window.localStorage.removeItem(this.key); }
        else { window.localStorage.setItem(this.key, mode); }
        this.apply();
        return this.effective();
    },
    effective: function () {
        const mode = this.get();
        return mode === 'system' ? (this.media.matches ? 'dark' : 'light') : mode;
    },
    apply: function () {
        const effective = this.effective();
        document.documentElement.setAttribute('data-theme', effective);
        document.documentElement.setAttribute('data-theme-mode', this.get());
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) { meta.setAttribute('content', effective === 'dark' ? '#0f1117' : '#4f46e5'); }
    }
};
window.workplanTheme.apply();
window.workplanTheme.media.addEventListener('change', () => window.workplanTheme.apply());

// Keeps Tab inside an open dialog: the last focusable wraps to the first and
// back. Installed by workplanModal.open, removed by workplanModal.close.
window.workplanFocusTrap = {
    handlers: new WeakMap(),
    selector: 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    install: function (dialog) {
        const handler = (event) => {
            if (event.key !== 'Tab') { return; }
            const items = Array.from(dialog.querySelectorAll(this.selector)).filter(el => el.offsetParent !== null);
            if (items.length === 0) { return; }
            const first = items[0], last = items[items.length - 1];
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        };
        dialog.addEventListener('keydown', handler);
        this.handlers.set(dialog, handler);
    },
    remove: function (dialog) {
        const handler = this.handlers.get(dialog);
        if (handler) { dialog.removeEventListener('keydown', handler); this.handlers.delete(dialog); }
    }
};

// The skip link: Blazor intercepts same-document links, so the jump to <main>
// is explicit - focus it (tabindex="-1") and scroll it into view.
// The chat thread is a bounded scroll box; a new answer is scrolled into view
// so the newest turn is what the reader sees.
window.workplanChat = {
    scrollToEnd: function () {
        const thread = document.querySelector('.chat-thread');
        if (thread) { thread.scrollTop = thread.scrollHeight; }
    }
};

// The mobile navigation drawer. Focus follows it in; the layout sends focus back
// to the hamburger on close.
window.workplanNav = {
    focusFirst: function () {
        const link = document.querySelector('.app-shell.nav-open .sidebar .nav-link');
        if (link) { link.focus(); }
    }
};

// The Gantt chart's arrow-key navigation. The browser scrolls a scroll container
// with the arrow keys, which fights the focus move the component is making, so the
// default is cancelled here rather than with Blazor's @onkeydown:preventDefault —
// that attribute is fixed per render, not per key, so it would also swallow Tab and
// trap the reader inside the chart. Enter and Space are deliberately left alone:
// they are the buttons' own activation.
window.workplanGantt = {
    keys: ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'],
    install: function (chart) {
        if (!chart || chart.dataset.keysBound === 'true') { return; }
        chart.dataset.keysBound = 'true';
        chart.addEventListener('keydown', (event) => {
            if (this.keys.includes(event.key)) { event.preventDefault(); }
        });
    }
};

// Focus by id, for the Gantt chart's roving tabindex: the focused bar changes with
// the arrow keys, and the bars are rendered inside a @foreach where an
// ElementReference per bar would have to be kept in step with the list by hand.
window.workplanFocus = {
    byId: function (id) {
        const element = document.getElementById(id);
        if (element) { element.focus(); }
    }
};

window.workplanSkip = {
    toContent: function () {
        const main = document.getElementById('main');
        if (!main) { return; }
        main.focus({ preventScroll: true });
        main.scrollIntoView({ block: 'start' });
    }
};

// One turn of the browser's task queue, for the scheduling run.
//
// The run holds the tab's only thread and gives it back between multi-start
// restarts. What it gives it back *with* decides how much that is worth: a nested
// setTimeout is clamped to about 4 ms, and Chrome throttles a hidden tab's timers
// to roughly one wake-up a second - measured on this page as a run that crawled
// from 25 % to 27 % in ten seconds after the tab went to the background. A message
// posted to a port is a task and not a timer, so neither clamp applies, and the
// page still gets to paint between turns (which a microtask would not allow).
window.workplanYield = {
    next: function () {
        return new Promise((resolve) => {
            const channel = new MessageChannel();
            channel.port1.onmessage = () => { channel.port1.close(); resolve(); };
            channel.port2.postMessage(0);
        });
    }
};
