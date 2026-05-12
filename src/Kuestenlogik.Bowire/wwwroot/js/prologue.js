/**
 * Bowire — Interactive gRPC Browser
 * Pure vanilla JS, no framework dependencies.
 */
(function () {
    'use strict';

    const config = window.__BOWIRE_CONFIG__ || {
        title: 'Bowire',
        description: 'gRPC API Browser',
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
    var configHasUrls = (Array.isArray(config.serverUrls) && config.serverUrls.length > 0)
        || !!config.serverUrl;
    // embeddedMode (explicit flag from the host) wins over heuristics.
    // When set, the URL bar is always hidden regardless of serverUrls.
    var uiMode = config.embeddedMode
        ? 'embedded'
        : (config.lockServerUrl
            ? 'standalone-locked'
            : (configHasUrls ? 'standalone' : 'embedded'));

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
            var raw = localStorage.getItem(SERVER_URLS_KEY);
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
        try { localStorage.setItem(SERVER_URLS_KEY, JSON.stringify(serverUrls)); } catch {}
    }

    // Backwards-compat aliases for code paths that haven't been refactored yet
    function getPrimaryServerUrl() { return serverUrls.length > 0 ? serverUrls[0] : ''; }

    // ---- Protocol State ----
    let protocols = [];          // Available protocols [{id, name, icon}]
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
    let activeRequestTab = 'body';
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
        try { return JSON.parse(localStorage.getItem(METHOD_SCRIPTS_KEY) || '{}'); }
        catch { return {}; }
    }

    function saveAllMethodScripts(map) {
        try { localStorage.setItem(METHOD_SCRIPTS_KEY, JSON.stringify(map)); } catch {}
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
            var raw = localStorage.getItem(RECENT_METHODS_KEY);
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
        try { localStorage.setItem(RECENT_METHODS_KEY, JSON.stringify(list)); } catch {}
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

    let _tabIdCounter = 0;
    function nextTabId() {
        return 'tab_' + (++_tabIdCounter);
    }

    /**
     * Open a tab for the given service + method. If a tab already
     * exists for that pair, switch to it; otherwise create a new tab
     * and make it active.
     */
    function openTab(svc, method) {
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

        var tab = {
            id: nextTabId(),
            serviceKey: svc.name,
            methodKey: method.name,
            service: svc,
            method: method
        };
        requestTabs.push(tab);
        activeTabId = tab.id;

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
            if (v === 'favorites' || v === 'environments' || v === 'flows') return v;
            return 'services';
        } catch { return 'services'; }
    })();
    function setSidebarView(v) {
        sidebarView = (v === 'favorites' || v === 'environments' || v === 'flows') ? v : 'services';
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
        return !!(method && method.httpMethod && method.httpPath);
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
    let recordingManagerOpen = false; // modal visibility
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
    let collectionManagerOpen = false;
    let collectionManagerSelectedId = null;
    let collectionRunState = null; // { collectionId, stepIndex, status, results[] }

    function loadCollections() {
        try {
            var raw = localStorage.getItem(COLLECTIONS_KEY);
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch { return []; }
    }

    function persistCollections() {
        try {
            localStorage.setItem(COLLECTIONS_KEY, JSON.stringify(collectionsList));
        } catch {}
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
                    if (result.error) {
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

