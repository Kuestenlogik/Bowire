// @generated
// This file is a fragment of the assembled `wwwroot/bowire.js` bundle —
// the MSBuild step at compile-time concatenates every <BowireJsFragment>
// in `.csproj` into a single runtime file. The fragment opens an IIFE
// that init.js closes; per-file it is syntactically incomplete and
// would be invalid JS on its own. The `@generated` marker above tells
// CodeQL (and other static analysers) to skip parse-level checks here
// — the assembled bundle is what actually runs in the browser.
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

    // Boot-order pin — activeWorkspaceId has to be set BEFORE the
    // first wsKey() call, otherwise every per-workspace store
    // (server URLs, recordings, collections, &c.) reads from the
    // orphan namespace instead of the workspace the operator was
    // actually using. The full workspaces list + auto-create-on-
    // first-run dance still happens later at module scope; this
    // early read just claims the active id so the load-fns below
    // route correctly. Safe to re-read later — the value doesn't
    // change between this line and the workspace block.
    var activeWorkspaceId = null;
    try {
        var earlyActive = localStorage.getItem('bowire_active_workspace');
        if (earlyActive) activeWorkspaceId = earlyActive;
    } catch { /* localStorage disabled — fall through; orphan path applies */ }

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

    // ---- Per-URL aliases ---------------------------------------------
    // The sidebar, the discover-tree source header, the connection
    // popover, and the per-URL filter all show this name in place of
    // the (often very long) URL. Aliases are unique within a workspace
    // and survive URL edits — when the user retypes a URL we move the
    // existing alias over so the friendly name doesn't reset on every
    // change. localStorage shape is a plain { [url]: alias } dict.
    const SERVER_URL_ALIASES_KEY = 'bowire_server_url_aliases';
    let serverUrlAliases = (function () {
        try {
            var raw = localStorage.getItem(wsKey(SERVER_URL_ALIASES_KEY));
            if (!raw) return {};
            var parsed = JSON.parse(raw);
            return (parsed && typeof parsed === 'object') ? parsed : {};
        } catch { return {}; }
    })();

    function persistServerUrlAliases() {
        try { localStorage.setItem(wsKey(SERVER_URL_ALIASES_KEY), JSON.stringify(serverUrlAliases)); markSaved('URL aliases'); }
        catch (e) { markSaveFailed('URL aliases', e); }
    }

    // Best-effort short label for a URL — host + last meaningful path
    // segment, slugged so it's safe as a filter id / DOM token. Used as
    // the auto-suggested alias when the user first adds a URL.
    function defaultAliasForUrl(url) {
        if (!url) return '';
        var base = '';
        try {
            var u = new URL(url);
            var host = (u.host || '').replace(/^www\./i, '');
            // Pick the longest path segment (drops "/v3/openapi.json" → "openapi"
            // pattern that's usually less informative than the host).
            var segs = (u.pathname || '').split('/').filter(Boolean);
            var lastSeg = segs.length > 0 ? segs[segs.length - 1].replace(/\.[a-z0-9]+$/i, '') : '';
            base = lastSeg ? host.split('.')[0] + '-' + lastSeg : host.split('.')[0];
        } catch {
            base = url;
        }
        // Slug — lowercase, strip everything that isn't a word char or '-'.
        base = String(base).toLowerCase()
            .replace(/[^a-z0-9-]+/g, '-')
            .replace(/^-+|-+$/g, '');
        if (!base) base = 'url';
        // Truncate so the alias stays a "short" name.
        if (base.length > 24) base = base.slice(0, 24).replace(/-+$/, '');
        return base;
    }

    // Find an unused alias starting from `seed`, appending -2, -3, … on
    // collision. Optionally ignores `forUrl` so re-suggesting for an
    // existing entry doesn't collide with itself.
    function uniqueAlias(seed, forUrl) {
        var used = {};
        for (var k in serverUrlAliases) {
            if (Object.prototype.hasOwnProperty.call(serverUrlAliases, k) && k !== forUrl) {
                used[serverUrlAliases[k]] = true;
            }
        }
        if (!used[seed]) return seed;
        for (var i = 2; i < 1000; i++) {
            var candidate = seed + '-' + i;
            if (!used[candidate]) return candidate;
        }
        return seed + '-' + Date.now();
    }

    // Assign an auto-alias when a URL gains its first appearance. Never
    // overwrites an existing alias — the user's manual choice wins.
    function ensureAliasForUrl(url) {
        if (!url) return;
        if (serverUrlAliases[url]) return;
        serverUrlAliases[url] = uniqueAlias(defaultAliasForUrl(url), url);
        persistServerUrlAliases();
    }

    // Look up the alias for `url`, falling back to the URL itself so
    // call sites always have something printable.
    function aliasForUrl(url) {
        if (!url) return '';
        return serverUrlAliases[url] || url;
    }

    // Manually set an alias. Returns { ok: true } on success or
    // { ok: false, error } when the requested name is empty / collides
    // with another URL's alias / contains illegal characters. Callers
    // surface the error inline next to the alias input.
    function setUrlAlias(url, requested) {
        if (!url) return { ok: false, error: 'No URL to label' };
        var trimmed = String(requested || '').trim();
        if (!trimmed) return { ok: false, error: 'Alias cannot be empty' };
        if (!/^[A-Za-z0-9._-]{1,40}$/.test(trimmed)) {
            return { ok: false, error: 'Use letters, digits, dot, dash, underscore (max 40)' };
        }
        for (var k in serverUrlAliases) {
            if (Object.prototype.hasOwnProperty.call(serverUrlAliases, k)
                && k !== url
                && serverUrlAliases[k] === trimmed) {
                return { ok: false, error: 'Alias already used by another URL' };
            }
        }
        serverUrlAliases[url] = trimmed;
        persistServerUrlAliases();
        return { ok: true };
    }

    function removeAliasForUrl(url) {
        if (url && serverUrlAliases[url]) {
            delete serverUrlAliases[url];
            persistServerUrlAliases();
        }
    }

    // Migrate alias when the user edits an existing URL in place — the
    // user just retyped the address, not "this is a different
    // resource", so preserve the friendly name they already picked.
    function renameAliasForUrl(oldUrl, newUrl) {
        if (!oldUrl || !newUrl || oldUrl === newUrl) return;
        if (!serverUrlAliases[oldUrl]) {
            ensureAliasForUrl(newUrl);
            return;
        }
        // If the destination already has an alias, just drop the old one
        // (we can't have two URLs share the same alias).
        if (serverUrlAliases[newUrl]) {
            delete serverUrlAliases[oldUrl];
        } else {
            serverUrlAliases[newUrl] = serverUrlAliases[oldUrl];
            delete serverUrlAliases[oldUrl];
        }
        persistServerUrlAliases();
    }

    // Seed aliases for whatever URLs are already in the list on boot
    // (migration for workspaces saved before this feature shipped).
    serverUrls.forEach(ensureAliasForUrl);

    // Toggle the discovery connection for a single source URL. Used
    // by the plug button on each source-panel header in the discover
    // tree. Connected → drop the URL's services from the in-memory
    // list and flag the URL as disconnected. Disconnected / error /
    // first-run → kick off a fresh fetchServicesForUrl and splice
    // the returned services back into the list when it resolves.
    function toggleSourceConnection(url) {
        if (!url) return;
        var st = connectionStatuses[url] || 'disconnected';
        if (st === 'connected' || st === 'connecting') {
            connectionStatuses[url] = 'disconnected';
            if (Array.isArray(services)) {
                services = services.filter(function (s) { return s.originUrl !== url; });
            }
            render();
            return;
        }
        connectionStatuses[url] = 'connecting';
        render();
        if (typeof fetchServicesForUrl !== 'function') return;
        Promise.resolve(fetchServicesForUrl(url)).then(function (fresh) {
            if (!Array.isArray(services)) services = [];
            services = services.filter(function (s) { return s.originUrl !== url; });
            if (Array.isArray(fresh) && fresh.length) {
                services = services.concat(fresh);
            }
            render();
        }).catch(function () {
            render();
        });
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

    // #156 — favorites-only toggle. Persists per workspace so each
    // project remembers whether the operator likes the focused view.
    // Hydrated below where wsKey() / activeWorkspaceId are reachable.
    let favoritesOnly = false;
    function setFavoritesOnly(v) {
        favoritesOnly = !!v;
        try { localStorage.setItem(wsKey('bowire_favorites_only'), favoritesOnly ? '1' : '0'); }
        catch { /* ignore */ }
    }

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

    // Expanded-state of source-URL panels in the discover tree. Each
    // URL is a top-level expansion panel that wraps its services so
    // the sidebar reads as "this URL → these services" instead of a
    // flat list. The default is "everything open" (Set holds the
    // explicitly-collapsed entries) — first-time users see their
    // services without having to click a toggle, returning users get
    // their last layout back. Stored as the URLs the user has
    // collapsed so a new URL added later shows up open by default.
    const COLLAPSED_SOURCES_KEY = 'bowire_collapsed_sources';
    let collapsedSources = (function () {
        try {
            var raw = localStorage.getItem(COLLAPSED_SOURCES_KEY);
            if (!raw) return new Set();
            var arr = JSON.parse(raw);
            return new Set(Array.isArray(arr) ? arr : []);
        } catch { return new Set(); }
    })();
    function persistCollapsedSources() {
        try {
            localStorage.setItem(COLLAPSED_SOURCES_KEY, JSON.stringify(Array.from(collapsedSources)));
        } catch {}
    }
    function isSourceExpanded(originUrl) {
        return !collapsedSources.has(originUrl || '');
    }
    function toggleSourceExpanded(originUrl) {
        var key = originUrl || '';
        if (collapsedSources.has(key)) collapsedSources.delete(key);
        else collapsedSources.add(key);
        persistCollapsedSources();
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
    // #299 — unified right-side drawer. When Assistant + Help are
    // both open, they share one chrome with a tab strip; this tracks
    // which tab is active. Defaults to 'assistant'; flipped by the
    // toggle handlers whenever the user opens (or clicks the tab of)
    // a specific panel.
    let rightDrawerActiveTab = 'assistant';
    try {
        var _rd = localStorage.getItem('bowire_right_drawer_active_tab');
        // #164 — Tests joined Assistant + Help as right-drawer tabs.
        // (Console lives as a bottom-attached drawer per #164 v2 and
        // is NOT a valid right-drawer tab — a stale 'console' value
        // falls back to 'assistant'.) Whitelist the valid ids.
        if (_rd === 'assistant' || _rd === 'help' || _rd === 'tests' || _rd === 'activity') rightDrawerActiveTab = _rd;
    } catch { /* ignore */ }
    // #164 — Tests drawer state. Mirrors aiDrawerOpen / helpDrawerOpen;
    // when on, Tests joins the unified right-drawer tab strip with its
    // own pass/fail accessory + per-method assertions UI. Persisted so
    // the operator's choice is sticky across reloads.
    let testsDrawerOpen = false;
    try { testsDrawerOpen = localStorage.getItem('bowire_tests_drawer_open') === '1'; } catch { /* ignore */ }
    // #164 v2 — Console height for the bottom-attached drawer.
    // Clamped at render time (80 px floor, 70 % viewport ceiling) but
    // stored as raw px so resize survives reloads. Default 240 px is
    // wide enough to show 3-4 log rows without dominating the workbench.
    // #164 v3 — Activity-rail overflow popover. Opens when the user
    // clicks the '…' button that appears once a mode no longer fits in
    // the vertical rail (e.g. console drawer eats half the height).
    // Session-only; resetting on reload is fine because the menu is
    // ephemeral by nature.
    let railOverflowOpen = false;
    let consoleHeight = 240;
    try {
        var _ch = parseInt(localStorage.getItem('bowire_console_height') || '', 10);
        if (_ch > 0 && _ch < 4000) consoleHeight = _ch;
    } catch { /* ignore */ }
    function persistConsoleHeight() {
        try { localStorage.setItem('bowire_console_height', String(consoleHeight)); }
        catch { /* ignore */ }
    }
    // #168 — Workbench-wide action log. Central record of every
    // reversible operator action; surfaces as the Statusbar Activity
    // pill + an Activity tab in the unified right drawer + Ctrl/Cmd+Z.
    //
    // State shape (in-memory):
    //   { id, ts, kind, title, undoFn, redoFn, status }
    //     status: 'available' | 'undone' | 'expired'
    //
    // Persistence: metadata only (no closures). Reload restores the
    // timeline as 'expired' entries so the Activity tab still surfaces
    // what happened, just without the undo button. TTL aligned with
    // the trash (30 days); cap at ACTION_LOG_MAX entries so the log
    // doesn't grow unbounded under a long session.
    const ACTION_LOG_KEY = 'bowire_action_log';
    const ACTION_LOG_MAX = 200;
    const ACTION_LOG_TTL_MS = 30 * 24 * 60 * 60 * 1000;
    let actionLog = [];
    let actionLogRedoStack = [];
    let activityDrawerOpen = false;
    try { activityDrawerOpen = localStorage.getItem('bowire_activity_drawer_open') === '1'; } catch { /* ignore */ }

    function _loadActionLogFromStorage() {
        try {
            var raw = localStorage.getItem(wsKey(ACTION_LOG_KEY));
            if (!raw) return [];
            var parsed = JSON.parse(raw);
            if (!Array.isArray(parsed)) return [];
            var cutoff = Date.now() - ACTION_LOG_TTL_MS;
            return parsed
                .filter(function (e) { return e && typeof e.ts === 'number' && e.ts > cutoff; })
                .map(function (e) {
                    // Closures didn't survive the reload — entry stays
                    // visible in the timeline as a record of what
                    // happened, but undo is no longer available.
                    e.status = 'expired';
                    e.undoFn = null;
                    e.redoFn = null;
                    return e;
                });
        } catch { return []; }
    }
    function _persistActionLog() {
        try {
            var slim = actionLog.map(function (e) {
                return {
                    id: e.id,
                    ts: e.ts,
                    kind: e.kind,
                    title: e.title,
                    status: e.status === 'available' ? 'available' : e.status
                };
            });
            localStorage.setItem(wsKey(ACTION_LOG_KEY), JSON.stringify(slim));
        } catch { /* quota / disabled — non-fatal */ }
    }
    function recordAction(opts) {
        if (!opts || typeof opts.undo !== 'function') return null;
        var entry = {
            id: 'act_' + Math.random().toString(36).slice(2, 10),
            ts: Date.now(),
            kind: opts.kind || 'unknown',
            title: opts.title || opts.kind || 'Action',
            undoFn: opts.undo,
            redoFn: typeof opts.redo === 'function' ? opts.redo : null,
            status: 'available'
        };
        actionLog.unshift(entry);
        if (actionLog.length > ACTION_LOG_MAX) actionLog.length = ACTION_LOG_MAX;
        // Recording a new action invalidates the redo stack (matches
        // VS Code / IntelliJ semantics — a new edit after an undo
        // drops the redoable future).
        actionLogRedoStack = [];
        _persistActionLog();
        return entry;
    }
    function undoLastAction() {
        var i = actionLog.findIndex(function (e) {
            return e.status === 'available' && typeof e.undoFn === 'function';
        });
        if (i < 0) return null;
        var entry = actionLog[i];
        try {
            entry.undoFn();
            entry.status = 'undone';
            if (entry.redoFn) actionLogRedoStack.unshift(entry);
            _persistActionLog();
            return entry;
        } catch (err) {
            console.warn('[actionLog] undo failed', entry.kind, err);
            return null;
        }
    }
    function redoLastAction() {
        if (actionLogRedoStack.length === 0) return null;
        var entry = actionLogRedoStack.shift();
        if (!entry || typeof entry.redoFn !== 'function') return null;
        try {
            entry.redoFn();
            entry.status = 'available';
            _persistActionLog();
            return entry;
        } catch (err) {
            console.warn('[actionLog] redo failed', entry.kind, err);
            return null;
        }
    }
    function availableActionCount() {
        var n = 0;
        for (var i = 0; i < actionLog.length; i++) {
            if (actionLog[i].status === 'available') n++;
        }
        return n;
    }
    function clearActionLog() {
        actionLog = [];
        actionLogRedoStack = [];
        _persistActionLog();
    }
    // One-shot hydrate guard. wsKey() resolves off activeWorkspaceId
    // which isn't set until much later in prologue's init, so the load
    // can't run at the declaration site. render() calls this on every
    // pass; the flag means we only pay the localStorage read once.
    let _actionLogHydrated = false;
    function hydrateActionLog() {
        if (_actionLogHydrated) return;
        _actionLogHydrated = true;
        actionLog = _loadActionLogFromStorage();
    }

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
    // #296 — "Add to…" menu in the method header. Anchored to the
    // header button so clicking outside closes it. Stays as a single
    // boolean because at most one method header is on screen at a time.
    let methodAddToMenuOpen = false;
    // #166 — Keyboard shortcut sheet (Ctrl/Cmd+/). Session-only; the
    // sheet is informational, no benefit in remembering it across
    // reloads.
    let shortcutSheetOpen = false;

    // #169 — Hint dismiss store. Single localStorage key holds an
    // object { hintId → { dismissedAt, scope: 'session'|'permanent',
    // label? } }. Session-scoped entries are echoed to sessionStorage
    // so they vanish on cold start; permanent ones survive until the
    // operator re-enables them from Settings → General → "Hints and
    // warnings". The store is the single source of truth for both
    // renderAlertBar and renderEmptyCard so the Settings list can
    // show every dismissed hint regardless of which surface dismissed
    // it.
    const BOWIRE_DISMISSED_HINTS_KEY = 'bowire_dismissed_hints';
    let bowireDismissedHints = {};
    try {
        var rawDH = localStorage.getItem(BOWIRE_DISMISSED_HINTS_KEY);
        if (rawDH) {
            var parsedDH = JSON.parse(rawDH);
            if (parsedDH && typeof parsedDH === 'object') bowireDismissedHints = parsedDH;
        }
    } catch { /* corrupt — start fresh */ }
    function persistDismissedHints() {
        try { localStorage.setItem(BOWIRE_DISMISSED_HINTS_KEY, JSON.stringify(bowireDismissedHints)); }
        catch { /* quota / disabled storage — survive gracefully */ }
    }
    function isHintDismissed(hintId) {
        if (!hintId) return false;
        var entry = bowireDismissedHints[hintId];
        if (entry && entry.scope === 'permanent') return true;
        if (entry && entry.scope === 'session') {
            try { return sessionStorage.getItem('bowire_hint_session_' + hintId) === '1'; }
            catch { return true; }
        }
        return false;
    }
    function dismissHint(hintId, scope, label) {
        if (!hintId) return;
        var when = Date.now();
        bowireDismissedHints[hintId] = {
            dismissedAt: when,
            scope: scope === 'permanent' ? 'permanent' : 'session',
            label: label || bowireDismissedHints[hintId]?.label || hintId
        };
        persistDismissedHints();
        if (scope === 'session') {
            try { sessionStorage.setItem('bowire_hint_session_' + hintId, '1'); }
            catch { /* ignore */ }
        }
    }
    function undismissHint(hintId) {
        if (!hintId) return;
        delete bowireDismissedHints[hintId];
        persistDismissedHints();
        try { sessionStorage.removeItem('bowire_hint_session_' + hintId); }
        catch { /* ignore */ }
    }
    function resetAllDismissedHints() {
        var ids = Object.keys(bowireDismissedHints);
        bowireDismissedHints = {};
        persistDismissedHints();
        for (var i = 0; i < ids.length; i++) {
            try { sessionStorage.removeItem('bowire_hint_session_' + ids[i]); }
            catch { /* ignore */ }
        }
    }
    function listDismissedHints() {
        return Object.keys(bowireDismissedHints).map(function (id) {
            var e = bowireDismissedHints[id];
            return {
                id: id,
                label: e.label || id,
                scope: e.scope,
                dismissedAt: e.dismissedAt
            };
        }).sort(function (a, b) { return (b.dismissedAt || 0) - (a.dismissedAt || 0); });
    }
    // #116 — selected workspace in the rail-mode detail view.
    let workspacesSelectedId = null;
    // #192 — Workspaces tree state. Per-workspace expansion + an
    // optional sub-node selection so the operator can click e.g. a
    // specific URL leaf and the main pane jumps straight to that URL's
    // editor. Persisted across reloads so collapse state survives.
    let workspaceTreeExpanded = {};
    try {
        var rawWTE = localStorage.getItem('bowire_workspace_tree_expanded');
        if (rawWTE) {
            var parsedWTE = JSON.parse(rawWTE);
            if (parsedWTE && typeof parsedWTE === 'object') workspaceTreeExpanded = parsedWTE;
        }
    } catch { /* corrupt — start fresh */ }
    function persistWorkspaceTreeExpanded() {
        try { localStorage.setItem('bowire_workspace_tree_expanded', JSON.stringify(workspaceTreeExpanded)); }
        catch { /* quota / disabled — survive */ }
    }
    function isWorkspaceTreeNodeExpanded(key, defaultOpen) {
        if (workspaceTreeExpanded[key] === undefined) return !!defaultOpen;
        return !!workspaceTreeExpanded[key];
    }
    function toggleWorkspaceTreeNode(key, defaultOpen) {
        var cur = isWorkspaceTreeNodeExpanded(key, defaultOpen);
        workspaceTreeExpanded[key] = !cur;
        persistWorkspaceTreeExpanded();
    }
    // Sub-selection inside a workspace subtree: { wsId, kind, value }
    // kind ∈ 'workspace' | 'sources' | 'url' | 'environments' |
    //        'collections' | 'recordings' | 'settings'
    let workspaceTreeSelection = { kind: 'workspace' };
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
    // Boot migration — rail modes retired in favour of the Workspace-
    // detail pane (workspaces own their sources / collections /
    // environments now). Stale localStorage entries map to
    // 'workspaces' so existing installs land where the new
    // management lives instead of on a button-less mode.
    if (railMode === 'sources' || railMode === 'collections'
        || railMode === 'environments' || railMode === 'recordings') {
        railMode = 'workspaces';
        try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
    }
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
    // activeWorkspaceId was already claimed at the top of the module
    // (early boot-order pin so wsKey() routes correctly during the
    // initial load-fns). Re-read here too — covers the edge case
    // where the early read missed (localStorage temporarily blocked).
    try {
        var rawWs = localStorage.getItem('bowire_workspaces');
        if (rawWs) {
            var parsed = JSON.parse(rawWs);
            if (Array.isArray(parsed)) workspaces = parsed;
        }
        if (!activeWorkspaceId) {
            activeWorkspaceId = localStorage.getItem('bowire_active_workspace') || null;
        }
    } catch { /* ignore */ }
    // First-run behaviour. Default leaves the workspace list empty so
    // the operator sees the "Create your first workspace" Home CTA
    // and understands the concept up front instead of finding a
    // pre-seeded "Personal" they didn't ask for.
    //
    // The legacy auto-seed path is still available via the
    // bowire_auto_create_initial_workspace toggle (Settings → General).
    // Users who relied on the old "boot straight into Personal" flow
    // can flip it on once and stop seeing the empty home.
    // Resolution order, highest-precedence first:
    //   1. Host-side config (appsettings.json: Bowire.AutoCreateInitialWorkspace,
    //      CLI flag --auto-create-initial-workspace, env var
    //      Bowire__AutoCreateInitialWorkspace). Lets the admin/host
    //      enforce a baseline for every operator.
    //   2. Per-browser localStorage toggle (Settings → General).
    //      Lets an individual user opt in/out on their own machine.
    //   3. Default false — empty workspace list on first run.
    var autoCreateInitial = false;
    if (config && config.autoCreateInitialWorkspace === true) {
        autoCreateInitial = true;
    } else {
        try {
            autoCreateInitial = localStorage.getItem('bowire_auto_create_initial_workspace') === 'true';
        } catch { /* ignore */ }
    }
    if (workspaces.length === 0 && autoCreateInitial) {
        workspaces.push({
            id: 'personal',
            name: 'Personal',
            color: '#6366f1',
            createdAt: Date.now(),
            lastOpenedAt: Date.now(),
            storage: 'disk'
        });
        activeWorkspaceId = 'personal';
    }
    if (workspaces.length === 0) {
        // No workspaces at all (first-run, auto-create off). Leave
        // activeWorkspaceId null so wsKey() falls through to its
        // base-key shape and downstream code that gates on
        // `activeWorkspace()` doesn't crash. Home renders the
        // "Create your first workspace" CTA in this state.
        activeWorkspaceId = null;
    } else if (!activeWorkspaceId || !workspaces.find(function (w) { return w.id === activeWorkspaceId; })) {
        activeWorkspaceId = workspaces[0].id;
    }
    // #116 Phase 2 — wraps a flat storage key into a workspace-
    // scoped one ('bowire_recordings' → 'bowire_ws_<id>_recordings').
    // Global UI prefs (theme, sidebar width, drawer state, etc.) do
    // NOT route through this — they intentionally stay shared. The
    // per-workspace store sites call wsKey at every localStorage
    // setItem / getItem so a workspace switch picks up a fresh slate.
    function wsKey(baseKey) {
        // No active workspace = no scope. Route to a one-off orphan
        // namespace instead of the unscoped legacy key, so a
        // "delete all workspaces" sweep doesn't accidentally surface
        // v1.x global recordings / collections / &c. when the next
        // workspace is created. The orphan namespace gets cleared by
        // the deleteWorkspace cascade below.
        if (!activeWorkspaceId) {
            return 'bowire_ws_orphan_' + String(baseKey).replace(/^bowire_/, '');
        }
        return 'bowire_ws_' + activeWorkspaceId + '_'
            + String(baseKey).replace(/^bowire_/, '');
    }
    // #192 — read a per-workspace localStorage key for a workspace
    // *other* than the active one. Used by the Workspaces tree to
    // surface source counts / URL lists for collapsed-but-not-active
    // workspaces without having to switch to them.
    function wsKeyFor(workspaceId, baseKey) {
        if (!workspaceId) return baseKey;
        return 'bowire_ws_' + workspaceId + '_'
            + String(baseKey).replace(/^bowire_/, '');
    }
    function readWorkspaceUrls(workspaceId) {
        if (workspaceId === activeWorkspaceId) return serverUrls.slice();
        try {
            var raw = localStorage.getItem(wsKeyFor(workspaceId, SERVER_URLS_KEY));
            if (!raw) return [];
            var parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
        } catch { return []; }
    }
    function readWorkspaceJsonList(workspaceId, baseKey) {
        if (!workspaceId) return [];
        try {
            var raw = localStorage.getItem(wsKeyFor(workspaceId, baseKey));
            if (!raw) return [];
            var parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
        } catch { return []; }
    }

    // #156 — late hydration of favoritesOnly now that wsKey() is
    // reachable. Declared as `let` at the top of the module so the
    // state is module-scoped; only the read-from-localStorage happens
    // here once activeWorkspaceId is set.
    try { favoritesOnly = localStorage.getItem(wsKey('bowire_favorites_only')) === '1'; }
    catch { /* ignore */ }

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

    // #A — Self-contained workspaces. Reverses the #146 (Phase 2)
    // collapse: for every workspace, copy the envs it was including
    // out of the global shared store back into its own per-workspace
    // bucket (wsKey(ENVIRONMENTS_KEY) → bowire_ws_<id>_environments).
    // The shared store stays put — leaving it behind is the rollback
    // path if this migration ever needs to be reverted. After this
    // ran once, getEnvironments / saveEnvironments only see the
    // workspace's own list.
    //
    // The migration also strips includeAllEnvironments +
    // includedEnvironmentIds from the workspace meta so the new code
    // doesn't have to special-case the legacy shape.
    //
    // Marker bowire_envs_workspace_owned_v1 prevents re-running.
    (function migrateEnvsToWorkspaceOwned() {
        try {
            if (localStorage.getItem('bowire_envs_workspace_owned_v1') === '1') return;
            if (!Array.isArray(workspaces) || workspaces.length === 0) {
                localStorage.setItem('bowire_envs_workspace_owned_v1', '1');
                return;
            }
            var shared = [];
            try {
                var rawShared = localStorage.getItem('bowire_environments_shared');
                if (rawShared) {
                    var parsed = JSON.parse(rawShared);
                    if (Array.isArray(parsed)) shared = parsed;
                }
            } catch { /* ignore */ }
            for (var wi = 0; wi < workspaces.length; wi++) {
                var w = workspaces[wi];
                var includedIds;
                if (w.includeAllEnvironments) {
                    includedIds = shared.map(function (e) { return e.id; });
                } else if (Array.isArray(w.includedEnvironmentIds)) {
                    includedIds = w.includedEnvironmentIds;
                } else {
                    // Workspace had no explicit inclusion meta — give
                    // it everything. Empty workspaces stay empty.
                    includedIds = shared.map(function (e) { return e.id; });
                }
                var includedSet = new Set(includedIds);
                var workspaceEnvs = shared.filter(function (e) { return includedSet.has(e.id); });
                // Deep-clone so two workspaces that both included the
                // same env don't end up sharing references to the same
                // vars object — mutating one would otherwise leak into
                // the other.
                var copies;
                try { copies = JSON.parse(JSON.stringify(workspaceEnvs)); }
                catch { copies = workspaceEnvs.slice(); }
                try {
                    localStorage.setItem(
                        'bowire_ws_' + w.id + '_environments',
                        JSON.stringify(copies)
                    );
                } catch { /* skip on quota / SecurityError */ }
                // Drop inclusion meta — workspace is self-contained now.
                delete w.includeAllEnvironments;
                delete w.includedEnvironmentIds;
            }
            try {
                localStorage.setItem('bowire_workspaces', JSON.stringify(workspaces));
                localStorage.setItem('bowire_envs_workspace_owned_v1', '1');
            } catch { /* ignore */ }
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
        // Explicit null = "no active workspace" (e.g. after deleting
        // the last one). Don't auto-fallback to workspaces[0] — that
        // hides the null state and the topbar chip would show a
        // workspace the operator didn't pick. The fallback only kicks
        // in when activeWorkspaceId points at a stale id that no
        // longer exists (e.g. an externally-edited bowire_active_workspace
        // file) — there's still at least one workspace, just nothing
        // currently selected, so the first-listed entry is a sane
        // landing.
        if (!activeWorkspaceId) return null;
        var hit = workspaces.find(function (w) { return w.id === activeWorkspaceId; });
        if (hit) return hit;
        return workspaces.length > 0 ? workspaces[0] : null;
    }
    // Workspace names must be unique. Case-insensitive trim-compared so
    // "Staging" / "staging " / "STAGING" don't sneak past as duplicates.
    function _isWorkspaceNameTaken(name, excludeId) {
        var norm = String(name || '').trim().toLowerCase();
        if (!norm) return false;
        return workspaces.some(function (w) {
            return w.id !== excludeId
                && String(w.name || '').trim().toLowerCase() === norm;
        });
    }
    // Picks the next free "Workspace N" — used only when the caller
    // didn't supply an explicit name (e.g. quick-create flows). For
    // user-supplied names we reject duplicates rather than silently
    // suffixing; consistency with renameWorkspace's behaviour.
    function _nextDefaultWorkspaceName() {
        var n = workspaces.length + 1;
        while (_isWorkspaceNameTaken('Workspace ' + n)) n++;
        return 'Workspace ' + n;
    }
    // Returns the new workspace on success, or null if the explicit
    // name was already taken. Callers should toast on null so the
    // operator sees why their create attempt didn't land.
    function createWorkspace(name, color) {
        var requested = String(name || '').trim();
        if (requested) {
            if (_isWorkspaceNameTaken(requested)) {
                if (typeof toast === 'function') {
                    toast('A workspace named "' + requested + '" already exists.', 'error');
                }
                return null;
            }
        } else {
            requested = _nextDefaultWorkspaceName();
        }
        var id = 'ws_' + Math.random().toString(36).slice(2, 10);
        var ws = {
            id: id,
            name: requested,
            color: color || '#6366f1',
            createdAt: Date.now(),
            lastOpenedAt: Date.now(),
            // Self-contained workspaces (#A): envs live under
            // wsKey(ENVIRONMENTS_KEY), not in a cross-workspace shared
            // pool, so there's no inclusion list to track. Workspaces
            // start empty; the operator creates envs via the env
            // dropdown's "+ New environment…" item or via the env
            // overview's "+ New environment" button.
            // #212 Phase 0 — workspace storage mode. Drives every
            // entity store (recordings, env-sync, future collections /
            // flows). Default 'disk' (~/.bowire/workspaces/<id>/);
            // operator can opt into 'browser-only' via Settings →
            // General → Storage.
            storage: 'disk',
        };
        workspaces.push(ws);
        activeWorkspaceId = id;
        persistWorkspaces();
        return ws;
    }

    // #127 follow-up — "Save As" / Fork-Workspace affordance. v1.9
    // had Save-As at the workspace dropdown; v2.0's auto-save model
    // dropped explicit Save-As. This helper rebuilds the path: create
    // a fresh workspace under <newName> + clone every _WORKSPACE_DATA_KEYS
    // value + every preset-mode bucket from the source workspace's
    // localStorage namespace into the new one. Returns the new
    // workspace, or null when the create itself fails (name taken).
    function duplicateWorkspace(sourceId, newName) {
        var source = workspaces.find(function (w) { return w.id === sourceId; });
        if (!source) return null;
        var fresh = createWorkspace(newName || (source.name + ' (copy)'), source.color);
        if (!fresh) return null;
        // _WORKSPACE_DATA_KEYS is the canonical "everything this
        // workspace owns" list; iterating it covers URLs / envs /
        // collections / recordings / flows / pins / favorites /
        // benchmarks / open tabs without bespoke per-key code.
        try {
            _WORKSPACE_DATA_KEYS.forEach(function (k) {
                var raw = localStorage.getItem(wsKeyFor(sourceId, k));
                if (raw !== null) {
                    localStorage.setItem(wsKeyFor(fresh.id, k), raw);
                }
            });
            // Presets framework storage — same shape as data keys but
            // keyed under bowire_presets_<mode> per workspace. Copy
            // each known mode bucket so saved configs travel too.
            if (Array.isArray(_WORKSPACE_PRESET_MODES)) {
                _WORKSPACE_PRESET_MODES.forEach(function (mode) {
                    var pkey = 'bowire_presets_' + mode;
                    var raw = localStorage.getItem(wsKeyFor(sourceId, pkey));
                    if (raw !== null) {
                        localStorage.setItem(wsKeyFor(fresh.id, pkey), raw);
                    }
                });
            }
            markSaved('duplicate');
        } catch (e) {
            console.warn('[bowire] duplicateWorkspace failed mid-copy', e);
        }
        return fresh;
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
    // #212 Phase 0 — workspace-level storage decision. One toggle per
    // workspace drives every entity store (recordings, env-sync,
    // future collections / flows). Replaces the per-subsystem
    // recordingStorageMode field (kept as a legacy alias for
    // back-compat in case any caller still reads it).
    //
    //   'disk'         (default) → ~/.bowire/workspaces/<id>/ is the
    //                              source of truth. localStorage acts
    //                              as a best-effort cache and silently
    //                              degrades on quota errors (so a huge
    //                              recording doesn't blow the tab).
    //                              Recordings initial-load is manifest-
    //                              only; step bodies hydrate on demand.
    //   'browser-only'           → localStorage is the only store;
    //                              no disk writes for ANY entity.
    //                              Subject to the ~5-10 MB browser
    //                              quota; useful for sandbox / privacy
    //                              workflows.
    //
    // #196 will add a 'git' value (workspace lives as a checked-out
    // folder); browser-only is rejected on a git workspace.
    function getWorkspaceStorageMode(ws) {
        var target = ws || (typeof activeWorkspace === 'function' ? activeWorkspace() : null);
        if (!target) return 'disk';
        var s = target.storage;
        if (s === 'browser-only') return 'browser-only';
        if (s === 'disk') return 'disk';
        // Fallback to the retired recordingStorageMode for workspaces
        // that haven't been migrated yet (migration runs once on
        // boot, but a freshly-imported pre-#212 workspace can land
        // here mid-session).
        var legacy = target.recordingStorageMode;
        if (legacy === 'browser-only') return 'browser-only';
        return 'disk';
    }
    function setWorkspaceStorageMode(id, mode) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return;
        ws.storage = (mode === 'browser-only') ? 'browser-only' : 'disk';
        // Mirror onto the legacy field so any old caller still sees
        // the same answer. Recordings code reads through
        // getRecordingStorageMode → getWorkspaceStorageMode, so this
        // mirror is just defensive.
        ws.recordingStorageMode = ws.storage;
        persistWorkspaces();
    }

    // #196 Phase 2.3 — per-workspace storage root override. When set
    // (typically by the operator pointing the workspace at a checked-
    // out git folder), backend stores resolve under it via
    // BowireUserContext.GetWorkspacePath(workspaceId, storageRoot, …)
    // instead of the legacy ~/.bowire/workspaces/<id>/ layout. Only
    // meaningful for storage === 'disk'; the value is persisted but
    // ignored for 'browser-only'. Empty string clears the override.
    function getWorkspaceStorageRoot(ws) {
        var target = ws || (typeof activeWorkspace === 'function' ? activeWorkspace() : null);
        if (!target) return null;
        var raw = target.storageRoot;
        if (typeof raw !== 'string') return null;
        var trimmed = raw.trim();
        return trimmed.length > 0 ? trimmed : null;
    }
    function setWorkspaceStorageRoot(id, path) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return;
        var trimmed = (typeof path === 'string') ? path.trim() : '';
        if (trimmed.length === 0) {
            // Clear → drop the field so the workspace JSON stays clean
            // for installs that never opted in. delete is fine on a
            // plain workspace object; persistWorkspaces serialises
            // whatever shape is left.
            delete ws.storageRoot;
        } else {
            ws.storageRoot = trimmed;
        }
        persistWorkspaces();
    }

    // #193 Phase 2 — plugin pins (Dictionary<protocolId, semverString>)
    // declared in the .bww file's `pluginPins` field. Lives in
    // localStorage under wsKey('bowire_plugin_pins'), so the existing
    // workspace export/import machinery round-trips it via
    // _WORKSPACE_DATA_KEYS without bespoke wiring. Empty / missing
    // means no requirement, current behaviour preserved.
    var PLUGIN_PINS_KEY = 'bowire_plugin_pins';
    function getWorkspacePluginPins(wsId) {
        var id = wsId || activeWorkspaceId;
        if (!id) return {};
        try {
            var raw = localStorage.getItem(_wsKeyFor(id, PLUGIN_PINS_KEY));
            if (!raw) return {};
            var parsed = JSON.parse(raw);
            return (parsed && typeof parsed === 'object' && !Array.isArray(parsed))
                ? parsed : {};
        } catch { return {}; }
    }
    function setWorkspacePluginPins(wsId, pins) {
        var id = wsId || activeWorkspaceId;
        if (!id) return false;
        var safe = (pins && typeof pins === 'object' && !Array.isArray(pins))
            ? pins : {};
        // Drop empty-string versions silently — they're noise, not a
        // valid semver constraint. Keep null + non-string values out
        // for the same reason; the .bww schema is Dictionary<string, string>.
        var cleaned = {};
        Object.keys(safe).forEach(function (k) {
            var v = safe[k];
            if (typeof v === 'string' && v.length > 0) cleaned[k] = v;
        });
        try {
            if (Object.keys(cleaned).length === 0) {
                localStorage.removeItem(_wsKeyFor(id, PLUGIN_PINS_KEY));
            } else {
                localStorage.setItem(_wsKeyFor(id, PLUGIN_PINS_KEY),
                    JSON.stringify(cleaned));
            }
            return true;
        } catch { return false; }
    }

    // #193 Phase 2 — pin-check against loaded protocol registry on
    // workspace open. Result shape mirrors checkMissingPlugins(rec)
    // in recording.js so a future unified pin-check modal can share
    // the rendering surface. The diff is computed client-side from
    // /api/plugins/protocols (already loaded for the recording-side
    // pin check) — no new endpoint required.
    //
    // Per-session ignore-list keyed by workspace id so the operator
    // can dismiss the banner for the current workspace + Bowire
    // session without persisting. Crossing a restart re-surfaces the
    // check so an operator who forgot can't silently keep replaying
    // against a missing protocol.
    var _pinCheckIgnoredThisSession = {};
    async function checkWorkspacePluginPins(wsId) {
        var id = wsId || activeWorkspaceId;
        if (!id) return { missing: [], wrongVersion: [], catalog: {} };
        var pins = getWorkspacePluginPins(id);
        var pinIds = Object.keys(pins);
        if (pinIds.length === 0) return { missing: [], wrongVersion: [], catalog: {} };

        var info = null;
        try {
            var r = await fetch(config.prefix + '/api/plugins/protocols');
            if (r.ok) info = await r.json();
        } catch { /* offline / embedded — treat as 'all ok' so we don't nag */ }
        if (!info) return { missing: [], wrongVersion: [], catalog: {} };

        var loaded = {};
        (info.loaded || []).forEach(function (pid) { loaded[String(pid).toLowerCase()] = true; });
        var catalog = info.catalog || {};
        var missing = [];
        pinIds.forEach(function (pid) {
            var lower = String(pid).toLowerCase();
            if (loaded[lower]) return;
            missing.push({ id: lower, packageId: catalog[lower] || null, version: pins[pid] });
        });
        // wrongVersion left empty in Phase 2.1 — assemblies don't
        // expose their semver through /api/plugins/protocols today.
        // Folded in as soon as the registry surfaces it (#196 follow-up).
        return { missing: missing, wrongVersion: [], catalog: catalog };
    }
    function ignoreWorkspacePinCheckForSession(wsId) {
        var id = wsId || activeWorkspaceId;
        if (id) _pinCheckIgnoredThisSession[id] = true;
    }
    function isWorkspacePinCheckIgnored(wsId) {
        var id = wsId || activeWorkspaceId;
        return id ? !!_pinCheckIgnoredThisSession[id] : false;
    }

    // Cached result for the active workspace so render() can render
    // synchronously. null = not checked yet / hidden; {missing,
    // installing, perPackage, error, wsId} = banner active. Shape
    // mirrors recording.js's pluginCheckState so the existing
    // "post-install restart" modal in renderPluginCheckModal can be
    // reused for the workspace flow once item 3 lands.
    var workspacePinCheckState = null;

    async function triggerWorkspacePinCheck(wsId) {
        var id = wsId || activeWorkspaceId;
        if (!id) { workspacePinCheckState = null; return; }
        if (isWorkspacePinCheckIgnored(id)) { workspacePinCheckState = null; return; }
        var result = await checkWorkspacePluginPins(id);
        if (result.missing.length === 0 && result.wrongVersion.length === 0) {
            workspacePinCheckState = null;
        } else {
            workspacePinCheckState = {
                wsId: id,
                missing: result.missing,
                wrongVersion: result.wrongVersion,
                installing: false,
                installedAll: false,
                error: null,
                perPackage: {}
            };
        }
        try { if (typeof render === 'function') render(); } catch { /* embedded host without render */ }
    }
    function dismissWorkspacePinCheck() {
        if (workspacePinCheckState) ignoreWorkspacePinCheckForSession(workspacePinCheckState.wsId);
        workspacePinCheckState = null;
        try { if (typeof render === 'function') render(); } catch { /* ignore */ }
    }
    // Cached /api/plugins/protocols result so the pin editor's
    // "available protocols" dropdown and the pin-check banner share
    // one fetch. Shape: { loaded: [<id>], catalog: { <id>: <packageId> } }.
    // First read kicks off the fetch + a re-render; subsequent reads
    // hit the cache. Invalidate by setting _pluginCatalogCache = null
    // (e.g. after a plugin install to surface a freshly-loaded one).
    var _pluginCatalogCache = null;
    var _pluginCatalogFetching = false;
    function ensurePluginCatalog() {
        if (_pluginCatalogCache || _pluginCatalogFetching) return;
        _pluginCatalogFetching = true;
        fetch(config.prefix + '/api/plugins/protocols')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (info) {
                _pluginCatalogCache = info || { loaded: [], catalog: {} };
                _pluginCatalogFetching = false;
                try { if (typeof render === 'function') render(); } catch { /* ignore */ }
            })
            .catch(function () {
                _pluginCatalogCache = { loaded: [], catalog: {} };
                _pluginCatalogFetching = false;
                try { if (typeof render === 'function') render(); } catch { /* ignore */ }
            });
    }
    function getCachedPluginCatalog() {
        return _pluginCatalogCache || { loaded: [], catalog: {} };
    }
    function invalidatePluginCatalogCache() { _pluginCatalogCache = null; }

    async function installAllWorkspacePins() {
        var st = workspacePinCheckState;
        if (!st || st.installing) return;
        st.installing = true; st.error = null;
        render();
        var anyFailed = false;
        for (var i = 0; i < st.missing.length; i++) {
            var entry = st.missing[i];
            if (!entry.packageId) {
                st.perPackage[entry.id] = 'failed';
                anyFailed = true;
                continue;
            }
            st.perPackage[entry.id] = 'installing';
            render();
            try {
                var resp = await fetch(config.prefix + '/api/plugins/install', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ packageId: entry.packageId })
                });
                if (!resp.ok) throw new Error('HTTP ' + resp.status);
                st.perPackage[entry.id] = 'done';
            } catch {
                st.perPackage[entry.id] = 'failed';
                anyFailed = true;
            }
            render();
        }
        st.installing = false;
        st.installedAll = !anyFailed;
        if (anyFailed) st.error = 'Some installs failed. Check the workbench logs.';
        render();
    }

    // #196 Phase 2.4 — git-backed FS-watch SSE subscriber. Opens an
    // EventSource against the standalone CLI's /api/workspace/events
    // route and surfaces a sticky toast with a "Reload" action on any
    // received event. The watcher debounces server-side at 300 ms, so
    // the burst from an editor save lands as one or two events at most.
    //
    // Per-session state so a workspace switch (which triggers a full
    // page reload) starts fresh, and so subsequent events within one
    // session don't stack a second toast on top of an undismissed one
    // — the existing toast is the operator's signal that "the disk
    // moved since you last reloaded"; nagging twice is noise.
    var _fsWatchSource = null;
    var _fsWatchToast = null;
    function startWorkspaceFsWatchSubscriber(storageRoot) {
        if (!storageRoot || _fsWatchSource) return;
        var url = config.prefix + '/api/workspace/events?storageRoot=' + encodeURIComponent(storageRoot);
        try {
            _fsWatchSource = new EventSource(url);
        } catch (e) {
            _fsWatchSource = null;
            return;
        }
        _fsWatchSource.onmessage = function (evt) {
            if (_fsWatchToast || typeof toast !== 'function') return;
            var payload = null;
            try { payload = JSON.parse(evt.data); } catch { /* tolerate */ }
            var hint = (payload && payload.path)
                ? '"' + payload.path + '" changed on disk.'
                : 'Workspace files changed on disk.';
            _fsWatchToast = toast(hint + ' Reload to pick up the new state?', 'info', {
                duration: 0, // sticky — explicit dismiss only
                action: {
                    label: 'Reload',
                    onClick: function () {
                        try { window.location.reload(); }
                        catch { /* embedded host */ }
                    }
                }
            });
            // Clear the deduplicating reference when the operator
            // dismisses the toast (either via the close button or
            // the action button — both pull the node out of the DOM).
            // Poll on rAF so a future event can re-surface the toast
            // without us needing to monkey-patch dismissToast.
            var _watchDismissed = function () {
                if (!_fsWatchToast || !_fsWatchToast.isConnected) {
                    _fsWatchToast = null;
                    return;
                }
                requestAnimationFrame(_watchDismissed);
            };
            requestAnimationFrame(_watchDismissed);
        };
        _fsWatchSource.onerror = function () {
            // 501 from an embedded host without AddBowireGitWorkspace,
            // 4xx from a bad storageRoot, or transient connection
            // drop — close cleanly so a stale handle doesn't pin a
            // connection. The next workspace open re-arms.
            try { _fsWatchSource && _fsWatchSource.close(); } catch { /* ignore */ }
            _fsWatchSource = null;
        };
    }

    // Back-compat aliases — recording.js + any other caller still
    // imports these names. Both delegate to the workspace-level
    // helpers. Drop these once every call site has migrated.
    function getRecordingStorageMode(ws) { return getWorkspaceStorageMode(ws); }
    function setWorkspaceRecordingStorageMode(id, mode) { setWorkspaceStorageMode(id, mode); }

    // One-shot migration: derive workspace.storage from the legacy
    // recordingStorageMode for every existing workspace. Stamps a
    // marker so subsequent boots skip the walk. Runs before any UI
    // reads workspace.storage so the toggle reflects the migrated
    // value on first render.
    function migrateWorkspaceStorage() {
        var marker = 'bowire_workspace_storage_v1';
        try {
            if (localStorage.getItem(marker) === 'done') return;
        } catch { /* ignore */ }
        var dirty = false;
        for (var i = 0; i < workspaces.length; i++) {
            var w = workspaces[i];
            if (w.storage === 'disk' || w.storage === 'browser-only') continue;
            // 'both' / 'disk-only' / undefined all collapse to 'disk'.
            // 'browser-only' carries through.
            w.storage = (w.recordingStorageMode === 'browser-only') ? 'browser-only' : 'disk';
            dirty = true;
        }
        if (dirty) persistWorkspaces();
        try { localStorage.setItem(marker, 'done'); } catch { /* ignore */ }
    }
    try { migrateWorkspaceStorage(); } catch { /* ignore */ }

    // Retired with the self-contained workspaces refactor (#A) — envs
    // are workspace-owned, not subscribed-to. Stub stays for any
    // straggler call sites; a follow-up sweep removes them.
    function setWorkspaceIncludeAllEnvs(_id, _value) { /* no-op */ }

    function renameWorkspace(id, name) {
        var ws = workspaces.find(function (w) { return w.id === id; });
        if (!ws) return false;
        var trimmed = String(name || '').trim();
        if (!trimmed) return false;
        if (_isWorkspaceNameTaken(trimmed, id)) {
            if (typeof toast === 'function') {
                toast('A workspace named "' + trimmed + '" already exists.', 'error');
            }
            return false;
        }
        ws.name = trimmed;
        persistWorkspaces();
        return true;
    }
    function deleteWorkspace(id) {
        var idx = workspaces.findIndex(function (w) { return w.id === id; });
        if (idx < 0) return false;
        workspaces.splice(idx, 1);

        // Workspace = project folder (#155): per-workspace localStorage
        // namespace gets purged on delete so a leftover wsKeyFor(id, ...)
        // entry doesn't haunt the next workspace that happens to share
        // a key. Covers every WORKSPACE_DATA_KEY + every known preset
        // mode bucket.
        try {
            if (Array.isArray(_WORKSPACE_DATA_KEYS)) {
                _WORKSPACE_DATA_KEYS.forEach(function (k) {
                    localStorage.removeItem(wsKeyFor(id, k));
                });
            }
            if (Array.isArray(_WORKSPACE_BROWSER_STATE_KEYS)) {
                _WORKSPACE_BROWSER_STATE_KEYS.forEach(function (k) {
                    localStorage.removeItem(wsKeyFor(id, k));
                });
            }
            if (Array.isArray(_WORKSPACE_PRESET_MODES)) {
                _WORKSPACE_PRESET_MODES.forEach(function (mode) {
                    localStorage.removeItem(wsKeyFor(id, 'bowire_presets_' + mode));
                });
            }
        } catch { /* localStorage quota / disabled — best-effort cleanup */ }

        if (activeWorkspaceId === id) {
            if (workspaces.length === 0) {
                // Honest empty state: no workspace = no scope.
                // Auto-seeding a "Personal" workspace looked plausible
                // in the dropdown + settings tree but quietly broke
                // every per-workspace persist path (in-memory caches
                // still pointed at the deleted namespace; new URLs /
                // recordings didn't render because the load-fns ran
                // against the old state). The user-facing fix is to
                // mirror reality — empty workspaces[] means no active
                // workspace, every rail surfaces the same "create one
                // to start" empty state, and the topbar switcher reads
                // "[no workspace]".
                activeWorkspaceId = null;
                try {
                    if (Array.isArray(_WORKSPACE_DATA_KEYS)) {
                        _WORKSPACE_DATA_KEYS.forEach(function (k) {
                            localStorage.removeItem('bowire_ws_orphan_'
                                + String(k).replace(/^bowire_/, ''));
                        });
                    }
                    if (Array.isArray(_WORKSPACE_BROWSER_STATE_KEYS)) {
                        _WORKSPACE_BROWSER_STATE_KEYS.forEach(function (k) {
                            localStorage.removeItem('bowire_ws_orphan_'
                                + String(k).replace(/^bowire_/, ''));
                        });
                    }
                } catch { /* best-effort */ }
                // Reset the in-memory state so the UI doesn't keep
                // showing recordings / urls / collections from the
                // deleted workspace. Load-fns run against the empty
                // orphan namespace and come back empty next render.
                try {
                    if (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) serverUrls.length = 0;
                    if (typeof serverUrlAliases !== 'undefined' && serverUrlAliases) {
                        Object.keys(serverUrlAliases).forEach(function (k) { delete serverUrlAliases[k]; });
                    }
                    if (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) recordingsList.length = 0;
                    if (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) collectionsList.length = 0;
                    if (typeof flowsList !== 'undefined' && Array.isArray(flowsList)) flowsList.length = 0;
                    if (typeof environments !== 'undefined' && Array.isArray(environments)) environments.length = 0;
                    if (typeof services !== 'undefined' && Array.isArray(services)) services.length = 0;
                    if (typeof favorites !== 'undefined' && Array.isArray(favorites)) favorites.length = 0;
                } catch { /* defensive — some arrays may not be declared yet */ }
            } else {
                activeWorkspaceId = workspaces[0].id;
            }
        }
        persistWorkspaces();
        return true;
    }

    // ---- Workspace Export / Import (A1) ----
    //
    // A workspace is the project folder; export turns it into a
    // portable .bowire.json blob the operator can move between
    // machines or share with a teammate. Import has three modes:
    //   'new'         — create a fresh workspace and load everything
    //                   into its namespace
    //   'merge-into'  — concat arrays / Object.assign maps into the
    //                   target workspace's existing data
    //   'replace'     — wipe target workspace's data and load
    //                   payload's into it
    //
    // Secrets are intentionally NOT exported (session-only, would leak
    // credentials in a shared file). Schemas uploaded server-side
    // (/api/proto/upload) are also out of scope for v1 — they live on
    // the server, not in workspace localStorage.

    function _wsKeyFor(wsId, baseKey) {
        if (!wsId) return baseKey;
        return 'bowire_ws_' + wsId + '_'
            + String(baseKey).replace(/^bowire_/, '');
    }

    // The set of per-workspace keys we round-trip. Add new keys here
    // when new workspace-scoped state lands. Order doesn't matter.
    var _WORKSPACE_DATA_KEYS = [
        'bowire_server_urls',
        // Short-name (alias) per server URL — { [url]: alias }. Lives
        // alongside the URL list so the friendly names the user picked
        // round-trip with the workspace through export / import.
        'bowire_server_url_aliases',
        'bowire_url_meta',
        'bowire_collections',
        'bowire_collections_trash',
        'bowire_recordings',
        'bowire_recordings_trash',
        'bowire_favorites',
        'bowire_benchmarks',
        'bowire_flows',
        // #A — self-contained workspaces. Envs are workspace-owned, so
        // the export must carry them with the workspace; otherwise an
        // import would land an env-less workspace and the operator
        // would have to re-create every staging / prod env by hand.
        'bowire_environments',
        'bowire_active_env',
        // #193 Phase 2 — plugin pins for the .bww schema. Carries the
        // project's required-protocol set (Dictionary<protocolId,
        // semverString>) so a team member opening the workspace gets
        // the "install missing plugins" banner instead of cryptic
        // "no such protocol" errors at first request. Empty / absent
        // means no requirement, current behaviour preserved.
        'bowire_plugin_pins'
        // bowire_request_tabs intentionally NOT listed here — open
        // tabs are browser session state (which row I happened to be
        // looking at), not project content. Persisted under wsKey()
        // so the strip survives a reload but is NOT round-tripped
        // through workspace export / import. Cleaned up on workspace
        // delete via _WORKSPACE_BROWSER_STATE_KEYS below so the
        // localStorage entry doesn't leak.
    ];

    // Per-workspace keys that survive a reload but are NOT part of
    // the project content. Wiped on workspace delete so deleting a
    // workspace fully reclaims its localStorage footprint; skipped
    // by exportWorkspaceJson so a .bww file carries the project,
    // not the author's accidental UI state.
    var _WORKSPACE_BROWSER_STATE_KEYS = [
        'bowire_request_tabs'
    ];
    // Modes that store per-method presets via the presets framework.
    // Each maps to a `bowire_presets_<mode>` key under wsKey().
    var _WORKSPACE_PRESET_MODES = [
        'discover', 'flows', 'benchmarks', 'mocks', 'proxy', 'security'
    ];

    function exportWorkspaceJson(wsId) {
        var ws = workspaces.find(function (w) { return w.id === wsId; });
        if (!ws) return null;
        function readJson(key) {
            try {
                var raw = localStorage.getItem(_wsKeyFor(wsId, key));
                return raw ? JSON.parse(raw) : null;
            } catch { return null; }
        }
        var data = {};
        _WORKSPACE_DATA_KEYS.forEach(function (k) {
            var v = readJson(k);
            if (v !== null && v !== undefined) data[k] = v;
        });
        var presets = {};
        _WORKSPACE_PRESET_MODES.forEach(function (mode) {
            var p = readJson('bowire_presets_' + mode);
            if (Array.isArray(p) && p.length > 0) presets[mode] = p;
        });
        if (Object.keys(presets).length > 0) data.presets = presets;
        return {
            format: 'bowire-workspace',
            version: 1,
            exportedAt: new Date().toISOString(),
            workspace: {
                name: ws.name,
                color: ws.color,
                description: ws.description || '',
                // Inclusion fields retired with #A — envs are workspace-
                // owned now, the workspace export carries the env list
                // itself under data.bowire_environments.
                // #212 Phase 0 — workspace-level storage decision.
                // Exported so a recipient sees the same disk-vs-browser
                // posture the source workspace was running with.
                storage: ws.storage || 'disk',
                // Legacy field mirrored on export for pre-#212
                // importers; new importers prefer `storage` and fall
                // back to this if absent.
                recordingStorageMode: ws.recordingStorageMode || 'both'
            },
            data: data
        };
    }

    function downloadWorkspaceExport(wsId) {
        var payload = exportWorkspaceJson(wsId);
        if (!payload) return false;
        var ws = workspaces.find(function (w) { return w.id === wsId; });
        var safeName = (ws && ws.name ? ws.name : wsId)
            .replace(/[^a-zA-Z0-9_-]+/g, '-').replace(/^-+|-+$/g, '');
        var fileName = 'bowire-workspace-' + (safeName || wsId) + '.bowire.json';
        try {
            var blob = new Blob([JSON.stringify(payload, null, 2)],
                { type: 'application/json' });
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url; a.download = fileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);
            return true;
        } catch (e) {
            console.error('[workspace-export] failed', e);
            return false;
        }
    }

    function importWorkspaceJson(payload, opts) {
        opts = opts || {};
        if (!payload || typeof payload !== 'object'
            || payload.format !== 'bowire-workspace') {
            throw new Error('Not a Bowire workspace file (missing format marker)');
        }
        if (payload.version !== 1) {
            throw new Error('Unsupported workspace format version: ' + payload.version);
        }
        var mode = opts.mode || 'new';
        var wsMeta = payload.workspace || {};
        var data = payload.data || {};

        var targetId;
        if (mode === 'new') {
            var ws = createWorkspace(wsMeta.name || 'Imported',
                wsMeta.color || '#6366f1');
            ws.description = wsMeta.description || '';
            // #212 Phase 0 — prefer the workspace-level storage
            // field; fall back to the legacy recordingStorageMode
            // value if the export came from a pre-#212 workspace.
            if (wsMeta.storage === 'browser-only' || wsMeta.storage === 'disk') {
                ws.storage = wsMeta.storage;
            } else if (wsMeta.recordingStorageMode === 'browser-only') {
                ws.storage = 'browser-only';
            } else {
                ws.storage = 'disk';
            }
            ws.recordingStorageMode = wsMeta.recordingStorageMode || 'both';
            persistWorkspaces();
            targetId = ws.id;
        } else {
            if (!opts.target) throw new Error('merge/replace requires opts.target');
            if (!workspaces.find(function (w) { return w.id === opts.target; })) {
                throw new Error('target workspace not found: ' + opts.target);
            }
            targetId = opts.target;
        }

        var isReplace = (mode === 'replace' || mode === 'new');

        function writeKey(key, value) {
            var k = _wsKeyFor(targetId, key);
            if (isReplace) {
                localStorage.setItem(k, JSON.stringify(value));
                return;
            }
            // Merge: array → concat, object → assign keys (incoming wins),
            // scalar → overwrite.
            var existing = null;
            try {
                var raw = localStorage.getItem(k);
                if (raw) existing = JSON.parse(raw);
            } catch { /* ignore */ }
            if (Array.isArray(value)) {
                var merged = Array.isArray(existing)
                    ? existing.concat(value) : value.slice();
                localStorage.setItem(k, JSON.stringify(merged));
            } else if (value && typeof value === 'object') {
                var assigned = Object.assign({}, existing || {}, value);
                localStorage.setItem(k, JSON.stringify(assigned));
            } else {
                localStorage.setItem(k, JSON.stringify(value));
            }
        }

        _WORKSPACE_DATA_KEYS.forEach(function (k) {
            if (Object.prototype.hasOwnProperty.call(data, k)) writeKey(k, data[k]);
        });
        if (data.presets && typeof data.presets === 'object') {
            Object.keys(data.presets).forEach(function (m) {
                if (Array.isArray(data.presets[m])) {
                    writeKey('bowire_presets_' + m, data.presets[m]);
                }
            });
        }
        return targetId;
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

    // #127 follow-up — dirty-tracker for the manual "Save now" button.
    // Bowire's persist functions run immediately on each mutation, so
    // there are no "unsaved changes" in the v1.9 sense. But Cmd+S /
    // "Save now" exists as a "force-flush every slot at once" path,
    // and the button should grey out when nothing has been autosaved
    // since the last force-flush — otherwise the operator hits Save
    // and gets no visible consequence.
    //
    // hasUnflushedChanges flips true on every markSaved call EXCEPT
    // the ones called from inside flushAllPersists (gated by
    // _flushInProgress so a flush-internal markSaved doesn't
    // re-dirty the state it just cleaned).
    let _hasUnflushedChanges = false;
    let _flushInProgress = false;
    function hasUnflushedChanges() { return _hasUnflushedChanges; }

    // #127 — folder-open capability. Set once at boot from
    // /api/workspace/can-open-folder. Embedded hosts get { available:
    // false } back so the save-pill click is gated off (the host is
    // typically a production server, spawning a desktop process there
    // makes zero sense). Standalone tool: true.
    let canOpenWorkspaceFolder = false;

    // #154 Phase 1 — in-app help capability. Set once at boot from
    // /api/help/available. true = IBowireHelpProvider is registered
    // (the Kuestenlogik.Bowire.Help package was installed); false =
    // workbench renders Help affordances as disabled with an "install
    // the Help package" tooltip. Phase 3 ships the Help drawer + F1
    // binding that consume this state.
    let helpAvailable = false;

    // #154 Phase 3 — Help drawer state.
    //  helpDrawerOpen    — visibility, mirrored to localStorage
    //  helpTopics        — list of { id, title, categoryId }; lazy-
    //                      loaded on first open
    //  helpTopicsLoaded  — fetch dedup
    //  helpSelectedId    — currently-rendered topic id
    //  helpSelectedTopic — { id, title, markdown, categoryId } or null
    //  helpSearchQuery   — search input text
    //  helpSearchHits    — [{ id, title, excerpt, score }] when query non-empty
    let helpDrawerOpen = false;
    try { helpDrawerOpen = localStorage.getItem('bowire_help_drawer_open') === '1'; }
    catch { /* ignore */ }
    let helpTopics = [];
    let helpTopicsLoaded = false;
    let helpSelectedId = null;
    let helpSelectedTopic = null;
    let helpSearchQuery = '';
    let helpSearchHits = [];

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
        _flushInProgress = true;
        try {
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
        } finally {
            _flushInProgress = false;
        }
        // Reset dirty tracker — every persist slot just got written.
        _hasUnflushedChanges = false;
        markSaved(fail > 0 ? ('flush (' + ok + ' ok, ' + fail + ' err)') : 'all');
        // The markSaved above re-set _hasUnflushedChanges=true because
        // _flushInProgress is now false. Clear it again — the flush
        // itself isn't new dirty state.
        _hasUnflushedChanges = false;
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
        // Dirty-tracker — only count autosaves outside a force-flush
        // sweep; the markSaved a flush itself emits at the end is
        // bookkeeping, not new dirty state.
        if (!_flushInProgress) {
            _hasUnflushedChanges = true;
        }
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
    try { sidebarCollapsed = localStorage.getItem('bowire_sidebar_collapsed') === '1'; }
    catch { /* ignore */ }
    function setSidebarCollapsed(v) {
        sidebarCollapsed = !!v;
        try { localStorage.setItem('bowire_sidebar_collapsed', sidebarCollapsed ? '1' : '0'); }
        catch { /* ignore */ }
    }
    function toggleSidebarCollapsed() {
        // Modes without a sidebar (sidebar.kind === 'none') have
        // nothing to collapse — skip the toggle so Ctrl+B doesn't
        // appear to do nothing.
        if (typeof currentRailSidebarSpec === 'function') {
            var spec = currentRailSidebarSpec();
            if (spec && spec.kind === 'none') return;
        }
        setSidebarCollapsed(!sidebarCollapsed);
        render();
    }
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
            // Tabs are browser session state, not project content — the
            // localStorage write is just so the strip survives a reload.
            // No markSaved() because the "Saved tabs" toast that used
            // to fire on every method click implied the user had to
            // care about persisting which row they had open, which is
            // backwards. The catch arm still surfaces real failures
            // (quota exceeded, &c.) — that's a legit thing to know.
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
        var seenIds = Object.create(null);
        for (var i = 0; i < data.tabs.length; i++) {
            var t = data.tabs[i];
            var svc = services.find(function (s) { return s.name === t.serviceKey; });
            if (!svc) continue;
            var meth = (svc.methods || []).find(function (m) { return m.name === t.methodKey; });
            if (!meth) continue;
            // Drop tabs whose id collides with one already restored
            // — corrupt persisted state should not resurrect the
            // duplicate that originally produced the two-active-
            // tabs symptom.
            if (seenIds[t.id]) continue;
            seenIds[t.id] = true;
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
        // Make sure the in-process counter starts above every
        // restored id so the next '+' / Ctrl-click / context-menu
        // tab can't collide with a tab we just rehydrated.
        bumpTabIdCounterPast(requestTabs);
    }

    let _tabIdCounter = 0;
    function nextTabId() {
        // Walk the counter forward until it produces an id that
        // isn't already in use. Rehydrate restores tabs with their
        // original ids before bumping the counter (see
        // bumpTabIdCounterPast below); without this loop a counter
        // bump that missed an outlier would still let two tabs
        // share the same id, which is what produced the "two
        // active tabs" symptom — every tab whose id === activeTabId
        // got the .active CSS class, including the unintended
        // duplicate.
        for (;;) {
            var id = 'tab_' + (++_tabIdCounter);
            var clash = false;
            for (var i = 0; i < requestTabs.length; i++) {
                if (requestTabs[i].id === id) { clash = true; break; }
            }
            if (!clash) return id;
        }
    }

    function bumpTabIdCounterPast(tabs) {
        // Move _tabIdCounter past the highest tab_<n> id we just
        // restored from localStorage so the next nextTabId() can't
        // hand back one of the restored ids by accident. Ignores
        // ids that don't fit the tab_<n> pattern (defensive).
        if (!Array.isArray(tabs)) return;
        for (var i = 0; i < tabs.length; i++) {
            var id = tabs[i] && tabs[i].id;
            if (typeof id !== 'string') continue;
            var m = id.match(/^tab_(\d+)$/);
            if (!m) continue;
            var n = parseInt(m[1], 10);
            if (n > _tabIdCounter) _tabIdCounter = n;
        }
    }

    /**
     * Open a tab for the given service + method. If a tab already
     * exists for that pair, switch to it; otherwise create a new tab
     * and make it active.
     */
    function openTab(svc, method, opts) {
        opts = opts || {};
        // De-dupe by (service, method): ONLY when there is no active
        // tab to repurpose (landing page, every tab closed). In that
        // case we fall back to switching to an existing tab that
        // already holds this method. With an active tab present the
        // normal path always repurposes it, even if another tab
        // happens to hold the same method — otherwise a pinned tab
        // hijacks the focus the moment the user clicks something
        // matching in the tree, and the active tab they were just
        // editing snaps away. The `inNewTab` path skips the de-dupe
        // unconditionally (it's how the user explicitly pins a
        // second copy).
        if (!opts.inNewTab && activeTabId === null) {
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
    let envEditorTab = 'variables';    // 'variables' | 'secrets' | 'auth' | 'compare'
    // Active tab inside the workspace settings pane. Mirrors the env
    // editor's tab idiom so the two settings surfaces share one shape:
    // General (name + color + description + danger zone + …), Variables
    // (workspace-scope `{{vars}}`), Secrets (workspace-scope
    // `{{secret.X}}`). Module-scoped so a render() preserves the
    // active tab across re-renders.
    let workspaceSettingsTab = 'general'; // 'general' | 'variables' | 'secrets'

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
        var removed = collectionsList[idx];
        collectionsList.splice(idx, 1);
        if (collectionManagerSelectedId === id) collectionManagerSelectedId = null;
        persistCollections();
        // Trash hand-off — same shape as the inline-sidebar delete
        // path, so the central deleteCollection() doesn't hard-delete
        // and bypass the "Recently deleted" affordance. The 30-day TTL
        // sweep in prologue still applies; the user sees the entry in
        // the trash section at the bottom of the Collections sidebar.
        if (typeof collectionsTrash !== 'undefined' && removed) {
            collectionsTrash.unshift({
                entry: removed,
                deletedAt: Date.now(),
                originalIdx: idx
            });
            if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
        }
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
        // Fast-path: when the Console tab is mounted, try to append
        // the new entry in place instead of forcing a full render.
        // This keeps streaming responses from re-rendering the whole
        // workbench on every frame. Falls back to render() when the
        // in-place append misses (e.g. the drawer isn't mounted yet).
        if (consoleOpen && appendConsoleEntry(e)) {
            if (typeof renderConsolePanel === 'function') renderConsolePanel(true);
            return;
        }
        if (consoleOpen && typeof render === 'function') render();
    }

    function clearConsole() {
        consoleLog = [];
        if (typeof render === 'function') render();
    }

    function toggleConsole() {
        consoleOpen = !consoleOpen;
        if (typeof render === 'function') render();
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
                    if (result.title) {
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
    var _envSecrets = {};            // envId → { name → value } (session-only, per Environment)
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
    // Per-Environment secrets. Same shape as workspace secrets (session-
    // only Map, masked in UI, sanitised on export) but keyed by env id.
    // Resolver looks here first (active env), then falls through to the
    // workspace-level bag so a workspace secret stays the baseline and
    // the env can override or add its own.
    function getEnvSecrets(envId) {
        if (!envId) return {};
        return _envSecrets[envId] || (_envSecrets[envId] = {});
    }
    function setEnvSecret(envId, name, value) {
        if (!envId) return;
        var bag = getEnvSecrets(envId);
        if (value === null || value === undefined || value === '') delete bag[name];
        else bag[name] = String(value);
    }
    function listEnvSecrets(envId) {
        return Object.keys(getEnvSecrets(envId));
    }
    function resolveSecret(name) {
        var envId = (typeof getActiveEnvId === 'function') ? getActiveEnvId() : null;
        if (envId) {
            var envBag = getEnvSecrets(envId);
            if (Object.prototype.hasOwnProperty.call(envBag, name)) return envBag[name];
        }
        var wsBag = getWorkspaceSecrets();
        return Object.prototype.hasOwnProperty.call(wsBag, name) ? wsBag[name] : null;
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

