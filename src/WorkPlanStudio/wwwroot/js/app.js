// Small JS interop surface used by the app.
// 1) blazorCulture   – remembers the chosen UI language.
// 2) workplanDb       – persists the in-browser SQLite database to localStorage.
// 3) workplanSettings – small key/value store for app settings (e.g. the optional
//                       AI assistant configuration). Values stay in this browser.

window.blazorCulture = {
    get: () => window.localStorage['BlazorCulture'],
    set: (value) => window.localStorage['BlazorCulture'] = value
};

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
    set: function (name, value) { window.localStorage.setItem(this.keyFor(name), value); }
};

window.workplanDb = {
    storageKey: 'workplanstudio.db',
    versionKey: 'workplanstudio.db.version',

    // Returns { data, version } or null when nothing has been stored yet.
    load: function () {
        const data = window.localStorage.getItem(this.storageKey);
        if (data === null) {
            return null;
        }
        const version = parseInt(window.localStorage.getItem(this.versionKey) || '0', 10);
        return { data: data, version: version };
    },

    save: function (base64, version) {
        window.localStorage.setItem(this.storageKey, base64);
        window.localStorage.setItem(this.versionKey, String(version));
    },

    clear: function () {
        window.localStorage.removeItem(this.storageKey);
        window.localStorage.removeItem(this.versionKey);
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
