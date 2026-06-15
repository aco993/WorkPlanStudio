// Small JS interop surface used by the app.
// 1) blazorCulture  – remembers the chosen UI language.
// 2) workplanDb     – persists the in-browser SQLite database to localStorage.

window.blazorCulture = {
    get: () => window.localStorage['BlazorCulture'],
    set: (value) => window.localStorage['BlazorCulture'] = value
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
    }
};
