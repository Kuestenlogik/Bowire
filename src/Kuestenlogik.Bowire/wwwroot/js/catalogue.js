    // #136 — URL / service catalogue providers (local / http / consul / k8s / agent).
    //
    // On boot the workbench asks the host whether a catalogue provider
    // is configured and, if so, fetches its current snapshot. Catalogue-
    // sourced URLs get merged into `serverUrls` so the existing
    // discovery + sidebar surface picks them up; each merged URL is
    // tagged in `catalogueOriginByUrl` so a later phase can render an
    // origin chip distinguishing it from user-entered URLs.
    //
    // No-op for hosts that haven't called AddBowireCatalogue() — the
    // info endpoint returns { available: false } and the rest of the
    // workbench keeps its current "manual URL entry only" behaviour.

    // Module-scope state: which URLs came from the catalogue + the
    // current catalogue snapshot for surfaces that want richer metadata
    // (Settings → Sources tab, filter popup, &c).
    let catalogueInfo = { available: false };
    let catalogueEntries = [];
    let catalogueOriginByUrl = Object.create(null);

    async function fetchCatalogueInfo() {
        try {
            const resp = await fetch(`${config.prefix}/api/catalogue/info`);
            if (!resp.ok) return null;
            return await resp.json();
        } catch (_) {
            return null;
        }
    }

    async function fetchCatalogueEntries() {
        try {
            const resp = await fetch(`${config.prefix}/api/catalogue/entries`);
            if (!resp.ok) return null;
            return await resp.json();
        } catch (_) {
            return null;
        }
    }

    async function refreshCatalogueEntries() {
        try {
            const resp = await fetch(`${config.prefix}/api/catalogue/refresh`, { method: 'POST' });
            if (!resp.ok) return null;
            return await resp.json();
        } catch (_) {
            return null;
        }
    }

    // Merge catalogue URLs into serverUrls + tag their origin. The
    // existing prologue.js loader has already merged config + local-
    // storage URLs; this runs after that and adds whatever the
    // catalogue provider returned. URLs already present (entered by
    // the user or shipped by config) keep their existing origin
    // marker — catalogue entries don't overwrite local choices.
    function applyCatalogueToServerUrls(payload) {
        if (!payload || !Array.isArray(payload.entries)) return 0;
        catalogueEntries = payload.entries;
        var added = 0;
        for (var i = 0; i < payload.entries.length; i++) {
            var entry = payload.entries[i];
            if (!entry || !entry.url) continue;
            if (catalogueOriginByUrl[entry.url]) continue;
            catalogueOriginByUrl[entry.url] = {
                providerId: payload.providerId || null,
                providerName: payload.providerName || null,
                name: entry.name || null,
                protocols: entry.protocols || null,
                tags: entry.tags || null,
                schema: entry.schema || null,
            };
            if (serverUrls.indexOf(entry.url) === -1) {
                serverUrls.push(entry.url);
                if (typeof connectionStatuses !== 'undefined') {
                    connectionStatuses[entry.url] = 'disconnected';
                }
                added++;
            }
        }
        return added;
    }

    // Boot-time loader called from init.js after the workbench
    // mounts. Fire-and-forget — failure to reach the catalogue
    // endpoint doesn't block the rest of the boot.
    async function initialCatalogueLoad() {
        var info = await fetchCatalogueInfo();
        if (!info || !info.available) {
            catalogueInfo = info || { available: false };
            return;
        }
        catalogueInfo = info;
        var payload = await fetchCatalogueEntries();
        if (payload) {
            applyCatalogueToServerUrls(payload);
            // Re-render so the sidebar picks up the new URLs.
            if (typeof render === 'function') render();
        }
    }
