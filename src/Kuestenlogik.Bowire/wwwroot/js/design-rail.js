    // ---- #293 Design Rail ----
    //
    // The Design rail is the home for the ad-hoc request-builder. Before
    // #293 the builder piggy-backed on whatever tab was active in
    // Discover, which conflated two distinct workflows: Discover =
    // schema-driven (pick a server-advertised method), Design = ad-hoc
    // (draft a request from scratch). The builder now has its own rail
    // with its own tab strip.
    //
    // State model:
    //   designTabs = [{ id, request }]
    //     where `request` is a fully-formed freeformRequest object
    //     (carrying the _requestBuilder marker). On tab switch we
    //     swap the GLOBAL freeformRequest slot so every existing
    //     code path (render, execute, autocomplete, tests, …) keeps
    //     working unchanged.
    //   activeDesignTabId : id | null
    //   _designTabIdCounter : monotonic counter
    //
    // Persistence: bowire_design_tabs under wsKey(), same convention
    // as bowire_request_tabs. Tabs survive a reload but are NOT
    // round-tripped through .bww export — see _WORKSPACE_BROWSER_STATE_KEYS
    // in prologue.js.

    let designTabs = [];
    let activeDesignTabId = null;
    let _designTabIdCounter = 0;
    let _designTabsRehydrated = false;

    function _nextDesignTabId() {
        for (;;) {
            var id = 'design_' + (++_designTabIdCounter);
            var clash = false;
            for (var i = 0; i < designTabs.length; i++) {
                if (designTabs[i].id === id) { clash = true; break; }
            }
            if (!clash) return id;
        }
    }

    // Persist open Design tabs + active id. Strips the live File ref
    // off binary bodies (browser File objects don't survive
    // JSON.stringify) — the on-disk filename stays in binaryName so
    // the operator at least sees what they had picked.
    function persistDesignTabs() {
        try {
            var data = {
                tabs: designTabs.map(function (t) {
                    var req = t.request || {};
                    var rb = req._requestBuilder || {};
                    // Shallow clone so the original keeps its live File ref.
                    var rbClone = Object.assign({}, rb);
                    rbClone._binaryRef = null;
                    var reqClone = Object.assign({}, req, { _requestBuilder: rbClone });
                    return { id: t.id, request: reqClone };
                }),
                active: activeDesignTabId,
            };
            localStorage.setItem(wsKey('bowire_design_tabs'), JSON.stringify(data));
        } catch (e) {
            if (typeof markSaveFailed === 'function') markSaveFailed('design tabs', e);
        }
    }

    function _restoreDesignTabsFromStorage() {
        try {
            var raw = localStorage.getItem(wsKey('bowire_design_tabs'));
            if (!raw) return null;
            var data = JSON.parse(raw);
            if (!data || !Array.isArray(data.tabs)) return null;
            return data;
        } catch { return null; }
    }

    function rehydrateDesignTabs() {
        if (_designTabsRehydrated) return;
        _designTabsRehydrated = true;
        if (designTabs.length > 0) return;
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
            designTabs.push({ id: t.id, request: t.request });
            var n = (t.id.match(/^design_(\d+)$/) || [])[1];
            if (n) {
                var num = parseInt(n, 10);
                if (num > _designTabIdCounter) _designTabIdCounter = num;
            }
        }
        if (designTabs.length > 0) {
            var keep = designTabs.find(function (x) { return x.id === data.active; }) || designTabs[0];
            activeDesignTabId = keep.id;
        }
    }

    // Spawn a fresh Design tab + activate it. opts mirrors
    // startHoppRequest's opts: { protocol, url, method }.
    // Returns the new tab id.
    function spawnDesignTab(opts) {
        opts = opts || {};
        // Park the currently-active tab's request before swapping.
        _snapshotActiveDesignTab();
        var pid = opts.protocol || 'rest';
        // Build a fresh request-builder request without touching
        // railMode / sidebarView — startFreeformRequest used to flip
        // those to 'discover' as a side effect, which now leaks the
        // operator off the Design rail every time we spawn.
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
        var tab = { id: _nextDesignTabId(), request: req };
        designTabs.push(tab);
        activeDesignTabId = tab.id;
        // Swap the global freeformRequest so the rest of the workbench
        // (execute, autocomplete, tests, &c) addresses this tab.
        freeformRequest = req;
        persistDesignTabs();
        return tab.id;
    }

    // Snapshot the currently-bound freeformRequest back into the
    // active Design tab so the in-flight edits survive a tab switch.
    function _snapshotActiveDesignTab() {
        if (!activeDesignTabId) return;
        var tab = designTabs.find(function (t) { return t.id === activeDesignTabId; });
        if (!tab) return;
        if (freeformRequest) tab.request = freeformRequest;
    }

    function switchDesignTab(id) {
        if (id === activeDesignTabId) return;
        _snapshotActiveDesignTab();
        var tab = designTabs.find(function (t) { return t.id === id; });
        if (!tab) return;
        activeDesignTabId = tab.id;
        freeformRequest = tab.request;
        persistDesignTabs();
        render();
    }

    function closeDesignTab(id) {
        var idx = designTabs.findIndex(function (t) { return t.id === id; });
        if (idx < 0) return;
        var wasActive = (id === activeDesignTabId);
        designTabs.splice(idx, 1);
        if (wasActive) {
            // Pick a sane neighbour: prefer right, then left, then none.
            var next = designTabs[idx] || designTabs[idx - 1] || null;
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

    // ---- Renderer ----
    // Called from render-main.js when railMode === 'design'.
    // Lays out: tab strip (pinned '+' + open tabs) → main builder body
    // for the active tab. Empty state when no tabs are open.
    function renderDesignMain() {
        // Rehydrate-from-storage on first paint per session.
        rehydrateDesignTabs();
        // Drift guard — the active tab may have been closed by external
        // code that didn't update activeDesignTabId. Re-snap.
        if (activeDesignTabId && !designTabs.find(function (t) { return t.id === activeDesignTabId; })) {
            activeDesignTabId = designTabs.length > 0 ? designTabs[0].id : null;
        }
        // Ensure freeformRequest mirrors the active tab so the
        // execute/autocomplete/test paths address the right request.
        if (activeDesignTabId) {
            var activeTab = designTabs.find(function (t) { return t.id === activeDesignTabId; });
            if (activeTab) freeformRequest = activeTab.request;
        } else {
            // No active tab — clear the global so a stray render path
            // doesn't try to draw a builder body for a dead reference.
            if (freeformRequest && isHoppRequest(freeformRequest)) freeformRequest = null;
        }

        var main = el('div', { id: 'bowire-main-design', className: 'bowire-main bowire-main-design' });

        // ---- Tab strip ----
        var tabBar = el('div', { id: 'bowire-design-tabs', className: 'bowire-request-tabs bowire-design-tabs' });
        var tabScroll = el('div', { className: 'bowire-request-tabs-scroll' });

        // Pinned '+ New Request' tab — cosmetically distinct: dashed
        // border, no close affordance, label spells out 'New Request'.
        // Click spawns a fresh request-builder tab + focuses it.
        var pinned = el('button', {
            id: 'bowire-design-tab-pinned',
            className: 'bowire-design-tab-pinned',
            type: 'button',
            title: 'New request (Ctrl+L)',
            onClick: function () {
                spawnDesignTab();
                render();
                requestAnimationFrame(function () {
                    var inp = document.getElementById('bowire-request-builder-url-input');
                    if (inp) inp.focus();
                });
            }
        },
            el('span', { className: 'bowire-design-tab-pinned-icon', innerHTML: svgIcon('plus') }),
            el('span', { className: 'bowire-design-tab-pinned-label', textContent: 'New Request' })
        );
        tabScroll.appendChild(pinned);

        // ---- Open builder tabs ----
        for (var ti = 0; ti < designTabs.length; ti++) {
            (function (tab) {
                var isActive = tab.id === activeDesignTabId;
                var req = tab.request || {};
                var pid = (req._requestBuilder && req._requestBuilder.protocol) || req.protocol || 'rest';
                var title = designTabTitle(tab);
                var tabEl = el('div', {
                    id: 'bowire-design-tab-' + tab.id,
                    className: 'bowire-request-tab bowire-design-tab' + (isActive ? ' active' : ''),
                    title: title,
                    'data-tab-id': tab.id,
                    'data-protocol': pid,
                    onClick: function (e) {
                        var id = e.currentTarget.dataset.tabId;
                        if (id) switchDesignTab(id);
                    },
                    onContextMenu: function (e) {
                        e.preventDefault();
                        e.stopPropagation();
                        if (typeof showContextMenu !== 'function') return;
                        var id = e.currentTarget.dataset.tabId;
                        if (!id) return;
                        var idx = designTabs.findIndex(function (t) { return t.id === id; });
                        var hasOthers = designTabs.length > 1;
                        var hasRight = idx >= 0 && idx < designTabs.length - 1;
                        showContextMenu(e.clientX, e.clientY, [
                            { label: 'Close', icon: 'close', onClick: function () { closeDesignTab(id); } },
                            {
                                label: 'Close others',
                                disabled: !hasOthers,
                                onClick: function () {
                                    designTabs.slice().forEach(function (t) {
                                        if (t.id !== id) closeDesignTab(t.id);
                                    });
                                    switchDesignTab(id);
                                }
                            },
                            {
                                label: 'Close tabs to the right',
                                disabled: !hasRight,
                                onClick: function () {
                                    var currentIdx = designTabs.findIndex(function (t) { return t.id === id; });
                                    if (currentIdx < 0) return;
                                    designTabs.slice(currentIdx + 1).forEach(function (t) {
                                        closeDesignTab(t.id);
                                    });
                                }
                            }
                        ]);
                    }
                },
                    el('span', {
                        className: 'bowire-request-tab-name',
                        textContent: title
                    }),
                    el('button', {
                        className: 'bowire-request-tab-close',
                        innerHTML: svgIcon('close'),
                        title: 'Close tab',
                        onClick: function (e) {
                            e.stopPropagation();
                            var parent = e.currentTarget.closest('.bowire-design-tab');
                            var id = parent && parent.dataset.tabId;
                            if (id) closeDesignTab(id);
                        }
                    })
                );
                tabScroll.appendChild(tabEl);
            })(designTabs[ti]);
        }
        tabBar.appendChild(tabScroll);
        main.appendChild(tabBar);

        // ---- Body ----
        if (activeDesignTabId) {
            var activeTab2 = designTabs.find(function (t) { return t.id === activeDesignTabId; });
            if (activeTab2) {
                // Mount the existing request-builder against the
                // active tab. _appendRequestBuilderInto reads
                // freeformRequest directly, which we just refreshed.
                if (typeof _appendRequestBuilderInto === 'function') {
                    _appendRequestBuilderInto(main);
                }
            }
        } else {
            // Empty state — no tabs open. Friendly hint.
            var empty = el('div', { className: 'bowire-design-empty' },
                el('div', { className: 'bowire-design-empty-icon', innerHTML: svgIcon('send') }),
                el('div', { className: 'bowire-design-empty-headline', textContent: 'No request open' }),
                el('div', {
                    className: 'bowire-design-empty-body',
                    textContent: 'Click + New Request to start drafting an ad-hoc request. Tip: Ctrl+L opens a new request from anywhere.'
                }),
                el('button', {
                    className: 'bowire-design-empty-cta',
                    type: 'button',
                    textContent: '+ New Request',
                    onClick: function () {
                        spawnDesignTab();
                        render();
                        requestAnimationFrame(function () {
                            var inp = document.getElementById('bowire-request-builder-url-input');
                            if (inp) inp.focus();
                        });
                    }
                })
            );
            main.appendChild(empty);
        }
        return main;
    }

    // ---- Helpers used by Ctrl+L + Home CTA ----
    //
    // Switch to the Design rail and spawn a fresh tab. Reused by the
    // global Ctrl+L shortcut and the Home portal's 'Just fire a
    // request →' card. Returns the spawned tab id so callers can
    // chain focus / further setup if they want.
    function gotoDesignAndSpawn(opts) {
        if (typeof railMode !== 'undefined' && railMode !== 'design') {
            railMode = 'design';
            try { localStorage.setItem('bowire_rail_mode', 'design'); } catch { /* ignore */ }
        }
        var id = spawnDesignTab(opts || {});
        render();
        return id;
    }
