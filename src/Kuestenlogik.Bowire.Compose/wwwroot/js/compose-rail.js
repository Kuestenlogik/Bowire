    // ---- #293 Compose Rail ----
    //
    // The Compose rail is the home for the ad-hoc request-builder. Before
    // #293 the builder piggy-backed on whatever tab was active in
    // Discover, which conflated two distinct workflows: Discover =
    // schema-driven (pick a server-advertised method), Compose = ad-hoc
    // (draft a request from scratch). The builder now has its own rail
    // with its own tab strip.
    //
    // State model:
    //   composeTabs = [{ id, request }]
    //     where `request` is a fully-formed freeformRequest object
    //     (carrying the _requestBuilder marker). On tab switch we
    //     swap the GLOBAL freeformRequest slot so every existing
    //     code path (render, execute, autocomplete, tests, …) keeps
    //     working unchanged.
    //   activeDesignTabId : id | null
    //   _designTabIdCounter : monotonic counter
    //
    // Persistence: bowire_compose_tabs under wsKey(), same convention
    // as bowire_request_tabs. Tabs survive a reload but are NOT
    // round-tripped through .bww export — see _WORKSPACE_BROWSER_STATE_KEYS
    // in prologue.js.

    let composeTabs = [];
    let activeDesignTabId = null;
    let _designTabIdCounter = 0;
    let _composeTabsRehydrated = false;

    // Library state. The Compose rail's Library (Collections + Presets)
    // used to live in a custom panel mounted in the main pane; it now
    // lives in the standard workbench sidebar slot (SidebarKind =>
    // "library" on BowireComposeRailContribution + the case 'library'
    // arm in render-sidebar.js's renderSidebar() switch). The collapse
    // affordance is the standard sidebar-splitter edge-toggle now —
    // see prologue.js's setSidebarCollapsed / toggleSidebarCollapsed.
    //   composeSidePanelCollapsed — legacy key kept for backwards-
    //     compatible localStorage round-tripping. The active collapse
    //     state lives in bowire_sidebar_collapsed (per workspace via
    //     the standard sidebar splitter). focusComposeOnCollection
    //     still flips this so any old persisted blob stays consistent.
    //   composeCollectionsExpanded[id] — per-collection expand state.
    //   composePresetsExpanded[key] — per-preset-mode expand state.
    let composeSidePanelCollapsed = false;
    let composeCollectionsExpanded = Object.create(null);
    let composePresetsExpanded = Object.create(null);
    let _composeSidePanelRehydrated = false;

    function _composeSidePanelKey() { return 'bowire_compose_panel_state'; }

    function persistComposeSidePanel() {
        try {
            var data = {
                collapsed: !!composeSidePanelCollapsed,
                collections: composeCollectionsExpanded,
                presets: composePresetsExpanded
            };
            localStorage.setItem(wsKey(_composeSidePanelKey()), JSON.stringify(data));
        } catch (_) { /* non-fatal */ }
    }

    function rehydrateComposeSidePanel() {
        if (_composeSidePanelRehydrated) return;
        _composeSidePanelRehydrated = true;
        try {
            var raw = localStorage.getItem(wsKey(_composeSidePanelKey()));
            if (!raw) return;
            var data = JSON.parse(raw);
            if (!data) return;
            composeSidePanelCollapsed = !!data.collapsed;
            if (data.collections && typeof data.collections === 'object') {
                composeCollectionsExpanded = data.collections;
            }
            if (data.presets && typeof data.presets === 'object') {
                composePresetsExpanded = data.presets;
            }
        } catch (_) { /* corrupt blob — keep defaults */ }
    }

    function _nextDesignTabId() {
        for (;;) {
            var id = 'design_' + (++_designTabIdCounter);
            var clash = false;
            for (var i = 0; i < composeTabs.length; i++) {
                if (composeTabs[i].id === id) { clash = true; break; }
            }
            if (!clash) return id;
        }
    }

    // Public entry-point used by the discover-pill / workspace-tree
    // routes that previously jumped into the standalone Collections
    // rail. With the Library now in the standard sidebar slot we also
    // un-collapse the sidebar itself via setSidebarCollapsed so the
    // user actually sees the expanded collection — the operator might
    // have stowed the sidebar via the splitter's edge-toggle.
    function focusComposeOnCollection(collectionId) {
        rehydrateComposeSidePanel();
        composeSidePanelCollapsed = false;
        if (typeof setSidebarCollapsed === 'function') {
            setSidebarCollapsed(false);
        }
        if (collectionId) {
            composeCollectionsExpanded[collectionId] = true;
        }
        persistComposeSidePanel();
    }
    if (typeof window !== 'undefined') {
        window.focusComposeOnCollection = focusComposeOnCollection;
    }

    // Persist open Compose tabs + active id. Strips the live File ref
    // off binary bodies (browser File objects don't survive
    // JSON.stringify) — the on-disk filename stays in binaryName so
    // the operator at least sees what they had picked.
    function persistDesignTabs() {
        try {
            var data = {
                tabs: composeTabs.map(function (t) {
                    var req = t.request || {};
                    var rb = req._requestBuilder || {};
                    // Shallow clone so the original keeps its live File ref.
                    var rbClone = Object.assign({}, rb);
                    rbClone._binaryRef = null;
                    var reqClone = Object.assign({}, req, { _requestBuilder: rbClone });
                    // #295 Phase D — origin persists with the tab so the
                    // badge survives a reload. Defaults to 'fresh' for
                    // legacy persisted tabs that pre-date the field.
                    return { id: t.id, request: reqClone, origin: t.origin || { kind: 'fresh' } };
                }),
                active: activeDesignTabId,
            };
            localStorage.setItem(wsKey('bowire_compose_tabs'), JSON.stringify(data));
        } catch (e) {
            if (typeof markSaveFailed === 'function') markSaveFailed('compose tabs', e);
        }
    }

    function _restoreDesignTabsFromStorage() {
        try {
            var raw = localStorage.getItem(wsKey('bowire_compose_tabs'));
            if (!raw) return null;
            var data = JSON.parse(raw);
            if (!data || !Array.isArray(data.tabs)) return null;
            return data;
        } catch { return null; }
    }

    function rehydrateDesignTabs() {
        if (_composeTabsRehydrated) return;
        _composeTabsRehydrated = true;
        if (composeTabs.length > 0) return;
        var data = _restoreDesignTabsFromStorage();
        if (!data || !Array.isArray(data.tabs) || data.tabs.length === 0) return;
        var seenIds = Object.create(null);
        for (var i = 0; i < data.tabs.length; i++) {
            var t = data.tabs[i];
            if (!t || !t.request) continue;
            if (seenIds[t.id]) continue;
            seenIds[t.id] = true;
            // Re-stamp the _requestBuilder shape — the persisted version may
            // be a few migrations behind; ensureHoppState normalises it.
            try {
                if (typeof ensureHoppState === 'function') ensureHoppState(t.request);
            } catch (_) { /* leave shape as-is */ }
            composeTabs.push({
                id: t.id,
                request: t.request,
                origin: t.origin || { kind: 'fresh' }
            });
            var n = (t.id.match(/^design_(\d+)$/) || [])[1];
            if (n) {
                var num = parseInt(n, 10);
                if (num > _designTabIdCounter) _designTabIdCounter = num;
            }
        }
        if (composeTabs.length > 0) {
            var keep = composeTabs.find(function (x) { return x.id === data.active; }) || composeTabs[0];
            activeDesignTabId = keep.id;
        }
    }

    // Spawn a fresh Compose tab + activate it. opts mirrors
    // startHoppRequest's opts: { protocol, url, method }.
    // Returns the new tab id.
    // Duplicate a Compose tab — deep-clones the request state of the
    // source tab into a new tab inserted immediately after it, then
    // switches to the clone. Used by the tab context menu's
    // 'Duplicate tab' action. Operator feedback: 'auf dem tab context
    // (rechtsklick) im compose rail … den tab mit seinem aktuellen
    // inhalt/zustand kopieren.'
    function _duplicateDesignTab(srcId) {
        if (!srcId) return null;
        // Snapshot the currently-active tab first so any unsaved edits
        // in freeformRequest land back on the source's tab.request
        // before we clone.
        _snapshotActiveDesignTab();
        var srcIdx = composeTabs.findIndex(function (t) { return t.id === srcId; });
        if (srcIdx < 0) return null;
        var src = composeTabs[srcIdx];
        var clonedRequest;
        try { clonedRequest = JSON.parse(JSON.stringify(src.request)); }
        catch { return null; /* circular structure — shouldn't happen for plain request state */ }
        var clonedOrigin;
        try { clonedOrigin = JSON.parse(JSON.stringify(src.origin || { kind: 'fresh' })); }
        catch { clonedOrigin = { kind: 'fresh' }; }
        var newTab = {
            id: _nextDesignTabId(),
            request: clonedRequest,
            origin: clonedOrigin
        };
        composeTabs.splice(srcIdx + 1, 0, newTab);
        activeDesignTabId = newTab.id;
        freeformRequest = clonedRequest;
        persistDesignTabs();
        if (typeof render === 'function') render();
        return newTab.id;
    }

    function spawnDesignTab(opts) {
        opts = opts || {};
        // Park the currently-active tab's request before swapping.
        _snapshotActiveDesignTab();
        var pid = opts.protocol || 'rest';
        // Build a fresh request-builder request without touching
        // railMode / sidebarView — startFreeformRequest used to flip
        // those to 'discover' as a side effect, which now leaks the
        // operator off the Compose rail every time we spawn.
        var req = {
            protocol: pid,
            serverUrl: opts.url || '',
            urlMode: 'inline',
            service: '',
            method: opts.method || 'GET',
            body: '{}',
            metadata: {},
            methodType: 'Unary',
            mockResponse: '',
            mockStatus: 'OK',
            _requestBuilder: (typeof _newHoppState === 'function')
                ? _newHoppState() : { protocol: pid, byProtocol: {} }
        };
        req._requestBuilder.protocol = pid;
        // Pick the first sub-tab the layout exposes so the operator
        // doesn't land on a stale 'parameter' for a protocol that
        // doesn't have one.
        try {
            var layout = (typeof rbLayouts !== 'undefined') ? rbLayouts[pid] : null;
            if (layout && typeof layout.subTabs === 'function') {
                var tabs = layout.subTabs(req);
                if (tabs.length > 0) req._requestBuilder.activeTab = tabs[0].id;
            }
        } catch (_) { /* layout helpers may not be ready — non-fatal */ }
        var tab = { id: _nextDesignTabId(), request: req, origin: opts.origin || { kind: 'fresh' } };
        composeTabs.push(tab);
        activeDesignTabId = tab.id;
        // Swap the global freeformRequest so the rest of the workbench
        // (execute, autocomplete, tests, &c) addresses this tab.
        freeformRequest = req;
        persistDesignTabs();
        return tab.id;
    }

    // Snapshot the currently-bound freeformRequest back into the
    // active Compose tab so the in-flight edits survive a tab switch.
    function _snapshotActiveDesignTab() {
        if (!activeDesignTabId) return;
        var tab = composeTabs.find(function (t) { return t.id === activeDesignTabId; });
        if (!tab) return;
        if (freeformRequest) tab.request = freeformRequest;
    }

    function switchDesignTab(id) {
        if (id === activeDesignTabId) return;
        _snapshotActiveDesignTab();
        var tab = composeTabs.find(function (t) { return t.id === id; });
        if (!tab) return;
        activeDesignTabId = tab.id;
        freeformRequest = tab.request;
        persistDesignTabs();
        render();
    }

    function closeDesignTab(id) {
        var idx = composeTabs.findIndex(function (t) { return t.id === id; });
        if (idx < 0) return;
        var wasActive = (id === activeDesignTabId);
        composeTabs.splice(idx, 1);
        if (wasActive) {
            // Pick a sane neighbour: prefer right, then left, then none.
            var next = composeTabs[idx] || composeTabs[idx - 1] || null;
            if (next) {
                activeDesignTabId = next.id;
                freeformRequest = next.request;
            } else {
                activeDesignTabId = null;
                freeformRequest = null;
            }
        }
        persistDesignTabs();
        render();
    }

    // Derive a live tab title from the request's protocol + method + URL.
    //   REST GET https://api.example.com/users/42  →  'GET /users/42'
    //   gRPC UserService/GetUser                   →  'gRPC GetUser'
    //   MQTT publish topic/foo                     →  'MQTT publish'
    //   Empty URL                                  →  'Untitled'
    function designTabTitle(tab) {
        if (!tab || !tab.request) return 'Untitled';
        var req = tab.request;
        var pid = (req._requestBuilder && req._requestBuilder.protocol) || req.protocol || 'rest';
        var url = (req.serverUrl || '').trim();
        var method = (req.method || '').trim();
        if (pid === 'rest') {
            if (!url) return method ? method.toUpperCase() : 'Untitled';
            // Render just the path so the strip stays scannable.
            var path = '';
            try {
                // URL constructor needs an absolute URL; fall back to
                // raw string for relative inputs.
                if (/^https?:\/\//i.test(url)) {
                    var u = new URL(url);
                    path = u.pathname + (u.search || '');
                } else {
                    path = url;
                }
            } catch (_) { path = url; }
            if (!path) path = '/';
            return (method || 'GET').toUpperCase() + ' ' + path;
        }
        if (pid === 'grpc' || pid === 'proto') {
            // Method field carries Service/Method on gRPC bars.
            var svc = (req.service || '').trim();
            var grpcMethod = method.split('/').pop() || method;
            if (!url && !method) return 'gRPC';
            return 'gRPC ' + (grpcMethod || svc || 'Untitled');
        }
        if (pid === 'mqtt' || pid === 'amqp' || pid === 'nats' || pid === 'kafka') {
            // The publish/subscribe verb lives on the protocol's scratch
            // state — try to surface it, fall back to the protocol label.
            var verb = '';
            try {
                var scratch = req._requestBuilder
                    && req._requestBuilder.byProtocol
                    && req._requestBuilder.byProtocol[pid];
                if (scratch && typeof scratch.mode === 'string') verb = scratch.mode;
            } catch (_) { /* ignore */ }
            return pid.toUpperCase() + (verb ? ' ' + verb : (url ? ' ' + url : ''));
        }
        if (pid === 'graphql') {
            if (!url) return 'GraphQL';
            return 'GraphQL ' + url;
        }
        if (pid === 'websocket' || pid === 'sse' || pid === 'signalr') {
            return pid.toUpperCase() + (url ? ' ' + url : '');
        }
        // Default fallback for anything else (mcp, odata, &c).
        return pid.toUpperCase() + (url ? ' ' + url : (method ? ' ' + method : ''));
    }

    // ---- #295 Phase B/D — spawn a Compose tab from a saved item ----
    //
    // Source for the request shape is a collection item OR a preset
    // config blob (both share the same _snapshot shape that the
    // freeform/discover "Add to…" menu produces — see _snapshotFreeform
    // in render-main.js + saveCurrentRequestToCollection in prologue.js).
    //
    // Builds a freshly-shaped request-builder request seeded from the
    // saved fields + stamps an `origin` marker so the tab badge can
    // surface where this tab came from.
    //
    //   origin shape:
    //     { kind: 'fresh' }                                    — default
    //     { kind: 'collection', collectionId, itemId, name }   — from collection
    //     { kind: 'preset', mode, presetId, name }             — from preset
    //     { kind: 'discover', service, method }                — from Discover
    //     { kind: 'recording', recordingId, stepIndex, name }  — from a recording
    function spawnDesignTabFromItem(item, origin) {
        if (!item) return null;
        _snapshotActiveDesignTab();
        var pid = item.protocol || 'rest';
        var req = {
            protocol: pid,
            serverUrl: item.serverUrl || '',
            urlMode: item.urlMode || 'inline',
            service: item.service || '',
            method: item.method || (pid === 'rest' ? 'GET' : ''),
            body: item.body || (Array.isArray(item.messages) ? item.messages[0] : '') || '{}',
            messages: Array.isArray(item.messages) ? item.messages.slice() : [item.body || '{}'],
            metadata: item.metadata ? Object.assign({}, item.metadata) : {},
            methodType: item.methodType || 'Unary',
            mockResponse: '',
            mockStatus: 'OK',
            _requestBuilder: (typeof _newHoppState === 'function')
                ? _newHoppState() : { protocol: pid, byProtocol: {} }
        };
        req._requestBuilder.protocol = pid;
        // Carry KV-shaped state when the item carries it (request-builder-shape
        // collections — `kind: 'request-builder'` snapshots from
        // _snapshotHoppForCollection). Best-effort — older 'ad-hoc'
        // shapes won't have these and just leave the bag empty.
        try {
            if (item.params && typeof item.params === 'object') {
                req._requestBuilder.params = Object.keys(item.params).map(function (k) {
                    return { key: k, value: item.params[k], description: '', enabled: true };
                });
            }
            if (item.metadata && typeof item.metadata === 'object') {
                req._requestBuilder.headers = Object.keys(item.metadata).map(function (k) {
                    return { key: k, value: item.metadata[k], description: '', enabled: true };
                });
            }
            if (item.authKind) req._requestBuilder.authKind = item.authKind;
            if (item.authData) req._requestBuilder.authData = Object.assign({}, item.authData);
            if (typeof item.preScript === 'string') req._requestBuilder.preScript = item.preScript;
            if (typeof item.postScript === 'string') req._requestBuilder.postScript = item.postScript;
        } catch (_) { /* shape-tolerant — leave defaults */ }
        try {
            var layout = (typeof rbLayouts !== 'undefined') ? rbLayouts[pid] : null;
            if (layout && typeof layout.subTabs === 'function') {
                var tabs = layout.subTabs(req);
                if (tabs.length > 0) req._requestBuilder.activeTab = tabs[0].id;
            }
        } catch (_) { /* layout not ready */ }
        var tab = { id: _nextDesignTabId(), request: req, origin: origin || { kind: 'fresh' } };
        composeTabs.push(tab);
        activeDesignTabId = tab.id;
        freeformRequest = req;
        persistDesignTabs();
        return tab.id;
    }

    // Replace the currently-active tab's request with a saved-item
    // snapshot. Used by drop-on-existing-tab semantics. Returns true
    // if it could happen.
    function replaceActiveDesignTabFromItem(item, origin) {
        if (!activeDesignTabId || !item) return false;
        var tab = composeTabs.find(function (t) { return t.id === activeDesignTabId; });
        if (!tab) return false;
        // Use spawn to build the canonical shape, then steal its
        // request + origin and drop the freshly created tab.
        var newId = spawnDesignTabFromItem(item, origin);
        if (!newId) return false;
        var newTab = composeTabs.find(function (t) { return t.id === newId; });
        if (!newTab) return false;
        tab.request = newTab.request;
        tab.origin = newTab.origin;
        // Drop the duplicate.
        var idx = composeTabs.findIndex(function (t) { return t.id === newId; });
        if (idx >= 0) composeTabs.splice(idx, 1);
        activeDesignTabId = tab.id;
        freeformRequest = tab.request;
        persistDesignTabs();
        return true;
    }

    // Subtle 1-character badge surfacing the tab's origin. Sits next
    // to the title. Returns null when no origin or origin is 'fresh'
    // so the strip stays scannable for the default case.
    function _designTabOriginBadge(origin) {
        if (!origin || !origin.kind || origin.kind === 'fresh') return null;
        var iconName, title;
        switch (origin.kind) {
            case 'collection':
                iconName = 'folder';
                title = 'From collection' + (origin.name ? ' — ' + origin.name : '');
                break;
            case 'preset':
                iconName = 'star';
                title = 'From preset' + (origin.name ? ' — ' + origin.name : '');
                break;
            case 'discover':
                iconName = 'search';
                title = 'From Discover' + (origin.method ? ' — ' + (origin.service || '') + '/' + origin.method : '');
                break;
            case 'recording':
                iconName = 'replay';
                title = 'From recording' + (origin.name ? ' — ' + origin.name : '')
                    + (typeof origin.stepIndex === 'number' ? ' (step ' + (origin.stepIndex + 1) + ')' : '');
                break;
            default:
                return null;
        }
        return el('span', {
            className: 'bowire-compose-tab-origin-badge bowire-compose-tab-origin-' + origin.kind,
            title: title,
            'aria-label': title,
            innerHTML: (typeof svgIcon === 'function') ? svgIcon(iconName) : ''
        });
    }

    // ---- Compose Library sidebar ----
    //
    // The Library (Collections + Presets) lives in the standard
    // workbench sidebar slot — same chrome (toolbar header, sidebar
    // splitter, edge-toggle, hover-intent, grip dots) as Discover /
    // Recordings / Workspaces / Mocks / &c. Dispatched by the
    // `case 'library':` arm in render-sidebar.js's renderSidebar()
    // switch, which calls window.renderComposeLibrarySidebar() when
    // the Compose package's compose-rail.js fragment is loaded.
    //
    // Layout (top → bottom):
    //   1. Standard sidebar toolbar — "Library" title + accent
    //      "+ New collection" primary button.
    //   2. Collections list — collection rows + per-collection items
    //      tree (existing row markup re-used).
    //   3. Presets section — per-mode preset groups.
    //
    // The render() call from any per-row interaction (expand, focus a
    // collection, drop into a tab) re-paints through the standard
    // sidebar pass; collapsing the sidebar via Ctrl+B / the splitter
    // edge-toggle / drag-to-min behaves exactly like every other rail.
    function renderComposeLibrarySidebar() {
        rehydrateComposeSidePanel();
        var sidebar = el('div', {
            id: 'bowire-sidebar',
            className: 'bowire-sidebar bowire-sidebar-mode bowire-sidebar-compose-library'
        });

        // Toolbar header — title + primary "+ new collection" button.
        // Same renderSidebarToolbar() helper every other rail uses, so
        // the title typography, primary-button colour, and overflow
        // affordance stay consistent with Discover / Recordings / &c.
        sidebar.appendChild(renderSidebarToolbar({
            title: 'Library',
            primary: {
                icon: 'plus',
                title: 'New collection',
                onClick: function () {
                    if (typeof bowirePrompt !== 'function') {
                        if (typeof createCollection === 'function') {
                            createCollection();
                            render();
                        }
                        return;
                    }
                    bowirePrompt('Collection name', {
                        title: 'New collection',
                        placeholder: 'e.g. Smoke tests',
                        confirmText: 'Create'
                    }).then(function (name) {
                        if (name === null) return;
                        var trimmed = String(name || '').trim();
                        if (typeof createCollection === 'function') {
                            createCollection(trimmed || undefined);
                            render();
                        }
                    });
                }
            }
        }));

        // Collections list (no per-section header — the toolbar above
        // already says "Library" and the only top-level grouping in
        // here is Collections vs Presets, which the Presets section
        // header below makes explicit).
        sidebar.appendChild(_renderComposeCollectionsSection());

        // Presets section — sub-divider header so the operator sees
        // where the per-mode preset list starts.
        sidebar.appendChild(_renderComposePresetsSection());

        return sidebar;
    }
    if (typeof window !== 'undefined') {
        window.renderComposeLibrarySidebar = renderComposeLibrarySidebar;
    }

    function _renderComposeCollectionsSection() {
        var section = el('div', { className: 'bowire-compose-side-section bowire-compose-side-section-collections' });
        var cols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
            ? collectionsList : [];

        if (cols.length === 0) {
            // Use the canonical .bowire-pane-empty class so the
            // typography (13 px body / secondary text colour /
            // 12 × 14 padding) matches every other rail's sidebar
            // empty-state (Recordings, Mocks, Flows, Workspaces,
            // Discover services view). The old .bowire-compose-side-empty
            // shipped at 11 px / tertiary which read as a third tier
            // unique to this rail.
            section.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No collections yet. Save a request from any live tab via the "Save to collection" action.'
            }));
            return section;
        }

        var list = el('div', { className: 'bowire-compose-side-list' });
        cols.forEach(function (col) {
            list.appendChild(_renderComposeCollectionRow(col));
        });
        section.appendChild(list);
        return section;
    }

    function _renderComposeCollectionRow(col) {
        var open = !!composeCollectionsExpanded[col.id];
        var row = el('div', { className: 'bowire-compose-side-collection' + (open ? ' is-open' : '') });
        var head = el('div', {
            className: 'bowire-compose-side-collection-head',
            onClick: function () {
                composeCollectionsExpanded[col.id] = !composeCollectionsExpanded[col.id];
                persistComposeSidePanel();
                render();
            },
            // R3a — Compose Library row → Flows transition. Right-click
            // the collection head to surface "Build flow from collection",
            // mirroring the Recordings-detail "Convert to Flow" affordance
            // so the operator's cross-rail jumps are uniform regardless of
            // which side of the workspace they're starting from. Lands
            // them on the Flows rail with the new flow open + selected.
            onContextMenu: function (e) {
                if (typeof showContextMenu !== 'function') return;
                e.preventDefault();
                e.stopPropagation();
                var itemCount = (col.items || []).length;
                showContextMenu(e.clientX, e.clientY, [
                    {
                        label: 'Build flow from collection',
                        icon: 'flow',
                        disabled: itemCount === 0,
                        title: itemCount === 0
                            ? 'This collection has no saved requests yet'
                            : 'Project each saved request as a Request node on a fresh flow, then jump to the Flows rail',
                        onClick: function () {
                            if (typeof convertCollectionToFlow !== 'function') return;
                            var flowId = convertCollectionToFlow(col.id);
                            if (!flowId) return;
                            railMode = 'flows';
                            try { localStorage.setItem('bowire_rail_mode', 'flows'); } catch (_) { /* ignore */ }
                            if (typeof setSidebarView === 'function') setSidebarView('flows');
                            if (typeof flowEditorSelectedId !== 'undefined') flowEditorSelectedId = flowId;
                            render();
                            if (typeof toast === 'function') {
                                toast('Flow created from "' + (col.name || 'collection') + '" — open on Flows rail', 'success');
                            }
                        }
                    }
                ]);
            }
        });
        head.appendChild(el('span', {
            className: 'bowire-compose-side-collection-caret',
            innerHTML: svgIcon('chevron')
        }));
        head.appendChild(el('span', {
            className: 'bowire-compose-side-collection-name',
            textContent: col.name || 'Unnamed'
        }));
        head.appendChild(el('span', {
            className: 'bowire-compose-side-collection-count',
            textContent: String((col.items || []).length)
        }));
        row.appendChild(head);

        if (open && col.items && col.items.length > 0) {
            var itemList = el('div', { className: 'bowire-compose-side-collection-items' });
            col.items.forEach(function (item) {
                itemList.appendChild(_renderComposeCollectionItemRow(col, item));
            });
            row.appendChild(itemList);
        }
        return row;
    }

    function _renderComposeCollectionItemRow(col, item) {
        var protoLabel = (item.protocol || 'rest').toUpperCase();
        var methodLabel = item.method || '';
        // Compact title: METHOD path-or-service-method.
        var title;
        if ((item.protocol || 'rest') === 'rest') {
            var path = '';
            try {
                if (item.serverUrl && /^https?:\/\//i.test(item.serverUrl)) {
                    var u = new URL(item.serverUrl);
                    path = u.pathname + (u.search || '');
                } else {
                    path = item.serverUrl || '';
                }
            } catch (_) { path = item.serverUrl || ''; }
            title = (methodLabel || 'GET').toUpperCase() + ' ' + (path || '/');
        } else {
            title = protoLabel + ' ' + (item.service ? item.service + '/' : '') + methodLabel;
        }
        var row = el('div', {
            className: 'bowire-compose-side-item',
            title: title + ' — click to open in a new tab',
            draggable: 'true',
            onClick: function () {
                spawnDesignTabFromItem(item, {
                    kind: 'collection',
                    collectionId: col.id,
                    itemId: item.id,
                    name: col.name
                });
                render();
                requestAnimationFrame(function () {
                    var inp = document.getElementById('bowire-request-builder-url-input');
                    if (inp) inp.focus();
                });
            },
            onDragStart: function (e) {
                try {
                    var payload = {
                        kind: 'collection-item',
                        collectionId: col.id,
                        itemId: item.id
                    };
                    e.dataTransfer.setData('application/x-bowire-compose', JSON.stringify(payload));
                    e.dataTransfer.effectAllowed = 'copy';
                } catch (_) { /* setData may throw in obscure browsers */ }
            }
        });
        row.appendChild(el('span', {
            className: 'bowire-compose-side-item-proto',
            'data-protocol': item.protocol || 'rest',
            textContent: protoLabel
        }));
        row.appendChild(el('span', {
            className: 'bowire-compose-side-item-title',
            textContent: title
        }));
        return row;
    }

    function _renderComposePresetsSection() {
        var section = el('div', { className: 'bowire-compose-side-section bowire-compose-side-section-presets' });
        var head = el('div', { className: 'bowire-compose-side-section-header' });
        head.appendChild(el('span', { className: 'bowire-compose-side-section-icon', innerHTML: svgIcon('pin') }));
        head.appendChild(el('span', { className: 'bowire-compose-side-section-label', textContent: 'Presets' }));
        section.appendChild(head);

        // Presets are per-mode. The Compose rail surfaces every mode
        // that has at least one preset so the operator can drag
        // saved-config blobs into a fresh Compose tab. Mode list is
        // discovered lazily from loadPresets() — the framework caches
        // per mode so this stays cheap.
        var modes = ['discover', 'benchmark', 'mock', 'flow', 'security', 'proxy'];
        var anyShown = false;
        modes.forEach(function (mode) {
            if (typeof loadPresets !== 'function') return;
            var list;
            try { list = loadPresets(mode) || []; } catch (_) { list = []; }
            if (list.length === 0) return;
            anyShown = true;
            section.appendChild(_renderComposePresetGroup(mode, list));
        });
        if (!anyShown) {
            section.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No presets yet. Save a configuration from any mode (Discover, Mocks, Benchmarks…) to see it here.'
            }));
        }
        return section;
    }

    function _renderComposePresetGroup(mode, list) {
        var key = 'preset_' + mode;
        var open = !!composePresetsExpanded[key];
        var group = el('div', { className: 'bowire-compose-side-preset-group' + (open ? ' is-open' : '') });
        var head = el('div', {
            className: 'bowire-compose-side-collection-head',
            onClick: function () {
                composePresetsExpanded[key] = !composePresetsExpanded[key];
                persistComposeSidePanel();
                render();
            }
        });
        head.appendChild(el('span', {
            className: 'bowire-compose-side-collection-caret',
            innerHTML: svgIcon('chevron')
        }));
        head.appendChild(el('span', {
            className: 'bowire-compose-side-collection-name',
            textContent: mode.charAt(0).toUpperCase() + mode.slice(1)
        }));
        head.appendChild(el('span', {
            className: 'bowire-compose-side-collection-count',
            textContent: String(list.length)
        }));
        group.appendChild(head);
        if (!open) return group;

        var items = el('div', { className: 'bowire-compose-side-collection-items' });
        list.forEach(function (preset) {
            items.appendChild(_renderComposePresetRow(mode, preset));
        });
        group.appendChild(items);
        return group;
    }

    function _renderComposePresetRow(mode, preset) {
        var cfg = preset.config || {};
        var row = el('div', {
            className: 'bowire-compose-side-item',
            title: preset.name + ' — click to open in a new tab',
            draggable: 'true',
            onClick: function () {
                spawnDesignTabFromItem(cfg, {
                    kind: 'preset',
                    mode: mode,
                    presetId: preset.id,
                    name: preset.name
                });
                render();
            },
            onDragStart: function (e) {
                try {
                    var payload = {
                        kind: 'preset',
                        mode: mode,
                        presetId: preset.id
                    };
                    e.dataTransfer.setData('application/x-bowire-compose', JSON.stringify(payload));
                    e.dataTransfer.effectAllowed = 'copy';
                } catch (_) { /* ignore */ }
            }
        });
        row.appendChild(el('span', {
            className: 'bowire-compose-side-item-proto',
            'data-protocol': cfg.protocol || 'rest',
            textContent: (cfg.protocol || 'rest').toUpperCase()
        }));
        row.appendChild(el('span', {
            className: 'bowire-compose-side-item-title',
            textContent: preset.name || 'Unnamed preset'
        }));
        return row;
    }

    // ---- #295 Phase B — drop-target plumbing on the tab strip ----
    //
    // Accepts the application/x-bowire-compose payload emitted by
    // collection items + preset rows. Drop on a blank strip area =
    // new tab. Drop on an existing tab = replace its content (with
    // confirm if the operator has typed-but-unsaved work).
    function _resolveComposeDragPayload(dt) {
        try {
            var raw = dt.getData('application/x-bowire-compose');
            if (!raw) return null;
            var payload = JSON.parse(raw);
            return payload || null;
        } catch (_) { return null; }
    }
    function _findCollectionItem(collectionId, itemId) {
        if (typeof collectionsList === 'undefined' || !Array.isArray(collectionsList)) return null;
        var col = collectionsList.find(function (c) { return c.id === collectionId; });
        if (!col || !Array.isArray(col.items)) return null;
        var item = col.items.find(function (i) { return i.id === itemId; });
        return { col: col, item: item };
    }
    function _findPreset(mode, presetId) {
        if (typeof loadPresets !== 'function') return null;
        try {
            var list = loadPresets(mode) || [];
            var preset = list.find(function (p) { return p.id === presetId; });
            if (!preset) return null;
            return { mode: mode, preset: preset };
        } catch (_) { return null; }
    }

    function _handleComposeStripDrop(e) {
        var payload = _resolveComposeDragPayload(e.dataTransfer);
        if (!payload) return false;
        e.preventDefault();
        if (payload.kind === 'collection-item') {
            var found = _findCollectionItem(payload.collectionId, payload.itemId);
            if (!found || !found.item) return false;
            spawnDesignTabFromItem(found.item, {
                kind: 'collection',
                collectionId: found.col.id,
                itemId: found.item.id,
                name: found.col.name
            });
            render();
            return true;
        }
        if (payload.kind === 'preset') {
            var fp = _findPreset(payload.mode, payload.presetId);
            if (!fp) return false;
            spawnDesignTabFromItem(fp.preset.config || {}, {
                kind: 'preset',
                mode: fp.mode,
                presetId: fp.preset.id,
                name: fp.preset.name
            });
            render();
            return true;
        }
        return false;
    }

    function _handleComposeTabDrop(e, targetTabId) {
        var payload = _resolveComposeDragPayload(e.dataTransfer);
        if (!payload) return false;
        e.preventDefault();
        // Switch to the target tab so the operator sees what's about to
        // change, then replace.
        if (targetTabId && targetTabId !== activeDesignTabId) {
            switchDesignTab(targetTabId);
        }
        if (payload.kind === 'collection-item') {
            var found = _findCollectionItem(payload.collectionId, payload.itemId);
            if (!found || !found.item) return false;
            replaceActiveDesignTabFromItem(found.item, {
                kind: 'collection',
                collectionId: found.col.id,
                itemId: found.item.id,
                name: found.col.name
            });
            render();
            return true;
        }
        if (payload.kind === 'preset') {
            var fp = _findPreset(payload.mode, payload.presetId);
            if (!fp) return false;
            replaceActiveDesignTabFromItem(fp.preset.config || {}, {
                kind: 'preset',
                mode: fp.mode,
                presetId: fp.preset.id,
                name: fp.preset.name
            });
            render();
            return true;
        }
        return false;
    }

    // ---- #295 Phase C — Save-to-Collection picker ----
    //
    // Pops a small dropdown anchored to an element that lists every
    // workspace collection + a "+ New collection" footer. Reuses the
    // request-builder bar's snapshot so the saved entry carries the
    // full _requestBuilder state (params + headers + auth + scripts),
    // not just the bare body/url. Falls back to a toast on failure.
    function _composeSaveToCollectionPicker(fr, anchor) {
        // Close any in-flight picker.
        var existing = document.querySelector('.bowire-compose-save-picker');
        if (existing) { existing.remove(); return; }
        if (typeof addToCollection !== 'function'
                || typeof createCollection !== 'function') return;

        var picker = el('div', { className: 'bowire-compose-save-picker bowire-dropdown-menu visible' });

        function _doSave(col) {
            try {
                var snap = (typeof _snapshotHoppForCollection === 'function')
                    ? _snapshotHoppForCollection(fr)
                    : {
                        protocol: fr.protocol || 'rest',
                        method: fr.method || 'GET',
                        methodType: 'Unary',
                        body: fr.body || '',
                        messages: [fr.body || ''],
                        metadata: fr.metadata || null,
                        serverUrl: fr.serverUrl || '',
                        urlMode: 'inline',
                        kind: 'request-builder'
                    };
                addToCollection(col.id, snap);
                if (typeof toast === 'function') toast('Saved to "' + col.name + '"', 'success');
                // #295 Phase D — saving promotes the tab's origin to
                // 'collection' so the badge surfaces where the next
                // open of this tab came from logically. We don't
                // overwrite an existing non-fresh origin (e.g. a
                // tab opened from Discover stays that origin even
                // after saving).
                var t = composeTabs.find(function (tt) { return tt.id === activeDesignTabId; });
                if (t && (!t.origin || t.origin.kind === 'fresh')) {
                    t.origin = { kind: 'collection', collectionId: col.id, name: col.name };
                    persistDesignTabs();
                }
            } catch (e) {
                if (typeof toast === 'function') toast('Save failed: ' + e.message, 'error');
            }
        }

        var cols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
            ? collectionsList : [];
        if (cols.length === 0) {
            picker.appendChild(el('div', {
                className: 'bowire-dropdown-item-meta',
                style: 'padding:8px 12px;color:var(--bowire-text-tertiary);font-size:11px',
                textContent: 'No collections yet'
            }));
        } else {
            cols.forEach(function (col) {
                picker.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-dropdown-item',
                    onClick: function () {
                        _doSave(col);
                        picker.remove();
                        render();
                    }
                },
                    el('span', { className: 'bowire-dropdown-item-icon', innerHTML: svgIcon('folder') }),
                    el('span', { textContent: col.name }),
                    el('span', {
                        className: 'bowire-dropdown-item-meta',
                        textContent: (col.items || []).length + ((col.items || []).length === 1 ? ' entry' : ' entries')
                    })
                ));
            });
        }
        picker.appendChild(el('div', { className: 'bowire-dropdown-divider' }));
        picker.appendChild(el('button', {
            type: 'button',
            className: 'bowire-dropdown-item bowire-dropdown-item-create',
            onClick: function () {
                if (typeof bowirePrompt !== 'function') {
                    var col = createCollection();
                    _doSave(col);
                    picker.remove();
                    render();
                    return;
                }
                bowirePrompt('Collection name', {
                    title: 'New collection',
                    placeholder: 'e.g. Smoke tests',
                    confirmText: 'Create'
                }).then(function (name) {
                    if (name === null) { picker.remove(); return; }
                    var trimmed = String(name || '').trim();
                    var col = createCollection(trimmed || undefined);
                    _doSave(col);
                    picker.remove();
                    render();
                });
            }
        },
            el('span', { className: 'bowire-dropdown-item-icon', innerHTML: svgIcon('plus') }),
            el('span', { textContent: 'New collection…' })
        ));

        // Anchor at the bottom-right of the source button. The picker
        // floats absolutely; the parent positions via .bowire-compose-save-picker
        // CSS rule. If the anchor is gone (race during morphdom), drop
        // it on body to at least show.
        var host = (anchor && anchor.parentElement) || document.body;
        host.appendChild(picker);
        setTimeout(function () {
            function _close(e) {
                if (e && picker.contains(e.target)) return;
                picker.remove();
                document.removeEventListener('click', _close);
            }
            document.addEventListener('click', _close);
        }, 0);
    }

    // ---- Renderer ----
    // Called from render-main.js when railMode === 'compose'.
    // Lays out: tab strip (pinned '+' + open tabs) → main builder body
    // for the active tab. Empty state when no tabs are open.
    function renderComposeMain() {
        // Pattern B: workspace-prereq branch first. Compose tabs +
        // collections side panel are workspace-scoped, so without a
        // workspace there's nothing to rehydrate against. The
        // operator clicked Compose on purpose — show the prereq card
        // in the main pane area (skip the tab strip entirely, since
        // there's no Workspace to spawn tabs into yet).
        if (typeof activeWorkspaceId !== 'undefined'
                && !activeWorkspaceId
                && typeof renderWorkspacePrereqEmpty === 'function') {
            var prereqMain = el('div', { id: 'bowire-main-compose', className: 'bowire-main bowire-main-compose' });
            var prereqCol = el('div', { className: 'bowire-compose-main-col' });
            prereqCol.appendChild(renderWorkspacePrereqEmpty({
                icon: 'compose',
                railLabel: 'Compose',
                railBody: 'Compose is the ad-hoc request builder — type a URL, pick a method, hit Execute.'
            }));
            prereqMain.appendChild(prereqCol);
            return prereqMain;
        }

        rehydrateComposeSidePanel();
        // Rehydrate-from-storage on first paint per session.
        rehydrateDesignTabs();
        // Drift guard — the active tab may have been closed by external
        // code that didn't update activeDesignTabId. Re-snap.
        if (activeDesignTabId && !composeTabs.find(function (t) { return t.id === activeDesignTabId; })) {
            activeDesignTabId = composeTabs.length > 0 ? composeTabs[0].id : null;
        }
        // Ensure freeformRequest mirrors the active tab so the
        // execute/autocomplete/test paths address the right request.
        if (activeDesignTabId) {
            var activeTab = composeTabs.find(function (t) { return t.id === activeDesignTabId; });
            if (activeTab) freeformRequest = activeTab.request;
        } else {
            // No active tab — clear the global so a stray render path
            // doesn't try to draw a builder body for a dead reference.
            if (freeformRequest && isHoppRequest(freeformRequest)) freeformRequest = null;
        }

        // The Library (Collections + Presets) moved out of the main
        // pane into the standard workbench sidebar slot — see
        // renderComposeLibrarySidebar() above and BowireComposeRail-
        // Contribution.SidebarKind ("library"). The main pane now
        // collapses to a single column: tab strip + request builder.
        var main = el('div', { id: 'bowire-main-compose', className: 'bowire-main bowire-main-compose' });
        var mainCol = el('div', { className: 'bowire-compose-main-col' });

        // ---- Tab strip ----
        var tabBar = el('div', {
            id: 'bowire-compose-tabs',
            className: 'bowire-request-tabs bowire-compose-tabs',
            onDragOver: function (e) {
                if (e.dataTransfer && e.dataTransfer.types
                        && e.dataTransfer.types.indexOf('application/x-bowire-compose') >= 0) {
                    e.preventDefault();
                    e.dataTransfer.dropEffect = 'copy';
                    tabBar.classList.add('is-drop-target');
                }
            },
            onDragLeave: function () { tabBar.classList.remove('is-drop-target'); },
            onDrop: function (e) {
                tabBar.classList.remove('is-drop-target');
                _handleComposeStripDrop(e);
            }
        });
        var tabScroll = el('div', { className: 'bowire-request-tabs-scroll' });

        // Pinned '+' tab — matches Discover's pattern (just the plus
        // glyph, no spelled-out label). Cosmetically distinct (dashed
        // border, no close affordance) so it doesn't get accidentally
        // closed. Click spawns a fresh request-builder tab + focuses
        // it. Appended AFTER the open builder tabs so it always sits
        // at the right edge of the strip — same convention browsers
        // and IDEs use.
        var pinned = el('button', {
            id: 'bowire-compose-tab-pinned',
            className: 'bowire-compose-tab-pinned',
            type: 'button',
            title: 'New request (Ctrl+L)',
            'aria-label': 'New request',
            onClick: function () {
                spawnDesignTab();
                render();
                requestAnimationFrame(function () {
                    var inp = document.getElementById('bowire-request-builder-url-input');
                    if (inp) inp.focus();
                });
            }
        },
            el('span', { className: 'bowire-compose-tab-pinned-icon', innerHTML: svgIcon('plus') })
        );

        // ---- Open builder tabs ----
        for (var ti = 0; ti < composeTabs.length; ti++) {
            (function (tab) {
                var isActive = tab.id === activeDesignTabId;
                var req = tab.request || {};
                var pid = (req._requestBuilder && req._requestBuilder.protocol) || req.protocol || 'rest';
                var title = designTabTitle(tab);
                var tabEl = el('div', {
                    id: 'bowire-compose-tab-' + tab.id,
                    className: 'bowire-request-tab bowire-compose-tab' + (isActive ? ' active' : '')
                        + (tab.origin && tab.origin.kind && tab.origin.kind !== 'fresh'
                            ? ' has-origin' : ''),
                    title: title,
                    'data-tab-id': tab.id,
                    'data-protocol': pid,
                    onClick: function (e) {
                        var id = e.currentTarget.dataset.tabId;
                        if (id) switchDesignTab(id);
                    },
                    onDragOver: function (e) {
                        if (e.dataTransfer && e.dataTransfer.types
                                && e.dataTransfer.types.indexOf('application/x-bowire-compose') >= 0) {
                            e.preventDefault();
                            e.dataTransfer.dropEffect = 'copy';
                            e.stopPropagation();
                            e.currentTarget.classList.add('is-drop-replace');
                        }
                    },
                    onDragLeave: function (e) {
                        e.currentTarget.classList.remove('is-drop-replace');
                    },
                    onDrop: function (e) {
                        e.currentTarget.classList.remove('is-drop-replace');
                        e.stopPropagation();
                        var id = e.currentTarget.dataset.tabId;
                        _handleComposeTabDrop(e, id);
                    },
                    onContextMenu: function (e) {
                        e.preventDefault();
                        e.stopPropagation();
                        if (typeof showContextMenu !== 'function') return;
                        var id = e.currentTarget.dataset.tabId;
                        if (!id) return;
                        var idx = composeTabs.findIndex(function (t) { return t.id === id; });
                        var hasOthers = composeTabs.length > 1;
                        var hasRight = idx >= 0 && idx < composeTabs.length - 1;
                        showContextMenu(e.clientX, e.clientY, [
                            // Operator feedback: 'auf dem tab context
                            // (rechtsklick) im compose rail geht aktuell
                            // nur verschiedene varianten von schließen,
                            // aber dort könnte man auch den tab kopieren
                            // bzw. clonen anbieten. also den tab mit
                            // seinem aktuellen inhalt/zustand kopieren.'
                            { label: 'Duplicate tab', icon: 'copy', onClick: function () { _duplicateDesignTab(id); } },
                            { separator: true },
                            { label: 'Close', icon: 'close', onClick: function () { closeDesignTab(id); } },
                            {
                                label: 'Close others',
                                disabled: !hasOthers,
                                onClick: function () {
                                    composeTabs.slice().forEach(function (t) {
                                        if (t.id !== id) closeDesignTab(t.id);
                                    });
                                    switchDesignTab(id);
                                }
                            },
                            {
                                label: 'Close tabs to the right',
                                disabled: !hasRight,
                                onClick: function () {
                                    var currentIdx = composeTabs.findIndex(function (t) { return t.id === id; });
                                    if (currentIdx < 0) return;
                                    composeTabs.slice(currentIdx + 1).forEach(function (t) {
                                        closeDesignTab(t.id);
                                    });
                                }
                            }
                        ]);
                    }
                });
                // #295 Phase D — origin badge sits before the title so
                // the eye lands on "where did this come from" before
                // reading the URL. Hidden for fresh tabs (no badge).
                var originBadge = _designTabOriginBadge(tab.origin);
                if (originBadge) tabEl.appendChild(originBadge);
                tabEl.appendChild(el('span', {
                    className: 'bowire-request-tab-name',
                    textContent: title
                }));
                tabEl.appendChild(el('button', {
                    className: 'bowire-request-tab-close',
                    innerHTML: svgIcon('close'),
                    title: 'Close tab',
                    onClick: function (e) {
                        e.stopPropagation();
                        var parent = e.currentTarget.closest('.bowire-compose-tab');
                        var id = parent && parent.dataset.tabId;
                        if (id) closeDesignTab(id);
                    }
                }));
                tabScroll.appendChild(tabEl);
            })(composeTabs[ti]);
        }
        // Pinned '+' sits at the end of the strip — always the last
        // tab, even after the operator opens N tabs (matches Discover
        // + every modern browser / IDE).
        tabScroll.appendChild(pinned);
        tabBar.appendChild(tabScroll);
        mainCol.appendChild(tabBar);
        // Overflow popover — re-use the same helper as Discover's
        // open-methods strip. Pinned '+' stays right-most as a fixed
        // sibling; the chevron sits just before it. Right-click on the
        // overflow popover rows is NOT supported — operators reach
        // Duplicate / Close-others via the inline tab's right-click,
        // which still works for visible tabs.
        requestAnimationFrame(function () {
            var live = document.querySelector('#bowire-compose-tabs .bowire-request-tabs-scroll');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, {
                    tabSelector: '.bowire-compose-tab',
                    fixedSelector: '.bowire-compose-tab-pinned',
                    label: 'More tabs'
                });
            }
        });

        // ---- Body ----
        if (activeDesignTabId) {
            var activeTab2 = composeTabs.find(function (t) { return t.id === activeDesignTabId; });
            if (activeTab2) {
                // Mount the existing request-builder against the
                // active tab. _appendRequestBuilderInto reads
                // freeformRequest directly, which we just refreshed.
                //
                // The wrapper's id is keyed by the active tab id so
                // morphdom rebuilds the whole subtree (URL input, body
                // editor, method dropdown, &c) whenever the operator
                // switches tabs or duplicates one. Without the key,
                // morphdom preserves the old form-input DOM nodes and
                // their `onInput` closures — which captured `fr` at the
                // PREVIOUS tab's render time — so typing into the new
                // active tab silently mutates the previous tab's
                // request. That's exactly what 'Duplicate tab' hit:
                // the clone became active, but the URL input's stale
                // handler still pointed at the source's request object,
                // so editing the clone's URL bled back into the source.
                if (typeof _appendRequestBuilderInto === 'function') {
                    var builderWrap = el('div', {
                        id: 'bowire-compose-builder-' + activeTab2.id,
                        className: 'bowire-compose-builder-wrap'
                    });
                    _appendRequestBuilderInto(builderWrap);
                    mainCol.appendChild(builderWrap);
                }
            }
        } else {
            // Empty state — adopt the canonical rail-empty shape
            // (#301) so Compose matches Recordings / Discover / Mocks
            // / etc. The bowire-main-pad wrapper plus renderEmptyCard
            // centre the card in the main pane consistently.
            var padWrap = el('div', { className: 'bowire-main-pad' });
            padWrap.appendChild(renderEmptyCard({
                icon: 'compose',
                headline: 'No request open',
                body: 'Start with a new request — type a URL, pick a method, hit Execute. Ctrl+L opens a new request from anywhere.',
                actions: [{
                    // Label intentionally drops the '+' that the tab-strip
                    // pinned-new chip carries — in the welcome the card
                    // itself is the action surface, and renderEmptyCard's
                    // convention is "the label IS the verb". The plus made
                    // the welcome read as a noisy duplicate of the strip
                    // chip. Operator feedback unified all rails' welcome
                    // labels to plain verbs ("New workspace", "New
                    // collection", etc.).
                    label: 'New request',
                    primary: true,
                    onClick: function () {
                        spawnDesignTab();
                        render();
                        requestAnimationFrame(function () {
                            var inp = document.getElementById('bowire-request-builder-url-input');
                            if (inp) inp.focus();
                        });
                    }
                }, {
                    // Per-rail welcome tour: explains the freeform
                    // builder loop (URL → method → body → Execute).
                    // Force-mode so the operator can re-trigger from
                    // the same empty card after dismissal.
                    id: 'bowire-compose-empty-tour-btn',
                    label: 'Take a tour',
                    onClick: function () {
                        if (typeof window !== 'undefined'
                            && typeof window.bowireStartComposeRequestTour === 'function') {
                            window.bowireStartComposeRequestTour({ force: true });
                        }
                    }
                }]
            }));
            mainCol.appendChild(padWrap);
        }

        // Library moved to the standard sidebar slot — the main pane
        // is now a single column. The activity rail + sidebar +
        // sidebar splitter sit to the left of `main` as part of the
        // standard workbench shell (see render-env-auth.js's body
        // layout); the Compose tab strip + request builder fill the
        // entire main pane.
        main.appendChild(mainCol);
        return main;
    }

    // ---- Helpers used by Ctrl+L + Home CTA ----
    //
    // Switch to the Compose rail and spawn a fresh tab. Reused by the
    // global Ctrl+L shortcut and the Home portal's 'Just fire a
    // request →' card. Returns the spawned tab id so callers can
    // chain focus / further setup if they want.
    function gotoComposeAndSpawn(opts) {
        if (typeof railMode !== 'undefined' && railMode !== 'compose') {
            railMode = 'compose';
            try { localStorage.setItem('bowire_rail_mode', 'compose'); } catch { /* ignore */ }
        }
        var id = spawnDesignTab(opts || {});
        render();
        return id;
    }
