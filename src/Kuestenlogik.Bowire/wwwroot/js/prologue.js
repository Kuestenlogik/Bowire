/**
 * Bowire — Interactive gRPC Browser
 * Pure vanilla JS, no framework dependencies.
 */
(function () {
    'use strict';

    const config = window.__BOWIRE_CONFIG__ || {
        title: 'Bowire',
        description: 'Multi-protocol API workbench',
        prefix: '/bowire',
        theme: 'dark',
        showInternalServices: false
    };

    // ---- UI Mode ----
    // Determined once at boot from the host config — never changes at
    // runtime. Controls whether the URL bar is visible and editable.
    //
    //   embedded:   No config URLs, no lock → plugin discovery via
    //               IServiceProvider, URL bar hidden.
    //   standalone: Config has URLs or user needs to enter them.
    //     - editable: URLs can be added/removed
    //     - locked:   URLs are read-only (central hosted test tool)
    // embeddedMode is the authoritative signal from the host
    // (BrowserUiHost sets BowireMode.Standalone for the CLI; embedded
    // hosts that MapBowire() inside their own app set embeddedMode=true).
    // Earlier code fell back to "do we have URLs?" to guess the mode,
    // which made the standalone CLI launched without --url look like an
    // embedded host → the URL bar was hidden → no way to add a URL (#81).
    // Trust the flag.
    var uiMode = config.embeddedMode
        ? 'embedded'
        : (config.lockServerUrl ? 'standalone-locked' : 'standalone');

    // ---- Server URL State (standalone mode) ----
    // Multi-URL: a list of discovery URLs, each fetched independently. The
    // legacy single-URL config (config.serverUrl) and old localStorage entries
    // are migrated into the list on first load.
    const SERVER_URLS_KEY = 'bowire_server_urls';

    function loadInitialServerUrls() {
        // Locked mode: trust whatever the host shipped via config.serverUrls
        if (config.lockServerUrl) {
            if (Array.isArray(config.serverUrls) && config.serverUrls.length > 0) return config.serverUrls.slice();
            if (config.serverUrl) return [config.serverUrl];
            return [];
        }

        // Editable mode: start from localStorage (user choices survive
        // reloads), then MERGE in any config.serverUrls that aren't
        // already in the list. This ensures host-configured URLs
        // (e.g. mqtt://broker or odata/$metadata) are always present
        // even if localStorage has an older/empty set.
        var urls = [];
        try {
            var raw = localStorage.getItem(wsKey(SERVER_URLS_KEY));
            if (raw) {
                var parsed = JSON.parse(raw);
                if (Array.isArray(parsed)) urls = parsed;
            }
        } catch { /* ignore corrupt storage */ }

        // Merge config URLs that aren't already in the user's list
        var configUrls = Array.isArray(config.serverUrls) ? config.serverUrls : [];
        if (config.serverUrl && configUrls.indexOf(config.serverUrl) === -1) {
            configUrls = [config.serverUrl].concat(configUrls);
        }
        for (var ci = 0; ci < configUrls.length; ci++) {
            if (urls.indexOf(configUrls[ci]) === -1) {
                urls.push(configUrls[ci]);
            }
        }

        return urls;
    }

    let serverUrls = loadInitialServerUrls();
    // Per-URL connection status: { [url]: 'disconnected' | 'connecting' | 'connected' | 'error' }
    let connectionStatuses = {};
    serverUrls.forEach(function (u) { connectionStatuses[u] = 'disconnected'; });

    function persistServerUrls() {
        if (config.lockServerUrl) return;
        try { localStorage.setItem(wsKey(SERVER_URLS_KEY), JSON.stringify(serverUrls)); markSaved('URLs'); }
        catch (e) { markSaveFailed('URLs', e); }
    }

    // Backwards-compat aliases for code paths that haven't been refactored yet
    function getPrimaryServerUrl() { return serverUrls.length > 0 ? serverUrls[0] : ''; }

    // ---- Protocol State ----
    let protocols = [];          // Available protocols [{id, name, icon}]
    // /api/plugins/health snapshot. Populated lazily by settings.js
    // when the Plugins tab opens — the workbench surfaces failed
    // plugin loads (ContractMajorMismatch, ManifestMissing, …) as a
    // banner so operators don't have to curl the endpoint by hand to
    // find out why a sibling plugin silently disappeared after a tool
    // update. Empty array before the first fetch / between fetches.
    let pluginHealth = [];
    // Multi-select filter: Set of protocol ids that should remain visible
    // in the service list. Empty set = no filter = show everything. The
    // filter lives in a popup next to the search bar (renderProtocolFilter
    // in render-sidebar.js). Persisted as a JSON array in localStorage.
    const PROTOCOL_FILTER_KEY = 'bowire_protocol_filter';
    let protocolFilter = (function () {
        try {
            var raw = localStorage.getItem(PROTOCOL_FILTER_KEY);
            if (!raw) return new Set();
            var arr = JSON.parse(raw);
            return new Set(Array.isArray(arr) ? arr : []);
        } catch { return new Set(); }
    })();
    function persistProtocolFilter() {
        try {
            localStorage.setItem(PROTOCOL_FILTER_KEY, JSON.stringify(Array.from(protocolFilter)));
        } catch {}
    }
    // Derived single-protocol selector, kept in sync with protocolFilter.
    // Many code paths (API invocations, recording, landing) use this as a
    // fallback protocol id when selectedService.source isn't set. Rule:
    // `selectedProtocol` is meaningful only when the filter unambiguously
    // points at one protocol; otherwise null.
    let selectedProtocol = null;
    function refreshSelectedProtocolFromFilter() {
        if (protocolFilter.size === 1) {
            selectedProtocol = protocolFilter.values().next().value;
        } else {
            selectedProtocol = null;
        }
    }
    refreshSelectedProtocolFromFilter();
    // Method-type filter: Set of method type strings (Unary,
    // ServerStreaming, ClientStreaming, Duplex) that should remain
    // visible. Empty = no filter = show everything. Persisted to
    // localStorage alongside the protocol filter.
    const METHOD_TYPE_FILTER_KEY = 'bowire_method_type_filter';
    let methodTypeFilter = (function () {
        try {
            var raw = localStorage.getItem(METHOD_TYPE_FILTER_KEY);
            if (!raw) return new Set();
            var arr = JSON.parse(raw);
            return new Set(Array.isArray(arr) ? arr : []);
        } catch { return new Set(); }
    })();
    function persistMethodTypeFilter() {
        try {
            localStorage.setItem(METHOD_TYPE_FILTER_KEY, JSON.stringify(Array.from(methodTypeFilter)));
        } catch {}
    }
    // URL filter: Set of originUrl strings the user has narrowed the
    // service list to. Empty = no filter = show services from every
    // discovery URL. Useful in multi-URL setups (think gateway + admin
    // backend + a staging clone) where the same protocol shows up at
    // several origins and the user wants to focus on just one. The
    // dropdown only renders this section when serverUrls.length > 1,
    // because a one-URL filter is trivially the empty-or-everything
    // toggle.
    const URL_FILTER_KEY = 'bowire_url_filter';
    let urlFilter = (function () {
        try {
            var raw = localStorage.getItem(URL_FILTER_KEY);
            if (!raw) return new Set();
            var arr = JSON.parse(raw);
            return new Set(Array.isArray(arr) ? arr : []);
        } catch { return new Set(); }
    })();
    function persistUrlFilter() {
        try {
            localStorage.setItem(URL_FILTER_KEY, JSON.stringify(Array.from(urlFilter)));
        } catch {}
    }

    // Controls the visibility of the filter popup (anchored next to the
    // search bar). Closed by an outside-click handler when open.
    let protocolFilterOpen = false;

    // ---- Command palette / global search ----
    // The topbar has a central search input that both live-filters the
    // sidebar list (via the legacy `searchQuery` state below) AND opens a
    // suggestions dropdown for quick navigation. The dropdown is a mix of
    // methods-that-match, "apply as name filter", protocol-filter actions,
    // and environment switches.
    //
    // Name filter is a distinct, persistent filter surfaced as a chip in
    // the sidebar. `searchQuery` is the transient input value (clears on
    // Esc), `nameFilter` is the promoted-to-chip version (clears only via
    // the chip × or "Clear all"). Both are AND-ed in getFilteredServices.
    // ---- Theme preference (auto / dark / light) ----
    // Three-state cycle: auto → dark → light → auto.
    // 'auto' follows the OS `prefers-color-scheme` media query live via
    // a matchMedia listener wired in init.js. 'dark' / 'light' pin an
    // explicit value and ignore the OS setting.
    //
    // The DOM data-theme attribute always carries the effective theme
    // ('dark' or 'light'), never 'auto' — CSS selectors stay simple.
    const THEME_PREF_KEY = 'bowire_theme_pref';
    let themePreference = (function () {
        try {
            var v = localStorage.getItem(THEME_PREF_KEY);
            return (v === 'dark' || v === 'light' || v === 'auto') ? v : 'auto';
        } catch { return 'auto'; }
    })();
    function getSystemTheme() {
        try {
            return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        } catch {
            return 'dark';
        }
    }
    function getEffectiveTheme() {
        return themePreference === 'auto' ? getSystemTheme() : themePreference;
    }
    function applyTheme() {
        document.documentElement.setAttribute('data-theme', getEffectiveTheme());
    }
    function setThemePreference(pref) {
        themePreference = (pref === 'dark' || pref === 'light') ? pref : 'auto';
        try { localStorage.setItem(THEME_PREF_KEY, themePreference); } catch {}
        applyTheme();
    }

    const NAME_FILTER_KEY = 'bowire_name_filter';
    let nameFilter = (function () {
        try { return localStorage.getItem(NAME_FILTER_KEY) || ''; }
        catch { return ''; }
    })();
    function setNameFilter(v) {
        nameFilter = v || '';
        try {
            if (nameFilter) localStorage.setItem(NAME_FILTER_KEY, nameFilter);
            else localStorage.removeItem(NAME_FILTER_KEY);
        } catch {}
    }
    let searchSuggestionsOpen = false;
    let searchSuggestionIndex = 0;
    // Cache of the last built suggestion list so keyboard nav can resolve
    // the selected index without rebuilding on every keydown.
    let searchSuggestionCache = [];

    // ---- State ----
    let services = [];
    let selectedMethod = null;
    let selectedService = null;
    // Expanded-state of service groups in the sidebar. Persisted to
    // localStorage so the user's layout survives page reloads — tiny
    // quality-of-life fix for people who repeatedly open the same few
    // service groups. persistExpandedServices() is called from every
    // toggle site and from the auto-expand path in fetchServices.
    const EXPANDED_SERVICES_KEY = 'bowire_expanded_services';
    let expandedServices = (function () {
        try {
            var raw = localStorage.getItem(EXPANDED_SERVICES_KEY);
            if (!raw) return new Set();
            var arr = JSON.parse(raw);
            return new Set(Array.isArray(arr) ? arr : []);
        } catch { return new Set(); }
    })();
    function persistExpandedServices() {
        try {
            localStorage.setItem(EXPANDED_SERVICES_KEY, JSON.stringify(Array.from(expandedServices)));
        } catch {}
    }
    let searchQuery = '';

    // ---- Discovery State ----
    // Drives the empty-state landing page in render-main.js so the user
    // gets a context-sensitive view depending on what's currently in
    // flight or has failed. fetchServices flips isLoadingServices around
    // its work and writes per-URL error messages to discoveryErrors;
    // landing.js' detectLandingState reads both to pick a state.
    let isLoadingServices = false;
    let discoveryErrors = {};   // { [url]: error message }
    // AI drawer (#90). Was a tab in the response pane; now a workbench-
    // wide side drawer that persists across method/service switches and
    // coexists with the response view. Open state mirrored to
    // localStorage so the user's preference is sticky across reloads.
    let aiDrawerOpen = false;
    try { aiDrawerOpen = localStorage.getItem('bowire_ai_drawer_open') === '1'; } catch { /* ignore */ }
    // Security drawer (#111). Peer of the AI drawer, lives on the
    // right edge of the body with its own topbar toggle. Hosts the
    // threat-model + template-suggest surfaces that used to live
    // inside the AI drawer; the AI drawer stays focused on hints +
    // chat. Open state mirrored to localStorage so the user's
    // preference is sticky across reloads.
    let securityDrawerOpen = false;
    try { securityDrawerOpen = localStorage.getItem('bowire_security_drawer_open') === '1'; } catch { /* ignore */ }

    // #129 — topbar overflow menu (Theme + About + Settings). Session-
    // only; not persisted to localStorage so a reload starts with the
    // menu closed. Closes on Esc / outside click via init.js handlers.
    let topbarOverflowOpen = false;
    // #116 workspace switcher menu — open/closed.
    let workspaceMenuOpen = false;
    // #116 — selected workspace in the rail-mode detail view.
    let workspacesSelectedId = null;
    // #152 — selected URL in the Sources rail-mode detail view.
    let sourcesSelectedUrl = null;
    // #152 v3 — multi-select state on the Sources list (reuses the
    // #143 selection model: Set of urls + anchor index).
    let sourcesSelected = new Set();
    let sourcesSelectionAnchor = null;

    // #152 v2 — per-URL header bag. Keys are URLs (the full string
    // the operator typed, including the 'rest@' / 'graphql@' prefix
    // if present); values are { header: value } maps. Applied to
    // every request invoked against the matching URL by api.js's
    // request-header builder.
    //
    // Storage key wsKey-wrapped so each workspace owns its own set
    // — Auth tokens for 'staging' shouldn't bleed into 'production'.
    let urlHeaders = {};
    try {
        var rawUrlHeaders = localStorage.getItem(wsKey('bowire_url_headers'));
        if (rawUrlHeaders) urlHeaders = JSON.parse(rawUrlHeaders) || {};
    } catch { /* corrupt; reset */ }
    function persistUrlHeaders() {
        try { localStorage.setItem(wsKey('bowire_url_headers'), JSON.stringify(urlHeaders)); markSaved('URL headers'); }
        catch (e) { markSaveFailed('URL headers', e); }
    }
    function getUrlHeaders(url) {
        if (!url) return {};
        return Object.assign({}, urlHeaders[url] || {});
    }
    function setUrlHeader(url, key, value) {
        if (!url || !key) return;
        if (!urlHeaders[url]) urlHeaders[url] = {};
        urlHeaders[url][key] = value;
        persistUrlHeaders();
    }
    function deleteUrlHeader(url, key) {
        if (!url || !key) return;
        if (!urlHeaders[url]) return;
        delete urlHeaders[url][key];
        if (Object.keys(urlHeaders[url]).length === 0) delete urlHeaders[url];
        persistUrlHeaders();
    }

    // #152 v3 — per-URL metadata. Sister bag to urlHeaders. Carries
    // operator-visible identity for a URL (custom name shown in
    // dropdowns, color dot in the source list) without rewriting
    // the serverUrls[] array shape — entries there stay raw URL
    // strings so existing code paths keep working.
    let urlMeta = {};
    try {
        var rawUrlMeta = localStorage.getItem(wsKey('bowire_url_meta'));
        if (rawUrlMeta) urlMeta = JSON.parse(rawUrlMeta) || {};
    } catch { /* corrupt; reset */ }
    function persistUrlMeta() {
        try { localStorage.setItem(wsKey('bowire_url_meta'), JSON.stringify(urlMeta)); markSaved('URL meta'); }
        catch (e) { markSaveFailed('URL meta', e); }
    }
    function getUrlMeta(url) {
        if (!url) return {};
        return Object.assign({}, urlMeta[url] || {});
    }
    function setUrlMeta(url, patch) {
        if (!url || !patch) return;
        if (!urlMeta[url]) urlMeta[url] = {};
        Object.assign(urlMeta[url], patch);
        persistUrlMeta();
    }
    function removeUrlMeta(url) {
        if (!url) return;
        if (urlMeta[url]) { delete urlMeta[url]; persistUrlMeta(); }
    }

    // Extract a protocol hint from the URL's prefix syntax
    // (rest@..., graphql@..., grpc@...). Returns null when no
    // prefix is present — discovery still finds the protocol.
    function getUrlProtocolHint(url) {
        if (!url) return null;
        var m = String(url).match(/^([a-z]+)@/i);
        return m ? m[1].toLowerCase() : null;
    }

    // Stable normaliser for service-count lookups. Some code paths
    // store originUrl as the raw user input (prefix included), some
    // as the resolved URL minus prefix. Compare both forms so the
    // count in the Sources sidebar matches reality.
    function _stripUrlPrefix(url) {
        if (!url) return url;
        return String(url).replace(/^[a-z]+@/i, '');
    }
    function urlMatchesService(url, svc) {
        if (!svc || !svc.originUrl || !url) return false;
        if (svc.originUrl === url) return true;
        return _stripUrlPrefix(svc.originUrl) === _stripUrlPrefix(url);
    }

    // #143 Phase 2 — Trash-store per list. localStorage-persisted
    // so a missed Undo toast isn't a permanent loss. Each entry
    // carries the original item + a deletedAt timestamp. The
    // sidebar's 'Recently deleted' section reads from these.
    // Auto-purge after 30 days happens on startup below.
    let recordingsTrash = [];
    let collectionsTrash = [];
    try {
        var rawRTrash = localStorage.getItem(wsKey('bowire_recordings_trash'));
        if (rawRTrash) recordingsTrash = JSON.parse(rawRTrash) || [];
        var rawCTrash = localStorage.getItem(wsKey('bowire_collections_trash'));
        if (rawCTrash) collectionsTrash = JSON.parse(rawCTrash) || [];
    } catch { /* ignore */ }
    // Drop anything older than 30 days.
    (function purgeOldTrash() {
        var cutoff = Date.now() - 30 * 24 * 60 * 60 * 1000;
        recordingsTrash = recordingsTrash.filter(function (t) { return t.deletedAt > cutoff; });
        collectionsTrash = collectionsTrash.filter(function (t) { return t.deletedAt > cutoff; });
    })();
    function persistRecordingsTrash() {
        try { localStorage.setItem(wsKey('bowire_recordings_trash'), JSON.stringify(recordingsTrash)); } catch { /* ignore */ }
    }
    function persistCollectionsTrash() {
        try { localStorage.setItem(wsKey('bowire_collections_trash'), JSON.stringify(collectionsTrash)); } catch { /* ignore */ }
    }
    // Trash-section open/closed per sidebar — session-only.
    let recordingsTrashOpen = false;
    let collectionsTrashOpen = false;

    // #125 Phase 2 — vars-autocomplete dropdown state. Single
    // floating popover; only one active across the whole document
    // because at most one input/textarea has the caret.
    let varsACState = null;   // { target, anchorPos, query, suggestions, selectedIdx } | null

    // #143 Phase 3 — multi-select state per list. Session-only so a
    // refresh clears it. Anchor tracks the last single-click for
    // shift-range expansion.
    let recordingsSelected = new Set();
    let recordingsSelectionAnchor = null;
    let collectionsSelected = new Set();
    let collectionsSelectionAnchor = null;
    // Generic helper — applies the click modifier rules to a Set.
    // Returns true if the modifier-aware click should be treated as
    // a selection (ctrl/meta/shift); false means it's a plain click
    // that the caller wants to fall through to single-row select.
    function applyListSelectionClick(set, anchorIdx, ids, idx, e) {
        if (e && (e.shiftKey)) {
            // Shift+click: extend the range from anchor (or 0 if no
            // anchor yet) to this idx. Plain set semantics — we add
            // every id in the range without clearing existing.
            var from = anchorIdx == null ? idx : Math.min(anchorIdx, idx);
            var to = anchorIdx == null ? idx : Math.max(anchorIdx, idx);
            for (var i = from; i <= to && i < ids.length; i++) set.add(ids[i]);
            return true;
        }
        if (e && (e.ctrlKey || e.metaKey)) {
            // Ctrl/Cmd+click: toggle single row in set.
            if (set.has(ids[idx])) set.delete(ids[idx]);
            else set.add(ids[idx]);
            return true;
        }
        return false;
    }

    // #133 — activity-rail mode. Persisted per-workspace once #116
    // lands; for now scoped globally to localStorage. Phase 1 only
    // wires the Discover mode to the existing sidebar content; the
    // rest of the icons render with a "coming soon" tooltip until
    // their dedicated sidebar templates land in Phase 2-4.
    // #139 — Home mode is the first-launch default. Returning users
    // restore their last-active mode from localStorage.
    let railMode = 'home';
    try { railMode = localStorage.getItem('bowire_rail_mode') || 'home'; } catch { /* ignore */ }
    // #133 Phase 2 — Mocks mode selection. Session-only;
    // re-derived from the live mocksList every render so a stopped
    // mock automatically deselects.
    let mockSelectedId = null;

    // #135 — request/response split orientation. 'horizontal' (today's
    // default) renders the two panes side-by-side; 'vertical' stacks
    // them (request on top, response below). Persisted globally for
    // now; per-tab persistence lands in a follow-up. Validate against
    // the known values so a stale localStorage entry from a previous
    // version doesn't break rendering.
    let splitMode = 'horizontal';
    try {
        var storedSplit = localStorage.getItem('bowire_split_mode');
        if (storedSplit === 'vertical' || storedSplit === 'horizontal') {
            splitMode = storedSplit;
        }
    } catch { /* ignore */ }

    // #116 — Workspaces Phase 1. UI surface + naming only; actual
    // state isolation (per-workspace URLs / envs / collections /
    // recordings / AI config) lands in Phase 2. For now the
    // workspace acts as a labelled folder for the operator's
    // context — switching swaps the visible name + the save-pill
    // label but the underlying state stays global. Once Phase 2
    // wires the storage prefix into every persist fn, switching
    // becomes structural.
    let workspaces = [];
    let activeWorkspaceId = null;
    try {
        var rawWs = localStorage.getItem('bowire_workspaces');
        if (rawWs) {
            var parsed = JSON.parse(rawWs);
            if (Array.isArray(parsed)) workspaces = parsed;
        }
        activeWorkspaceId = localStorage.getItem('bowire_active_workspace') || null;
    } catch { /* ignore */ }
    // First-run seed: every install gets a Personal workspace so
    // the switcher has at least one entry.
    if (workspaces.length === 0) {
        workspaces.push({
            id: 'personal',
            name: 'Personal',
            color: '#6366f1',
            createdAt: Date.now(),
            lastOpenedAt: Date.now(),
            // #146 — Personal defaults to 'include every shared
            // environment automatically' so the new user landing in
            // Personal sees every env they create without curating.
            // Operators creating a second workspace decide
            // explicitly via the rail-mode toggle.
            includeAllEnvironments: true,
            includedEnvironmentIds: [],
        });
        activeWorkspaceId = 'personal';
    }
    if (!activeWorkspaceId || !workspaces.find(function (w) { return w.id === activeWorkspaceId; })) {
        activeWorkspaceId = workspaces[0].id;
    }
    // #116 Phase 2 — wraps a flat storage key into a workspace-
    // scoped one ('bowire_recordings' → 'bowire_ws_<id>_recordings').
    // Global UI prefs (theme, sidebar width, drawer state, etc.) do
    // NOT route through this — they intentionally stay shared. The
    // per-workspace store sites call wsKey at every localStorage
    // setItem / getItem so a workspace switch picks up a fresh slate.
    function wsKey(baseKey) {
        if (!activeWorkspaceId) return baseKey;
        return 'bowire_ws_' + activeWorkspaceId + '_'
            + String(baseKey).replace(/^bowire_/, '');
    }

    // #146 — one-shot migration of envs from per-workspace bucketed
    // storage to a single shared store + per-workspace inclusion
    // lists. Walks every bowire_ws_<id>_environments key, collects
    // into the shared list (de-duped by name; later wins), populates
    // each workspace's includedEnvironmentIds from the bucket they
    // originated in. Also flattens bowire_ws_<id>_global_vars into
    // the shared global-vars store (merging keys; later wins).
    // Marker bowire_envs_shared_migrated_v1 prevents re-running.
    (function migrateEnvsToSharedStore() {
        try {
            if (localStorage.getItem('bowire_envs_shared_migrated_v1') === '1') return;
            if (!Array.isArray(workspaces) || workspaces.length === 0) return;
            var shared = [];
            var byName = new Map(); // name → id in shared list
            var sharedGlobals = {};
            try {
                var existingSharedRaw = localStorage.getItem('bowire_environments_shared');
                if (existingSharedRaw) {
                    var parsed = JSON.parse(existingSharedRaw);
                    if (Array.isArray(parsed)) {
                        for (var si = 0; si < parsed.length; si++) {
                            shared.push(parsed[si]);
                            byName.set(parsed[si].name, parsed[si].id);
                        }
                    }
                }
            } catch { /* ignore */ }
            for (var wi = 0; wi < workspaces.length; wi++) {
                var w = workspaces[wi];
                var bucketKey = 'bowire_ws_' + w.id + '_environments';
                var bucket = [];
                try {
                    var raw = localStorage.getItem(bucketKey);
                    if (raw) {
                        var arr = JSON.parse(raw);
                        if (Array.isArray(arr)) bucket = arr;
                    }
                } catch { /* ignore */ }
                w.includedEnvironmentIds = w.includedEnvironmentIds || [];
                for (var ei = 0; ei < bucket.length; ei++) {
                    var env = bucket[ei];
                    if (!env || !env.id) continue;
                    if (byName.has(env.name)) {
                        // 'later wins' — overwrite the shared entry
                        // with this workspace's copy. We keep the
                        // SHARED id (so other workspaces that already
                        // included the original keep referencing the
                        // same record).
                        var sharedId = byName.get(env.name);
                        for (var ti = 0; ti < shared.length; ti++) {
                            if (shared[ti].id === sharedId) { shared[ti] = Object.assign({}, env, { id: sharedId }); break; }
                        }
                        if (w.includedEnvironmentIds.indexOf(sharedId) === -1) {
                            w.includedEnvironmentIds.push(sharedId);
                        }
                    } else {
                        shared.push(env);
                        byName.set(env.name, env.id);
                        w.includedEnvironmentIds.push(env.id);
                    }
                }
                // Merge per-workspace globals into shared globals.
                try {
                    var gKey = 'bowire_ws_' + w.id + '_global_vars';
                    var rawG = localStorage.getItem(gKey);
                    if (rawG) {
                        var gObj = JSON.parse(rawG);
                        if (gObj && typeof gObj === 'object') {
                            for (var gk in gObj) {
                                if (Object.prototype.hasOwnProperty.call(gObj, gk)) sharedGlobals[gk] = gObj[gk];
                            }
                        }
                    }
                } catch { /* ignore */ }
            }
            try {
                localStorage.setItem('bowire_environments_shared', JSON.stringify(shared));
                if (Object.keys(sharedGlobals).length > 0) {
                    var existingG = {};
                    try {
                        var rawExist = localStorage.getItem('bowire_global_vars_shared');
                        if (rawExist) existingG = JSON.parse(rawExist) || {};
                    } catch { /* ignore */ }
                    var mergedG = Object.assign({}, existingG, sharedGlobals);
                    localStorage.setItem('bowire_global_vars_shared', JSON.stringify(mergedG));
                }
                localStorage.setItem('bowire_envs_shared_migrated_v1', '1');
                // persist the updated workspace meta with inclusion lists
                localStorage.setItem('bowire_workspaces', JSON.stringify(workspaces));
            } catch { /* localStorage write may fail — skip */ }
        } catch { /* never crash boot over a migration */ }
    })();

    // #116 Phase 2 — one-shot migration. On first boot with the
    // workspace-prefixed storage layout, copy the existing flat
    // keys into the active workspace's namespace so the operator's
    // pre-Phase-2 state shows up where they expect. Doesn't delete
    // the originals — a downgrade still reads them. Marked done in
    // localStorage so we don't churn on every load.
    (function migrateToWorkspacedStorage() {
        try {
            if (localStorage.getItem('bowire_ws_v2_migrated') === '1') return;
            var keys = [
                'bowire_server_urls', 'bowire_history', 'bowire_favorites',
                'bowire_environments', 'bowire_active_env', 'bowire_global_vars',
                'bowire_method_scripts', 'bowire_recent_methods',
                'bowire_recordings', 'bowire_collections',
                'bowire_recordings_trash', 'bowire_collections_trash',
                'bowire_request_tabs', 'bowire_flows',
            ];
            for (var i = 0; i < keys.length; i++) {
                var src = keys[i];
                var dst = wsKey(src);
                if (src === dst) continue;
                var val = localStorage.getItem(src);
                if (val == null) continue;
                if (localStorage.getItem(dst) != null) continue; // never overwrite
                localStorage.setItem(dst, val);
            }
            localStorage.setItem('bowire_ws_v2_migrated', '1');
        } catch { /* localStorage may be disabled — skip */ }
    })();

    function persistWorkspaces() {
        try {
            localStorage.setItem('bowire_workspaces', JSON.stringify(workspaces));
            if (activeWorkspaceId) localStorage.setItem('bowire_active_workspace', activeWorkspaceId);
            markSaved('workspace');
        } catch (e) { markSaveFailed('workspace', e); }
    }
    function activeWorkspace() {
        return workspaces.find(function (w) { return w.id === activeWorkspaceId; }) || workspaces[0];
    }
    function createWorkspace(name, color) {
        var id = 'ws_' + Math.random().toString(36).slice(2, 10);
        var ws = {
            id: id,
            name: name || ('Workspace ' + (workspaces.length + 1)),
            color: color || '#6366f1',
            createdAt: Date.now(),
            lastOpenedAt: Date.now(),
            // #146 — new workspaces start CURATED (per-env opt-in)
            // so the operator decides explicitly which shared envs
            // belong here. Personal is the one default-catchall.
            includeAllEnvironments: false,
            includedEnvironmentIds: [],
        };
        workspaces.push(ws);
        activeWorkspaceId = id;
        persistWorkspaces();
        return ws;
    }
    function switchWorkspace(id) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return;
        activeWorkspaceId = id;
        ws.lastOpenedAt = Date.now();
        persistWorkspaces();
        // #116 Phase 2 — every per-workspace store routes through
        // wsKey(...) keyed off activeWorkspaceId. A full reload
        // is the cleanest way to swap recordings / collections /
        // env / URLs / tabs / scripts to the new namespace; an
        // in-place re-fetch would have to walk every load-fn,
        // re-render every cached widget, and clear the URL bar's
        // discovery state — significantly more surface to get
        // wrong. Wallclock cost is one paint.
        try { window.location.reload(); } catch { /* embedded host */ }
    }
    // #146 — workspace-level switch: include all shared environments
    // automatically. When on, no per-env curation needed. Flipping
    // #144 Phase 1.7+ — recording storage mode per workspace.
    //   'both'         (default) → write to localStorage AND user-folder via /api/recordings.
    //   'browser-only'           → write to localStorage ONLY; disk sync skipped entirely.
    //   'disk-only'    (Phase 1.8) → no localStorage cache; manifest-only fetch on init,
    //                                step bodies lazy-fetched on demand.
    function getRecordingStorageMode(ws) {
        var target = ws || (typeof activeWorkspace === 'function' ? activeWorkspace() : null);
        if (!target) return 'both';
        var m = target.recordingStorageMode;
        if (m === 'browser-only' || m === 'disk-only') return m;
        return 'both';
    }
    function setWorkspaceRecordingStorageMode(id, mode) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return;
        ws.recordingStorageMode = (mode === 'browser-only' || mode === 'disk-only') ? mode : 'both';
        persistWorkspaces();
    }

    // the toggle persists immediately so a workspace switch reads
    // the new state from disk.
    function setWorkspaceIncludeAllEnvs(id, value) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return;
        ws.includeAllEnvironments = !!value;
        persistWorkspaces();
    }

    function renameWorkspace(id, name) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return;
        ws.name = name;
        persistWorkspaces();
    }
    function deleteWorkspace(id) {
        // Personal is the seed — keep at least one workspace; refuse
        // to delete the last one. Otherwise drop the entry and
        // re-point active to whatever's left.
        if (workspaces.length <= 1) return false;
        var idx = workspaces.findIndex(function (w) { return w.id === id; });
        if (idx < 0) return false;
        workspaces.splice(idx, 1);
        if (activeWorkspaceId === id) activeWorkspaceId = workspaces[0].id;
        persistWorkspaces();
        return true;
    }

    // #124 v2 — module-level slot for the omnibox palette DOM. The
    // topbar's renderTopbar builds the palette every render cycle
    // (with all the up-to-date suggestion bindings); body-render
    // appends it inside a modal overlay when searchSuggestionsOpen
    // is true. Storing the reference here keeps the build in one
    // place without forcing renderTopbar to know about modal
    // mounting.
    let _omniboxPaletteForModal = null;

    // #127 — auto-save tracking. Every persist fn calls markSaved
    // after a successful write so the statusbar can show a small
    // "Saved" pill that fades after 2 s. State {kind, at, target}:
    //  kind = 'idle' | 'saved' | 'failed'
    //  at = timestamp of the last transition (drives the fade)
    //  target = which slot was written ('tabs', 'urls', ...)
    let saveState = { kind: 'idle', at: 0, target: '' };
    let saveStateClearTimer = null;

    // #127 — folder-open capability. Set once at boot from
    // /api/workspace/can-open-folder. Embedded hosts get { available:
    // false } back so the save-pill click is gated off (the host is
    // typically a production server, spawning a desktop process there
    // makes zero sense). Standalone tool: true.
    let canOpenWorkspaceFolder = false;

    // #127 — Cmd+S Force-Flush. Calls every top-level persist*()
    // function in sequence so a Cmd+S press writes EVERY known slot,
    // not just whatever's about to autosave. Wraps each call in
    // try/catch so a single broken slot doesn't abort the rest. All
    // persist*() functions live in this IIFE (declared in their own
    // fragment file) and are reachable via function hoisting; the
    // per-fragment try/catch protects the flush from a single broken
    // call breaking the whole sweep.
    function flushAllPersists() {
        var ok = 0, fail = 0;
        function tryRun(label, fn) {
            try { fn(); ok++; }
            catch (e) { console.warn('[bowire] flush ' + label + ' failed', e); fail++; }
        }
        tryRun('serverUrls',      function () { persistServerUrls(); });
        tryRun('protocolFilter',  function () { persistProtocolFilter(); });
        tryRun('methodTypeFilter',function () { persistMethodTypeFilter(); });
        tryRun('urlFilter',       function () { persistUrlFilter(); });
        tryRun('expandedServices',function () { persistExpandedServices(); });
        tryRun('urlHeaders',      function () { persistUrlHeaders(); });
        tryRun('urlMeta',         function () { persistUrlMeta(); });
        tryRun('recordingsTrash', function () { persistRecordingsTrash(); });
        tryRun('collectionsTrash',function () { persistCollectionsTrash(); });
        tryRun('workspaces',      function () { persistWorkspaces(); });
        tryRun('requestTabs',     function () { persistRequestTabs(); });
        tryRun('collections',     function () { persistCollections(); });
        tryRun('recordings',      function () { persistRecordings(); });
        tryRun('flows',           function () { persistFlows(); });
        tryRun('benchmarks',      function () { persistBenchmarks(); });
        markSaved(fail > 0 ? ('flush (' + ok + ' ok, ' + fail + ' err)') : 'all');
    }

    // ---- #115 App-Version-Marker + Cosmetic-State-Reset ----
    //
    // Pragmatic alternative to the bowire_v2_*-prefix-sweep originally
    // listed in #115. Instead of rewriting every storage key on a
    // major version bump (risky: a per-key migration that loses data
    // is worse than the staleness it tries to prevent), we keep ONE
    // marker that records the major version of the install and reset
    // only the cosmetic UI state when the major changes.
    //
    // Data keys (workspaces, environments, recordings, collections,
    // history, favorites, server-urls, request-tabs, …) carry their
    // own per-key migrations with markers like bowire_envs_shared_
    // migrated_v1. Those don't need a prefix rewrite; they already
    // handle their own schema evolution.
    //
    // Persistent user settings (theme, watch-interval, vars-dollar-
    // snooze) also stay — the user chose those deliberately, a major
    // version bump shouldn't nuke them.
    //
    // What WE reset is pure UI state: which drawer was open, what
    // the split mode was, what rail mode the user was last in,
    // sidebar width, filter chips. Stale values here can point at
    // surfaces that changed shape across the major bump. A reset
    // means the user sees the new shell with new defaults.
    var APP_VERSION_KEY = 'bowire_app_version';
    var COSMETIC_KEYS_RESET_ON_BUMP = [
        'bowire_ai_drawer_open',
        'bowire_security_drawer_open',
        'bowire_split_mode',
        'bowire_rail_mode',
        'bowire_expanded_services',
        'bowire_sidebar_width',
        'bowire_sidebar_view',
        'bowire_protocol_filter',
        'bowire_method_type_filter',
        'bowire_url_filter',
        'bowire_name_filter',
        'bowire_source_mode',
        'bowire_transcoding_mode'
    ];

    function _appMajorVersion() {
        var v = (config && config.version) || '';
        // Match leading MAJOR.MINOR from strings like "2.0.1-rc.3" or
        // "0.9.4" so a patch release doesn't trigger the cosmetic
        // reset — only minor + major bumps do.
        var m = String(v).match(/^(\d+\.\d+)/);
        return m ? m[1] : '';
    }

    function checkAppVersionMarker() {
        var current = _appMajorVersion();
        if (!current) return; // version unknown — don't gamble
        var stored;
        try { stored = localStorage.getItem(APP_VERSION_KEY); }
        catch { return; }
        if (stored === current) return; // happy path: same version

        // First boot (stored===null) doesn't toast — there was no
        // previous install to warn about. Just record the marker.
        if (stored !== null) {
            for (var i = 0; i < COSMETIC_KEYS_RESET_ON_BUMP.length; i++) {
                try { localStorage.removeItem(COSMETIC_KEYS_RESET_ON_BUMP[i]); }
                catch { /* ignore */ }
            }
            // Toast deferred so it lands AFTER the main render — the
            // toast container is appended to document.body by toast()
            // but morphdom replaces #bowire-app on every render, and
            // popping the toast mid-boot can race.
            setTimeout(function () {
                if (typeof toast === 'function') {
                    toast(
                        'Bowire upgraded from v' + stored + ' to v' + current
                        + ' — workbench layout reset to defaults. Your data '
                        + '(workspaces, recordings, environments, collections, …) is untouched.',
                        'info',
                        { duration: 8000 }
                    );
                }
            }, 500);
        }
        try { localStorage.setItem(APP_VERSION_KEY, current); }
        catch { /* quota — best-effort */ }
    }

    function markSaved(target) {
        saveState = { kind: 'saved', at: Date.now(), target: target || 'state' };
        if (saveStateClearTimer) clearTimeout(saveStateClearTimer);
        saveStateClearTimer = setTimeout(function () {
            saveState = { kind: 'idle', at: 0, target: '' };
            saveStateClearTimer = null;
            try { render(); } catch { /* harmless */ }
        }, 2000);
        try { render(); } catch { /* harmless */ }
    }
    function markSaveFailed(target, error) {
        saveState = { kind: 'failed', at: Date.now(), target: target || 'state', error: error };
        if (saveStateClearTimer) clearTimeout(saveStateClearTimer);
        // Failures linger longer (4 s) so the operator catches them.
        saveStateClearTimer = setTimeout(function () {
            saveState = { kind: 'idle', at: 0, target: '' };
            saveStateClearTimer = null;
            try { render(); } catch { /* harmless */ }
        }, 4000);
        try { render(); } catch { /* harmless */ }
    }
    let activeRequestTab = 'body';
    // Sub-tab within the Body tab. For GraphQL methods the Body tab
    // composes from three surfaces (Query / Variables form / Selection
    // set picker) — historically all three rendered stacked, which
    // ate vertical space and forced scrolling. The sub-tab strip
    // gates rendering to one surface at a time (#85). Source-of-truth
    // sub-tab wins as the default ("query" for GraphQL).
    let activeBodySubTab = 'query';
    let activeResponseTab = 'response';
    let isExecuting = false;
    let sseSource = null;

    // Active-jobs tracking. Set of "service::method" keys that are
    // currently executing (unary in-flight, streaming, channel open).
    // Used to show a running indicator on methods in the sidebar and
    // favorites — so the user can see at a glance which methods are
    // still in flight even after switching to a different method.
    let activeJobs = new Set();
    function markJobActive(svcName, methodName) {
        activeJobs.add(svcName + '::' + methodName);
    }
    function markJobDone(svcName, methodName) {
        activeJobs.delete(svcName + '::' + methodName);
    }
    function isJobActive(svcName, methodName) {
        return activeJobs.has(svcName + '::' + methodName);
    }
    let responseData = null;
    let responseError = null;
    let streamMessages = [];
    let statusInfo = null;
    let sidebarCollapsed = false;
    let requestMessages = ['']; // Array of JSON strings, one per message
    // ---- Streaming UI state (Wireshark-style: list of messages + detail pane) ----
    let streamSelectedIndex = null;     // null = follow latest; otherwise index into streamMessages
    let streamAutoScroll = true;        // Auto-scroll list + auto-select latest while live
    let streamDetailMaximized = false;  // Hide list, show detail full-height
    let streamListSizePct = 45;         // Splitter position: list pane height as % (10..90)
    let streamFilterQuery = '';         // Substring or key:value filter applied to the visible stream list
    let streamFilterPanelOpen = false;  // Whether the collapsible filter panel above the list is expanded
    let widgetPaneMaximized = false;    // When true, the split-pane widget slot (map, future viewers) fills the viewport
    // Phase 3.1 — multi-select selection set for the Streaming-Frames
    // pane. Ctrl/Cmd-click toggles membership, Shift-click extends a
    // range from the last anchor, plain click replaces with a single-
    // member set. Empty set = "no manual selection" (the detail pane
    // still follows `streamSelectedIndex` for the active item).
    //
    // The set holds frame `id` strings minted by api.js/protocols.js
    // (`${service}/${method}#${index}`) so a snapshot is portable across
    // tabs and recordings. Snapshots dispatched on
    // `bowire:frames-selection-changed` carry the full N-member array
    // exactly once per logical change — never one event per delta.
    let streamSelectedIds = new Set();
    let streamSelectionAnchorIdx = null;   // last single-click index for shift-range
    let responseViewMode = 'json';      // 'json' (pretty/syntax) or 'tree' (collapsible nodes)
    let showAllHistory = false; // Whether history tab shows all methods or filtered

    // ---- Form Input State ----
    let requestInputMode = 'form';  // 'form' or 'json'
    let formValues = {};  // { fieldName: value } — current method only

    // Per-method request state cache. Keyed by `${service}::${method}`,
    // holds a snapshot of everything the user has typed so far:
    // formValues, requestMessages, requestInputMode. Swapping methods
    // saves the current snapshot and loads the target's, so a ping-pong
    // between two methods doesn't clobber work in either one.
    //
    // In-memory only (no localStorage) by design — request bodies can
    // contain auth tokens and other secrets we don't want on disk.
    // Cleared implicitly on page reload.
    let methodStates = {};
    function methodStateKey(svcName, methodName) {
        return svcName + '::' + methodName;
    }
    function saveCurrentMethodState() {
        if (!selectedService || !selectedMethod) return;
        var key = methodStateKey(selectedService.name, selectedMethod.name);
        // Deep-clone so later edits to formValues don't mutate the
        // saved snapshot. requestMessages is an array of strings →
        // shallow slice is enough.
        methodStates[key] = {
            formValues: JSON.parse(JSON.stringify(formValues)),
            requestMessages: requestMessages.slice(),
            requestInputMode: requestInputMode
        };
    }
    function loadMethodStateFor(svcName, methodName, fallbackMethodInput) {
        var key = methodStateKey(svcName, methodName);
        var saved = methodStates[key];
        if (saved) {
            formValues = JSON.parse(JSON.stringify(saved.formValues));
            requestMessages = saved.requestMessages.slice();
            requestInputMode = saved.requestInputMode;
            return true;
        }
        // No saved state — reset to defaults. Caller provides the
        // method's inputType so we can re-generate the default JSON
        // payload if needed.
        formValues = {};
        if (fallbackMethodInput !== undefined) {
            requestMessages = [generateDefaultJson(fallbackMethodInput, 0)];
            requestInputMode = hasDeepNesting(fallbackMethodInput, 0) ? 'json' : 'form';
        }
        return false;
    }
    function clearMethodState(svcName, methodName) {
        var key = methodStateKey(svcName, methodName);
        delete methodStates[key];
    }

    // ---- Per-method Scripts (pre-request / post-response) ----
    // Persisted in localStorage so scripts survive page reloads — unlike
    // request bodies, scripts are user-authored automation code that is
    // painful to lose.
    const METHOD_SCRIPTS_KEY = 'bowire_method_scripts';

    function loadAllMethodScripts() {
        try { return JSON.parse(localStorage.getItem(wsKey(METHOD_SCRIPTS_KEY)) || '{}'); }
        catch { return {}; }
    }

    function saveAllMethodScripts(map) {
        try { localStorage.setItem(wsKey(METHOD_SCRIPTS_KEY), JSON.stringify(map)); } catch {}
    }

    function getMethodScripts(svcName, methodName) {
        var key = methodStateKey(svcName, methodName);
        var all = loadAllMethodScripts();
        var entry = all[key];
        return {
            preScript: (entry && entry.preScript) || '',
            postScript: (entry && entry.postScript) || ''
        };
    }

    function setMethodScript(svcName, methodName, field, value) {
        var key = methodStateKey(svcName, methodName);
        var all = loadAllMethodScripts();
        if (!all[key]) all[key] = { preScript: '', postScript: '' };
        all[key][field] = value || '';
        saveAllMethodScripts(all);
    }

    // Recently-used methods (MRU). Drives the empty-query state of the
    // command palette so users get a quick re-jump back to the methods
    // they touched last. Stored as { service, method } pairs in newest-
    // first order; capped at MAX_RECENT_METHODS. Persisted because the
    // identifiers themselves are not sensitive — unlike the per-method
    // request bodies in methodStates.
    const RECENT_METHODS_KEY = 'bowire_recent_methods';
    const MAX_RECENT_METHODS = 10;
    function getRecentMethods() {
        try {
            var raw = localStorage.getItem(wsKey(RECENT_METHODS_KEY));
            if (!raw) return [];
            var arr = JSON.parse(raw);
            return Array.isArray(arr) ? arr : [];
        } catch { return []; }
    }
    function addRecentMethod(svcName, methodName) {
        if (!svcName || !methodName) return;
        var list = getRecentMethods();
        // De-dupe: if this exact pair is already in the list, drop the
        // old entry so the freshly-touched copy lands at the top.
        list = list.filter(function (r) {
            return !(r.service === svcName && r.method === methodName);
        });
        list.unshift({ service: svcName, method: methodName });
        if (list.length > MAX_RECENT_METHODS) list.length = MAX_RECENT_METHODS;
        try { localStorage.setItem(wsKey(RECENT_METHODS_KEY), JSON.stringify(list)); } catch {}
    }

    // ---- Channel State (Duplex / Client Streaming) ----
    // ---- Channel State ----
    // Active channel for the currently selected method. The legacy
    // global variables are kept for backward compatibility with the
    // existing render/execute code — they always reflect whichever
    // channel is associated with the current (service, method) pair.
    let duplexChannelId = null;
    let duplexConnected = false;
    let duplexSseSource = null;
    let sentCount = 0;
    let receivedCount = 0;
    let channelError = null;
    // Collected client->server messages with their send-timestamp offset
    // (ms since channelStartMs). Phase-2 mock replay needs this to pace
    // the duplex session at its original cadence. streamMessages already
    // carries the server->client frames with their server-side timestampMs.
    let sentMessages = [];
    let channelStartMs = 0;

    // Multi-channel store: keyed by "service::method", holds the
    // channel state for methods that have an open connection. When
    // the user switches methods, the current channel is stashed
    // here and the target method's channel (if any) is restored.
    var openChannels = {};

    function channelStoreKey(svcName, methodName) {
        return svcName + '::' + methodName;
    }

    function stashCurrentChannel() {
        if (!duplexChannelId) return;
        if (!selectedService || !selectedMethod) return;
        var key = channelStoreKey(selectedService.name, selectedMethod.name);
        openChannels[key] = {
            channelId: duplexChannelId,
            connected: duplexConnected,
            sseSource: duplexSseSource,
            sentCount: sentCount,
            receivedCount: receivedCount,
            channelError: channelError,
            streamMessages: streamMessages.slice(),
            sentMessages: sentMessages.slice(),
            channelStartMs: channelStartMs
        };
    }

    function restoreChannelFor(svcName, methodName) {
        var key = channelStoreKey(svcName, methodName);
        var saved = openChannels[key];
        if (saved) {
            duplexChannelId = saved.channelId;
            duplexConnected = saved.connected;
            duplexSseSource = saved.sseSource;
            sentCount = saved.sentCount;
            receivedCount = saved.receivedCount;
            channelError = saved.channelError;
            streamMessages = saved.streamMessages;
            sentMessages = saved.sentMessages || [];
            channelStartMs = saved.channelStartMs || 0;
            return true;
        }
        // No saved channel — reset to defaults
        duplexChannelId = null;
        duplexConnected = false;
        duplexSseSource = null;
        sentCount = 0;
        receivedCount = 0;
        channelError = null;
        sentMessages = [];
        channelStartMs = 0;
        return false;
    }

    function removeChannelFor(svcName, methodName) {
        var key = channelStoreKey(svcName, methodName);
        var saved = openChannels[key];
        if (saved && saved.sseSource) {
            try { saved.sseSource.close(); } catch {}
        }
        delete openChannels[key];
    }

    // ---- GraphQL State ----
    // Per-method query overrides — when the user edits the auto-generated
    // operation in the Query pane, we keep that override here keyed by
    // 'service::method'. handleExecute / channelSend wrap the user's
    // variables together with this override into the standard
    // { query, variables } payload before sending to the server.
    let graphqlQueryOverrides = {};

    // ---- WebSocket State ----
    // Per-method outgoing frame type — 'text' or 'binary'. Persisted in
    // memory only (no localStorage). channelSend wraps the editor value
    // into { type, text/base64 } based on this.
    let websocketFrameType = 'text';
    // When the user picked a binary file via the upload button, we keep its
    // base64 payload here so the next channelSend uses it instead of the
    // editor content.
    let websocketPendingBinary = null;

    // ---- History Search State ----
    // Free-text search over service / method / body / status, applied on
    // top of the existing showAllHistory toggle. Status filter buckets the
    // entries into "all" / "ok" / "error" so the user can scan failures
    // without scrolling. Both live in memory only — refreshing the page
    // resets them, but the underlying history records are still on disk.
    let historySearchQuery = '';
    let historyStatusFilter = 'all'; // 'all' | 'ok' | 'error'

    // ---- Code Export State ----
    // Per-method preference for the export language. The set of available
    // languages depends on the protocol — see CODE_EXPORT_LANGUAGES below.
    let codeExportLang = null;

    // ---- Form Validation State ----
    // Per-field validation errors keyed by the same dotted formValues key
    // ('user.address.zip', 'tags.0', etc.). Populated by handleExecute on
    // submit, cleared per-field as the user edits, fully cleared when the
    // selected method changes.
    let formValidationErrors = {};

    // ---- Request Tabs State ----
    // Multiple request methods can be open simultaneously. Each tab
    // represents an open method with its own service/method reference.
    // The active tab drives selectedService/selectedMethod. When the
    // user clicks a sidebar method, openTab() either focuses an
    // existing tab or creates a new one.
    let requestTabs = [];   // [{ id, serviceKey, methodKey, service, method }]
    let activeTabId = null;
    // #123 — open-tab persistence. Save the (serviceKey, methodKey,
    // id) triplets to localStorage on every tab mutation; rehydrate
    // service/method object references lazily once discovery
    // surfaces the right service. Stale entries (service no longer
    // discovered) are dropped silently — opening the same workbench
    // against a different schema shouldn't surface phantom tabs.
    function persistRequestTabs() {
        try {
            var data = {
                tabs: requestTabs.map(function (t) {
                    return { id: t.id, serviceKey: t.serviceKey, methodKey: t.methodKey };
                }),
                active: activeTabId,
            };
            localStorage.setItem(wsKey('bowire_request_tabs'), JSON.stringify(data));
            markSaved('tabs');
        } catch (e) { markSaveFailed('tabs', e); }
    }
    function restoreRequestTabsFromStorage() {
        try {
            var raw = localStorage.getItem(wsKey('bowire_request_tabs'));
            if (!raw) return null;
            var data = JSON.parse(raw);
            if (!data || !Array.isArray(data.tabs)) return null;
            return data;
        } catch { return null; }
    }
    // One-shot rehydrate guard. The first render() call that finds
    // requestTabs empty + services populated + a saved tab set in
    // localStorage will rehydrate; subsequent renders skip even if
    // the user manually closes every tab. Without the guard we'd
    // resurrect tabs the user just closed on the next render.
    let requestTabsRehydrated = false;
    function rehydrateRequestTabs() {
        if (requestTabsRehydrated) return;
        if (requestTabs.length > 0) { requestTabsRehydrated = true; return; }
        if (!Array.isArray(services) || services.length === 0) return;
        var data = restoreRequestTabsFromStorage();
        if (!data || data.tabs.length === 0) { requestTabsRehydrated = true; return; }
        requestTabsRehydrated = true;
        for (var i = 0; i < data.tabs.length; i++) {
            var t = data.tabs[i];
            var svc = services.find(function (s) { return s.name === t.serviceKey; });
            if (!svc) continue;
            var meth = (svc.methods || []).find(function (m) { return m.name === t.methodKey; });
            if (!meth) continue;
            requestTabs.push({
                id: t.id,
                serviceKey: t.serviceKey,
                methodKey: t.methodKey,
                service: svc,
                method: meth,
            });
        }
        if (requestTabs.length > 0) {
            // Pick the previously-active tab when it survived,
            // otherwise default to the first restored tab.
            var activeStillAround = requestTabs.find(function (x) { return x.id === data.active; });
            var firstTab = activeStillAround || requestTabs[0];
            activeTabId = firstTab.id;
            selectedMethod = firstTab.method;
            selectedService = firstTab.service;
        }
    }

    let _tabIdCounter = 0;
    function nextTabId() {
        return 'tab_' + (++_tabIdCounter);
    }

    /**
     * Open a tab for the given service + method. If a tab already
     * exists for that pair, switch to it; otherwise create a new tab
     * and make it active.
     */
    function openTab(svc, method, opts) {
        opts = opts || {};
        var existing = null;
        for (var i = 0; i < requestTabs.length; i++) {
            if (requestTabs[i].serviceKey === svc.name
                && requestTabs[i].methodKey === method.name) {
                existing = requestTabs[i];
                break;
            }
        }
        if (existing) {
            switchTab(existing.id);
            return;
        }
        // Clear freeform mode if active — opening a tab exits freeform
        freeformRequest = null;

        // Save outgoing method state before switching
        stashCurrentChannel();
        saveCurrentMethodState();

        // Browser/Hoppscotch-style tab semantics: by default, the
        // active tab adopts the new method (replacing what was open
        // there). A separate, explicit `inNewTab: true` opens a fresh
        // tab — that path is taken by the '+' button on the strip,
        // by Ctrl/Cmd+click on a method row, and by middle-click.
        // The previous behaviour ('every method click = new tab')
        // produced an ever-growing tab strip on normal navigation.
        var activeTab = null;
        if (!opts.inNewTab && activeTabId !== null) {
            for (var j = 0; j < requestTabs.length; j++) {
                if (requestTabs[j].id === activeTabId) { activeTab = requestTabs[j]; break; }
            }
        }
        var tab;
        if (activeTab) {
            // Repurpose the active tab. Keep the id stable so
            // persisted state + UI focus don't flicker.
            activeTab.serviceKey = svc.name;
            activeTab.methodKey = method.name;
            activeTab.service = svc;
            activeTab.method = method;
            tab = activeTab;
        } else {
            tab = {
                id: nextTabId(),
                serviceKey: svc.name,
                methodKey: method.name,
                service: svc,
                method: method
            };
            requestTabs.push(tab);
        }
        activeTabId = tab.id;
        persistRequestTabs();

        // Apply the new selection
        selectedMethod = method;
        selectedService = svc;

        // Restore channel state for the target method
        var hadChannel = restoreChannelFor(svc.name, method.name);
        if (!hadChannel) {
            responseData = null;
            responseError = null;
            streamMessages = [];
            statusInfo = null;
        }
        streamSelectedIndex = null;
        streamSelectedIds = new Set();
        streamSelectionAnchorIdx = null;
        streamAutoScroll = true;
        streamDetailMaximized = false;
        widgetPaneMaximized = false;
        formValidationErrors = {};
        diffViewOpen = false;
        diffSnapshotA = null;
        diffSnapshotB = null;
        showAllHistory = false;
        loadMethodStateFor(svc.name, method.name, method.inputType);
        addRecentMethod(svc.name, method.name);
        expandedServices.add(svc.name);
        persistExpandedServices();
        if (isMobile()) sidebarCollapsed = true;
        render();
    }

    /**
     * Switch to an existing tab by id. Stashes the current method
     * state and restores the target tab's state.
     */
    function switchTab(tabId) {
        if (activeTabId === tabId && !freeformRequest) return;
        var tab = null;
        for (var i = 0; i < requestTabs.length; i++) {
            if (requestTabs[i].id === tabId) { tab = requestTabs[i]; break; }
        }
        if (!tab) return;

        // Clear freeform mode if active — switching to a tab exits freeform
        freeformRequest = null;

        stashCurrentChannel();
        saveCurrentMethodState();

        activeTabId = tab.id;
        persistRequestTabs();
        selectedMethod = tab.method;
        selectedService = tab.service;

        var hadChannel = restoreChannelFor(tab.serviceKey, tab.methodKey);
        if (!hadChannel) {
            responseData = null;
            responseError = null;
            streamMessages = [];
            statusInfo = null;
        }
        streamSelectedIndex = null;
        streamSelectedIds = new Set();
        streamSelectionAnchorIdx = null;
        streamAutoScroll = true;
        streamDetailMaximized = false;
        widgetPaneMaximized = false;
        formValidationErrors = {};
        diffViewOpen = false;
        diffSnapshotA = null;
        diffSnapshotB = null;
        showAllHistory = false;
        loadMethodStateFor(tab.serviceKey, tab.methodKey, tab.method.inputType);
        render();
    }

    /**
     * Close a tab by id. If it was the active tab, activate an
     * adjacent tab (prefer the one to the right, fall back to left).
     * If no tabs remain, clear the selection.
     */
    function closeTab(tabId) {
        var idx = -1;
        for (var i = 0; i < requestTabs.length; i++) {
            if (requestTabs[i].id === tabId) { idx = i; break; }
        }
        if (idx < 0) return;

        requestTabs.splice(idx, 1);
        persistRequestTabs();

        if (activeTabId === tabId) {
            if (requestTabs.length === 0) {
                activeTabId = null;
                selectedMethod = null;
                selectedService = null;
                responseData = null;
                responseError = null;
                streamMessages = [];
                statusInfo = null;
                channelError = null;
            } else {
                // Pick a neighbor: prefer right, fall back to left
                var nextIdx = idx < requestTabs.length ? idx : requestTabs.length - 1;
                var next = requestTabs[nextIdx];
                activeTabId = next.id;
                selectedMethod = next.method;
                selectedService = next.service;

                var hadChannel = restoreChannelFor(next.serviceKey, next.methodKey);
                if (!hadChannel) {
                    responseData = null;
                    responseError = null;
                    streamMessages = [];
                    statusInfo = null;
                }
                streamSelectedIndex = null;
                streamSelectedIds = new Set();
                streamSelectionAnchorIdx = null;
                streamAutoScroll = true;
                streamDetailMaximized = false;
                formValidationErrors = {};
                showAllHistory = false;
                loadMethodStateFor(next.serviceKey, next.methodKey, next.method.inputType);
            }
        }
        render();
    }

    // ---- Response Diff State ----
    // Per-method FIFO ring of the last 5 unary responses, keyed by
    // 'service::method'. Each entry: { timestamp, status, durationMs, body }.
    // The diff view lets the user pick any two snapshots to compare side by
    // side. In-memory only — responses can carry secrets.
    const MAX_RESPONSE_SNAPSHOTS = 5;
    let responseSnapshots = {};   // { [methodKey]: [ snapshot, ... ] }
    let diffViewOpen = false;
    let diffSnapshotA = null;     // index into responseSnapshots[key]
    let diffSnapshotB = null;     // index into responseSnapshots[key]

    // ---- Keyboard Shortcuts Overlay State ----
    let showShortcutsOverlay = false;

    const HISTORY_KEY = 'bowire_history';
    const FAVORITES_KEY = 'bowire_favorites';
    const ENVIRONMENTS_KEY = 'bowire_environments';
    const ACTIVE_ENV_KEY = 'bowire_active_env';
    const GLOBAL_VARS_KEY = 'bowire_global_vars';
    const SOURCE_MODE_KEY = 'bowire_source_mode';
    const SIDEBAR_WIDTH_KEY = 'bowire_sidebar_width';
    const SIDEBAR_VIEW_KEY = 'bowire_sidebar_view';
    const MAX_HISTORY = 50;

    // ---- Sidebar view mode (services list vs. favorites-only list) ----
    // 'services' = default — full service/method tree with protocol filter.
    // 'favorites' = flat list of user-pinned methods across all protocols.
    // The switch is a pair of pills at the top of the sidebar; the chosen
    // view survives page reloads via localStorage.
    let sidebarView = (function () {
        try {
            var v = localStorage.getItem(SIDEBAR_VIEW_KEY);
            if (v === 'favorites' || v === 'environments' || v === 'flows' || v === 'proxy') return v;
            return 'services';
        } catch { return 'services'; }
    })();
    function setSidebarView(v) {
        sidebarView = (v === 'favorites' || v === 'environments' || v === 'flows' || v === 'proxy') ? v : 'services';
        try { localStorage.setItem(SIDEBAR_VIEW_KEY, sidebarView); } catch {}
    }

    // ---- Sidebar width (drag-to-resize splitter between sidebar and main) ----
    // Default is the CSS var `--bowire-sidebar-width` (400 px). User-set
    // values persist across reloads. 0 means "use CSS default" — lets the
    // responsive breakpoints in bowire.css take over on tablet/mobile.
    //
    // Min is set so all four view tabs (Services / Favorites /
    // Environments / Flows) still fit on a single row — below 360 the
    // 'Environments' label pushes 'Flows' onto a wrapped second line
    // (or worse, off the edge) and the user loses sight of features.
    // The CSS keeps `flex-wrap: wrap` as a safety net, but the
    // resize handle won't push you into that state by accident.
    const SIDEBAR_WIDTH_MIN = 360;
    const SIDEBAR_WIDTH_MAX = 720;
    let sidebarWidth = (function () {
        try {
            var saved = localStorage.getItem(SIDEBAR_WIDTH_KEY);
            if (!saved) return 0;
            var n = parseInt(saved, 10);
            if (!isFinite(n)) return 0;
            if (n < SIDEBAR_WIDTH_MIN || n > SIDEBAR_WIDTH_MAX) return 0;
            return n;
        } catch { return 0; }
    })();

    // ---- Source Mode (URL reflection vs. .proto file upload) ----
    // The two are alternative ways to discover services. The selector at the
    // top of the sidebar lets the user switch between them. Persisted across
    // reloads in localStorage. Ignored entirely when the URL is locked
    // (embedded mode testing only the host's own service).
    let sourceMode = (function () {
        try { return localStorage.getItem(SOURCE_MODE_KEY) || 'url'; }
        catch { return 'url'; }
    })();

    function setSourceMode(mode) {
        sourceMode = mode;
        try { localStorage.setItem(SOURCE_MODE_KEY, mode); } catch {}
    }

    // ---- Transcoding Mode (per method, gRPC vs HTTP) ----
    // For transcoded gRPC methods (those that carry an httpMethod annotation
    // pulled from google.api.http), the user can choose to invoke them via
    // gRPC OR via HTTP. The choice is per-method and lives in localStorage so
    // it survives reloads and method-switching.
    const TRANSCODING_MODE_KEY = 'bowire_transcoding_mode';
    let transcodingModes = (function () {
        try { return JSON.parse(localStorage.getItem(TRANSCODING_MODE_KEY) || '{}') || {}; }
        catch { return {}; }
    })();

    function transcodingKey(service, method) { return service + '::' + method; }

    function getTranscodingMode(service, method) {
        // Default: 'grpc' (the original protocol). User can opt into 'http'.
        return transcodingModes[transcodingKey(service, method)] || 'grpc';
    }

    function setTranscodingMode(service, method, mode) {
        transcodingModes[transcodingKey(service, method)] = mode;
        try { localStorage.setItem(TRANSCODING_MODE_KEY, JSON.stringify(transcodingModes)); } catch {}
    }

    function methodSupportsTranscoding(method) {
        // Transcoding only makes sense for gRPC methods that carry a
        // google.api.http annotation — one method, two transports
        // (native gRPC vs. transcoded HTTP). REST methods are always
        // populated with httpMethod/httpPath because REST *is* HTTP;
        // checking those alone made the toggle render on every REST
        // endpoint (#86), where it has nothing to switch.
        return !!(method && method.source === 'grpc' && method.httpMethod && method.httpPath);
    }

    /**
     * The "via HTTP" path requires the REST plugin to be loaded — it provides
     * the IInlineHttpInvoker that core dispatches through. When the REST
     * plugin isn't installed, transcoded gRPC methods still SHOW their HTTP
     * verb + path (read-only) but the toggle is hidden so users don't try
     * to invoke through a path that has no implementation.
     */
    function isHttpInvocationAvailable() {
        return Array.isArray(protocols)
            && protocols.some(function (p) { return p && p.id === 'rest'; });
    }

    // ---- Environments State ----
    let envManagerOpen = false;
    let envManagerSelectedId = null; // currently edited env in the manager modal
    let envManagerTab = 'env';        // 'env' or 'global'
    let envManagerDiffTargetId = null; // when set, the right panel shows a diff
                                       // against this env instead of the editor
    let envEditorTab = 'variables';    // 'variables' | 'auth' | 'compare'

    // ---- Recordings State ----
    // A recording is a named, ordered sequence of captured invocations that
    // can be replayed (re-runs every step in order with current env vars),
    // exported (HAR 1.2 / .http / .json), or converted into per-method test
    // assertions. Postman feature parity for "Collection Runner" without
    // re-creating the whole collection concept — Bowire already has Favorites
    // for "save this one call" and now Recordings for "save this sequence".
    //
    // Storage shape (mirrors environments — localStorage cache + disk source
    // of truth at ~/.bowire/recordings.json):
    //   bowire_recordings → [{ id, name, description, createdAt, steps: [
    //                           { id, protocol, service, method, methodType,
    //                             body, messages[], metadata, status,
    //                             durationMs, response, capturedAt }, ... ] }, ... ]
    const RECORDINGS_KEY = 'bowire_recordings';
    let recordingsList = [];          // all known recordings
    let recordingActiveId = null;     // id of the recording currently being captured to (null = not recording)
    // recordingManagerOpen retired — Recordings rail mode owns the surface.
    let recordingManagerSelectedId = null; // selected recording in the manager left panel
    let recordingReplayState = null;  // { recordingId, stepIndex, status, errors[] } during replay

    // ---- Freeform Request State ----
    // When freeformRequest is non-null, the main pane shows the
    // freeform request builder instead of a discovered method's
    // request/response layout. The user picks a protocol, enters
    // a URL + method name + body, and hits Execute.
    let freeformRequest = null;
    // Shape: { protocol: 'grpc'|'rest'|..., serverUrl: '', service: '',
    //          method: '', body: '{}', metadata: {}, methodType: 'Unary',
    //          mockResponse: '', mockStatus: 'OK' }
    // mockResponse / mockStatus feed the GUI-Mock-Builder "Save as Mock
    // Step" path — the user can author a mock response without ever
    // hitting a live backend. A successful Execute auto-populates them
    // so the user can tweak the real response before freezing it.
    let freeformMockExpanded = false;

    function startFreeformRequest() {
        freeformRequest = {
            protocol: protocols.length > 0 ? protocols[0].id : 'grpc',
            serverUrl: serverUrls.length > 0 ? serverUrls[0] : '',
            service: '',
            method: '',
            body: '{}',
            metadata: {},
            methodType: 'Unary',
            mockResponse: '',
            mockStatus: 'OK'
        };
        selectedMethod = null;
        selectedService = null;
        // The freeform builder only renders in the Discover main
        // pane; switching modes (Home, Mocks, Recordings, …)
        // bypasses it. Force Discover so clicking the '+' tab
        // button (or Ctrl+T) lands the operator on the builder
        // regardless of the active rail mode.
        if (typeof railMode !== 'undefined' && railMode !== 'discover') {
            railMode = 'discover';
            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
            if (typeof sidebarView !== 'undefined') sidebarView = 'services';
        }
        render();
    }

    function cancelFreeformRequest() {
        freeformRequest = null;
        render();
    }

    // ---- Collections State ----
    // Named groups of saved requests, independent of recordings.
    // Each item has: { id, protocol, service, method, methodType,
    // body, metadata, expectedStatus }. Persisted to localStorage
    // + disk sync like recordings/environments.
    const COLLECTIONS_KEY = 'bowire_collections';
    let collectionsList = [];
    // collectionManagerOpen retired — Collections rail mode owns the surface.
    let collectionManagerSelectedId = null;
    let collectionRunState = null; // { collectionId, stepIndex, status, results[] }

    function loadCollections() {
        try {
            var raw = localStorage.getItem(wsKey(COLLECTIONS_KEY));
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch { return []; }
    }

    // Pull collections from disk on init so the list survives browser
    // changes and CLI usage. Mirrors loadRecordingsFromDisk: disk wins
    // over the localStorage cache when both are present (the cache
    // only matters for instant updates between roundtrips).
    function loadCollectionsFromDisk() {
        return fetch(config.prefix + '/api/collections')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && Array.isArray(data.collections)) {
                    collectionsList = data.collections;
                    try { localStorage.setItem(wsKey(COLLECTIONS_KEY), JSON.stringify(collectionsList)); } catch { /* ignore */ }
                } else {
                    collectionsList = loadCollections();
                }
            })
            .catch(function () {
                collectionsList = loadCollections();
            });
    }

    function persistCollections() {
        try {
            localStorage.setItem(wsKey(COLLECTIONS_KEY), JSON.stringify(collectionsList));
            markSaved('collections');
        } catch (e) { markSaveFailed('collections', e); }
        scheduleCollectionsDiskSync();
    }

    var _collectionsDiskSyncTimer = null;
    function scheduleCollectionsDiskSync() {
        if (_collectionsDiskSyncTimer) clearTimeout(_collectionsDiskSyncTimer);
        _collectionsDiskSyncTimer = setTimeout(function () {
            _collectionsDiskSyncTimer = null;
            fetch(config.prefix + '/api/collections', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ collections: collectionsList })
            }).catch(function () {});
        }, 400);
    }

    function nextCollectionId() {
        return 'col_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    }

    function nextCollectionItemId() {
        return 'ci_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    }

    function createCollection(name) {
        var col = {
            id: nextCollectionId(),
            name: name || ('Collection ' + new Date().toLocaleString()),
            items: [],
            createdAt: Date.now()
        };
        collectionsList.push(col);
        persistCollections();
        return col;
    }

    function deleteCollection(id) {
        var idx = collectionsList.findIndex(function (c) { return c.id === id; });
        if (idx < 0) return;
        collectionsList.splice(idx, 1);
        if (collectionManagerSelectedId === id) collectionManagerSelectedId = null;
        persistCollections();
    }

    function addToCollection(collectionId, item) {
        var col = collectionsList.find(function (c) { return c.id === collectionId; });
        if (!col) return;
        col.items.push(Object.assign({ id: nextCollectionItemId() }, item));
        persistCollections();
    }

    function removeFromCollection(collectionId, itemId) {
        var col = collectionsList.find(function (c) { return c.id === collectionId; });
        if (!col) return;
        var idx = col.items.findIndex(function (i) { return i.id === itemId; });
        if (idx >= 0) col.items.splice(idx, 1);
        persistCollections();
    }

    function saveCurrentRequestToCollection(collectionId) {
        if (!selectedService || !selectedMethod) return;
        var body = requestMessages[0] || '{}';
        var meta = {};
        var metaRows = document.querySelectorAll('.bowire-metadata-row');
        for (var i = 0; i < metaRows.length; i++) {
            var inputs = metaRows[i].querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                meta[inputs[0].value.trim()] = inputs[1].value;
            }
        }
        addToCollection(collectionId, {
            protocol: (selectedService && selectedService.source) || selectedProtocol || 'grpc',
            service: selectedService.name,
            method: selectedMethod.name,
            methodType: selectedMethod.methodType || 'Unary',
            body: body,
            messages: requestMessages.slice(),
            metadata: Object.keys(meta).length > 0 ? meta : null,
            serverUrl: (selectedService && selectedService.originUrl) || (serverUrls[0] || null)
        });
        toast('Saved to collection', 'success');
    }

    // ---- Console / Log View State ----
    // Chronological ring buffer of all request/response activity. Distinct
    // from history (which is method-centric and persisted) — the console is
    // session-centric and ephemeral. Useful for debugging request sequences,
    // chaining, and watching streams in real time.
    const CONSOLE_MAX = 200;
    let consoleLog = [];          // [{ id, time, type, method, status, durationMs, body, expanded }]
    let consoleOpen = false;
    let consoleNextId = 1;

    function addConsoleEntry(entry) {
        var e = Object.assign({
            id: consoleNextId++,
            time: Date.now(),
            expanded: false
        }, entry);
        consoleLog.push(e);
        if (consoleLog.length > CONSOLE_MAX) {
            consoleLog.splice(0, consoleLog.length - CONSOLE_MAX);
        }
        // Fast-path: if the console is already open, try to append
        // the new entry in place instead of tearing down + rebuilding
        // the entire panel. This stops the flicker during streaming
        // where every message fired a full remove + re-create cycle.
        if (consoleOpen && appendConsoleEntry(e)) return;
        if (consoleOpen) renderConsolePanel(true);
    }

    function clearConsole() {
        consoleLog = [];
        renderConsolePanel(false);
    }

    function toggleConsole() {
        consoleOpen = !consoleOpen;
        renderConsolePanel(true);
    }

    // ---- Performance Benchmark State ----
    // Repeats a unary call N times and computes latency stats. Sequential or
    // limited concurrency. Each iteration runs through the full pipeline
    // (substitute, auth, fetch) so ${now}/${uuid} regenerate per call and
    // headers are always fresh. Numbers only — no response bodies are kept,
    // so memory stays bounded even for large N.
    let benchmark = {
        running: false,
        cancelled: false,
        total: 0,
        completed: 0,
        success: 0,
        failure: 0,
        durations: [],          // ms per call (success only)
        statusCounts: {},       // status name → count
        startTime: 0,
        endTime: 0,
        config: { n: 100, concurrency: 1 }
    };

    function resetBenchmark(config) {
        benchmark = {
            running: false,
            cancelled: false,
            total: config ? config.n : 0,
            completed: 0,
            success: 0,
            failure: 0,
            durations: [],
            statusCounts: {},
            startTime: 0,
            endTime: 0,
            config: config || benchmark.config
        };
    }

    function stopBenchmark() {
        benchmark.cancelled = true;
    }

    async function runBenchmark(n, concurrency) {
        if (!selectedMethod || !selectedService) return;
        if (benchmark.running) return;

        // Snapshot the current request body and metadata once. Each iteration
        // re-substitutes so ${now}/${uuid} change per call.
        var bodyTemplates;
        if (requestInputMode === 'form' && selectedMethod && selectedMethod.inputType) {
            syncFormToJson();
            bodyTemplates = [requestMessages[0] || '{}'];
        } else {
            var editors = $$('.bowire-message-editor');
            if (editors.length > 0) {
                bodyTemplates = editors.map(function (e) { return e.value || '{}'; });
            } else {
                var single = $('.bowire-editor');
                bodyTemplates = [single ? single.value : '{}'];
            }
        }

        // Snapshot metadata template (with raw values — substituted per call)
        var metadataTemplate = {};
        var rows = $$('.bowire-metadata-row');
        for (var ri = 0; ri < rows.length; ri++) {
            var inputs = rows[ri].querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                metadataTemplate[inputs[0].value.trim()] = inputs[1].value;
            }
        }

        var service = selectedService.name;
        var method = selectedMethod.name;
        var fullName = service + '/' + method;
        var protocolId = selectedService.source || selectedProtocol || undefined;
        // Snapshot the originUrl so each iteration routes to the right URL
        var serviceUrlParam = serverUrlParamForService(selectedService, false);

        resetBenchmark({ n: n, concurrency: Math.max(1, Math.min(concurrency, 20)) });
        benchmark.running = true;
        benchmark.startTime = performance.now();
        addConsoleEntry({ type: 'request', method: fullName, status: 'Benchmark', body: 'Starting ' + n + ' calls (concurrency ' + concurrency + ')' });
        render();

        var nextIndex = 0;
        async function worker() {
            while (true) {
                if (benchmark.cancelled) return;
                var idx = nextIndex++;
                if (idx >= n) return;

                // Per-call substitution + auth so ${now}/${uuid} are fresh
                var messages = bodyTemplates.map(substituteVars);
                var meta = {};
                for (var k in metadataTemplate) {
                    meta[k] = substituteVars(metadataTemplate[k]);
                }
                meta = await applyAuth(meta);

                var t0 = performance.now();
                try {
                    var resp = await fetch(config.prefix + '/api/invoke' + serviceUrlParam, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ service: service, method: method, messages: messages, metadata: Object.keys(meta).length > 0 ? meta : null, protocol: protocolId })
                    });
                    var elapsed = performance.now() - t0;
                    var result = await resp.json();
                    if (result.error || result.title) {
                        benchmark.failure++;
                        benchmark.statusCounts['Error'] = (benchmark.statusCounts['Error'] || 0) + 1;
                    } else {
                        benchmark.success++;
                        benchmark.durations.push(elapsed);
                        var status = result.status || 'OK';
                        benchmark.statusCounts[status] = (benchmark.statusCounts[status] || 0) + 1;
                    }
                } catch (e) {
                    benchmark.failure++;
                    benchmark.statusCounts['NetworkError'] = (benchmark.statusCounts['NetworkError'] || 0) + 1;
                }
                benchmark.completed++;

                // Throttle re-renders to ~10/s for large runs
                if (benchmark.completed % Math.max(1, Math.floor(n / 50)) === 0 || benchmark.completed === n) {
                    render();
                }
            }
        }

        // Spawn workers
        var workers = [];
        for (var w = 0; w < benchmark.config.concurrency; w++) workers.push(worker());
        await Promise.all(workers);

        benchmark.endTime = performance.now();
        benchmark.running = false;
        var totalMs = benchmark.endTime - benchmark.startTime;
        addConsoleEntry({
            type: 'response',
            method: fullName,
            status: benchmark.cancelled ? 'Cancelled' : 'Benchmark complete',
            durationMs: Math.round(totalMs),
            body: benchmark.success + ' OK / ' + benchmark.failure + ' failed'
        });
        render();
    }

    function computeBenchmarkStats() {
        if (benchmark.durations.length === 0) return null;
        var sorted = benchmark.durations.slice().sort(function (a, b) { return a - b; });
        function percentile(p) {
            if (sorted.length === 0) return 0;
            var idx = Math.ceil((p / 100) * sorted.length) - 1;
            return sorted[Math.max(0, Math.min(idx, sorted.length - 1))];
        }
        var sum = 0;
        for (var i = 0; i < sorted.length; i++) sum += sorted[i];
        var totalSec = (benchmark.endTime - benchmark.startTime) / 1000;
        return {
            count: sorted.length,
            min: sorted[0],
            max: sorted[sorted.length - 1],
            avg: sum / sorted.length,
            p50: percentile(50),
            p90: percentile(90),
            p95: percentile(95),
            p99: percentile(99),
            throughput: totalSec > 0 ? (sorted.length / totalSec) : 0,
            totalSeconds: totalSec
        };
    }

    // ---- #131 Benchmarks rail-mode state ----
    // Per-workspace list of saved benchmark specs + their last result.
    // Hydrated lazily in loadBenchmarks() (benchmarks.js). The legacy
    // `benchmark` object above stays as the live-run scratchpad for the
    // in-progress execution; once a run completes the renderer copies
    // the relevant fields onto the spec's `lastRun` property and the
    // scratchpad resets.
    let benchmarksList = null;       // null = not yet loaded; [] = loaded, empty

    // ---- #125 Phase 4 — secret.* + ai.* source-prefix state ----
    //
    // secrets: in-memory Map per workspace, populated through the
    // Workspace-detail Settings section. Phase 4 is intentionally
    // session-local — no disk plaintext until Phase 5 wraps an
    // OS-keyring (Windows Credential Manager / macOS Keychain /
    // libsecret). The map carries names → values; the resolver
    // surfaces the real value to the request pipeline but the chip
    // renders masked, and recording / export sanitisers replace the
    // value with '***' before it leaves the workbench.
    //
    // ai-cache: a session-scoped Object keyed by the full `ai.<path>`
    // reference. prefetchAiVars(template) scans the template once
    // before send-time, calls sendChat with a short prompt
    // ("suggest a value for X in context Y"), parses the answer and
    // writes the cache. substituteVars then returns the cached value
    // synchronously the same way it does for env vars.
    var _workspaceSecrets = {};      // workspaceId → { name → value }
    var _aiVarsCache = {};           // 'ai.NAME' → resolved value (string)
    var _aiVarsInflight = {};        // 'ai.NAME' → Promise (dedup concurrent prefetches)

    function getWorkspaceSecrets(workspaceId) {
        var id = workspaceId || activeWorkspaceId;
        if (!id) return {};
        return _workspaceSecrets[id] || (_workspaceSecrets[id] = {});
    }
    function setWorkspaceSecret(name, value, workspaceId) {
        var bag = getWorkspaceSecrets(workspaceId);
        if (value === null || value === undefined || value === '') delete bag[name];
        else bag[name] = String(value);
    }
    function listWorkspaceSecrets(workspaceId) {
        return Object.keys(getWorkspaceSecrets(workspaceId));
    }
    function resolveSecret(name) {
        var bag = getWorkspaceSecrets();
        return Object.prototype.hasOwnProperty.call(bag, name) ? bag[name] : null;
    }
    function resolveAiVar(key) {
        // key is the full prefix-stripped name, e.g. 'next_pet_id'.
        // Cache lookup uses the unprefixed key.
        return Object.prototype.hasOwnProperty.call(_aiVarsCache, key)
            ? _aiVarsCache[key] : null;
    }
    function clearAiVarsCache() { _aiVarsCache = {}; _aiVarsInflight = {}; }

    // #125 Phase 4 — secret sanitiser. Walk a JSON-serialisable value
    // and replace any string occurrence of a current secret's value
    // with '***'. Used at every export boundary (recording step disk
    // write, recording export download, mock response capture). The
    // resolver hands the real value to the upstream request but the
    // captured response shouldn't carry it back in plaintext (an API
    // that echoes the auth header in a debug field would otherwise
    // leak it into the recording).
    //
    // Skip-cheap path: if the workspace has no secrets, return the
    // value untouched. Otherwise walk recursively. The replace is
    // string-contains, not key-aware — we don't know which fields
    // can carry the value, so we treat every string as suspect.
    function sanitiseForExport(value) {
        var bag = (typeof getWorkspaceSecrets === 'function')
            ? getWorkspaceSecrets() : {};
        var values = [];
        for (var k in bag) {
            if (Object.prototype.hasOwnProperty.call(bag, k) && bag[k]) {
                values.push(bag[k]);
            }
        }
        if (values.length === 0) return value;
        return _walkAndReplace(value, values);
    }

    function _walkAndReplace(value, secretValues) {
        if (value === null || value === undefined) return value;
        var t = typeof value;
        if (t === 'string') {
            var out = value;
            for (var i = 0; i < secretValues.length; i++) {
                if (out.indexOf(secretValues[i]) !== -1) {
                    out = out.split(secretValues[i]).join('***');
                }
            }
            return out;
        }
        if (t !== 'object') return value;
        if (Array.isArray(value)) {
            return value.map(function (v) { return _walkAndReplace(v, secretValues); });
        }
        var copy = {};
        for (var k in value) {
            if (Object.prototype.hasOwnProperty.call(value, k)) {
                copy[k] = _walkAndReplace(value[k], secretValues);
            }
        }
        return copy;
    }
    let benchmarksSelectedId = null;
    let benchmarkActiveSpecId = null; // spec currently running, if any

    // ---- Request Chaining State ----
    // Holds the parsed JSON of the last successful response so subsequent
    // requests can reference its fields via ${response.path.to.field}.
    // Streaming and channel methods overwrite with each received message
    // (last message wins). Reset on page reload — never persisted to disk
    // because responses can contain tokens or other secrets.
    let lastResponseJson = null;

    function captureResponse(rawText) {
        if (typeof rawText !== 'string' || !rawText) return;
        try { lastResponseJson = JSON.parse(rawText); }
        catch { lastResponseJson = rawText; }
    }

    function captureResponseValue(value) {
        // For pre-parsed objects (e.g. SSE event payloads)
        if (value === null || value === undefined) return;
        lastResponseJson = value;
    }

    /**
     * Walk a dotted path against an arbitrary JSON value.
     * Supports nested objects, arrays via numeric segments, and quoted keys
     * via bracket-style aren't needed for v1 — keep it simple.
     */
    function walkJsonPath(value, path) {
        if (value == null) return undefined;
        if (!path) return value;
        var parts = path.split('.');
        var cur = value;
        for (var i = 0; i < parts.length; i++) {
            if (cur == null) return undefined;
            var part = parts[i];
            if (Array.isArray(cur)) {
                var idx = parseInt(part, 10);
                if (Number.isNaN(idx)) return undefined;
                cur = cur[idx];
            } else if (typeof cur === 'object') {
                cur = cur[part];
            } else {
                return undefined;
            }
        }
        return cur;
    }

    function resolveResponseVar(path) {
        if (lastResponseJson == null) return null;
        var v = walkJsonPath(lastResponseJson, path);
        if (v === undefined) return null;
        if (v === null) return 'null';
        if (typeof v === 'object') return JSON.stringify(v);
        return String(v);
    }

