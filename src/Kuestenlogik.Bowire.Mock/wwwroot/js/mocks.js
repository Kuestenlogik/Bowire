// mocks.js — workbench-side controller for UI-driven mock servers (#56).
//
// Backend surface (BowireMockManagementEndpoints in Bowire.Mock):
//   POST   /api/mocks            -> start mock from a recording payload
//   GET    /api/mocks            -> list running mocks
//   GET    /api/mocks/{id}       -> single mock detail
//   DELETE /api/mocks/{id}       -> stop mock
//
// This file ships only the read/start/stop loop + a minimal Mocks
// overlay. The Mocks-as-sidebar-view (replacing/joining Recordings)
// + topbar status indicator land in a follow-up; the start-from-
// recording button below already unblocks the "I captured something,
// now run it as a mock without dropping to a terminal" path.
//
// Issue #57 (per-mock request log + SSE tail) is wired against the
// same BowireMockHostManager but the endpoint + UI lands separately.

    // ---------- state ----------
    var mocksList = [];                  // [{ mockId, recordingName, port, startedAt }]
    // mocksManagerOpen retired — Mocks rail mode owns the surface.
    var mocksLoadInFlight = false;
    // #57 per-mock log state: mockId -> { total, capacity, entries, pollTimer }
    var mockLogState = {};
    var mockLogOpenFor = null;       // mockId currently expanded in the manager
    // #170 fault-editor state: mockId -> { rules:[...], open:bool, dirty:bool, error:string }
    var mockFaultState = {};
    // #560 schema-mock start draft — module-scope so the paste survives a
    // re-render (log polling / list refresh) instead of clearing mid-edit.
    var schemaMockDraft = { kind: 'openapi', text: '', open: false };

    function loadMocks() {
        if (mocksLoadInFlight) return Promise.resolve(mocksList);
        mocksLoadInFlight = true;
        // Single registry after the #223 consolidation — every running
        // mock (whether started via POST /api/mocks inline-recording
        // body or via the "Use as mock" recordingId-lookup body) lives
        // in BowireMockHostManager and surfaces here.
        return fetch(config.prefix + '/api/mocks')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && Array.isArray(data.mocks)) mocksList = data.mocks;
                return mocksList;
            })
            .catch(function () { return mocksList; })
            .finally(function () { mocksLoadInFlight = false; });
    }

    // ---------- actions ----------
    // Start a mock from a recording shape (matching wwwroot's recordingsList
    // item: { id, name, steps, sourceSchema?, ... }). Wraps it into the
    // BowireRecording document shape the backend / MockServer expect.
    // The optional `silent` flag suppresses the action-log entry when
    // the call originates from a resolver (undo/redo applying its
    // own inverse). Same pattern favorite-remove uses with
    // _toggleFavoriteRaw — prevents the redo of mock-create / undo of
    // mock-delete from re-recording a fresh entry of the same kind.
    function startMockFromRecording(rec, port, silent) {
        if (!rec) return Promise.reject(new Error('No recording'));
        var doc = JSON.stringify({ recordings: [rec] });
        return fetch(config.prefix + '/api/mocks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                recording: doc,
                name: rec.name || ('recording-' + rec.id),
                port: port || 0    // 0 = OS-assigned
            })
        }).then(function (r) {
            if (!r.ok) {
                return r.json().catch(function () { return { title: 'Mock start failed (' + r.status + ')' }; })
                    .then(function (err) { throw new Error(problemTitle(err, 'Mock start failed')); });
            }
            return r.json();
        }).then(function (summary) {
            mocksList = [summary].concat(mocksList.filter(function (m) { return m.mockId !== summary.mockId; }));
            if (railMode === 'intercept') render();
            // #303 — advance the build-a-mock tour when a mock host
            // successfully boots. Fires regardless of which entry path
            // ran the start (recording detail, sidebar context, &c.).
            if (typeof window !== 'undefined'
                && typeof window.bowireFireTourEvent === 'function') {
                window.bowireFireTourEvent('mock-started', { mockId: summary.mockId, port: summary.port });
            }
            // Toast the mock-create so Ctrl/Cmd+Z stops the just-
            // started mock host and the operator gets an inline Undo.
            // undoSpec carries the mockId + the recording payload +
            // chosen port so the resolver can rehydrate both directions
            // after reload (stop = DELETE /api/mocks/{id}; restart =
            // re-POST the same body). The undo/redo closures pass
            // silent=true so the inverse calls don't append a fresh
            // entry of their own. Silent restarts (resolver-driven
            // redo) fall back to a quiet toast — no Undo button, no
            // action-log entry — so the operator still sees the port
            // confirmation.
            var _mockName = rec.name || ('recording-' + rec.id);
            if (!silent && typeof toast === 'function') {
                var recSnapshot = JSON.parse(JSON.stringify(rec));
                var summaryId = summary.mockId;
                var summaryPort = port || 0;
                toast('Created mock "' + _mockName + '" on port ' + summary.port, 'success', {
                    undo: function () { stopMock(summaryId); },
                    logAction: {
                        kind: 'mock-create',
                        rail: 'mocks',
                        title: 'Created mock "' + _mockName + '"',
                        undoSpec: { mockId: summaryId, recording: recSnapshot, port: summaryPort },
                        redo: function () { startMockFromRecording(recSnapshot, summaryPort, true); }
                    }
                });
            } else if (typeof toast === 'function') {
                toast('Mock running on port ' + summary.port, 'success');
            }
            return summary;
        }).catch(function (err) {
            toast(err.message || 'Mock start failed', 'error');
            throw err;
        });
    }

    // #560 — start a mock straight from a schema (OpenAPI / GraphQL SDL),
    // no recording needed. POSTs the { schemaKind, schemaInline } start
    // shape to the same /api/mocks endpoint, then seeds the mock-config
    // artifact so the refinement editors (#561) have a target.
    function startMockFromSchema(schemaKind, schemaInline, port) {
        if (!schemaKind) {
            if (typeof toast === 'function') toast('Pick a schema kind', 'error');
            return Promise.reject(new Error('Pick a schema kind'));
        }
        if (!schemaInline || !schemaInline.trim()) {
            if (typeof toast === 'function') toast('Paste a schema first', 'error');
            return Promise.reject(new Error('Paste a schema first'));
        }
        return fetch(config.prefix + '/api/mocks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ schemaKind: schemaKind, schemaInline: schemaInline, port: port || 0 })
        }).then(function (r) {
            if (!r.ok) {
                return r.json().catch(function () { return { title: 'Schema mock start failed (' + r.status + ')' }; })
                    .then(function (err) { throw new Error(problemTitle(err, 'Schema mock start failed')); });
            }
            return r.json();
        }).then(function (summary) {
            mocksList = [summary].concat(mocksList.filter(function (m) { return m.mockId !== summary.mockId; }));
            mockSelectedId = summary.mockId;
            // Seed the config artifact (records the schema provenance) so the
            // per-field / conditional editors have a target. Non-fatal.
            seedMockConfig(summary.mockId, schemaKind);
            if (typeof window !== 'undefined'
                && typeof window.bowireFireTourEvent === 'function') {
                window.bowireFireTourEvent('mock-started', { mockId: summary.mockId, port: summary.port });
            }
            // Clear the draft on success so the card resets for the next one.
            schemaMockDraft.text = '';
            if (typeof toast === 'function') {
                var startedId = summary.mockId;
                toast('Schema mock running on port ' + summary.port, 'success', {
                    undo: function () { stopMock(startedId); }
                });
            }
            if (railMode === 'intercept') render();
            return summary;
        }).catch(function (err) {
            if (typeof toast === 'function') toast(err.message || 'Schema mock start failed', 'error');
            throw err;
        });
    }

    // #560 — write an initial mock-configuration so GET/PUT
    // /api/mocks/{id}/config has a persisted target, recording which schema
    // the mock came from. Best-effort: a failure is harmless because the GET
    // falls back to a default envelope until the operator first edits.
    function seedMockConfig(mockId, schemaKind) {
        var wsId = (typeof activeWorkspaceId !== 'undefined' && activeWorkspaceId) ? activeWorkspaceId : '';
        var qs = wsId ? ('?workspaceId=' + encodeURIComponent(wsId)) : '';
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/config' + qs, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ configFormatVersion: 1, source: { kind: schemaKind }, fieldOverrides: [] })
        }).catch(function () { /* non-fatal */ });
    }

    // #57: pull a window of the per-mock request log. Poll loop keeps
    // the open drawer fresh; backend supports `since=<lastSeq>` for
    // tail behaviour, used here to merge new entries onto the front.
    function loadMockLog(mockId) {
        var st = mockLogState[mockId] || (mockLogState[mockId] = { entries: [], total: 0, capacity: 0, lastSeq: 0 });
        var qs = 'limit=200' + (st.lastSeq ? '&since=' + st.lastSeq : '');
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/requests?' + qs)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data || !Array.isArray(data.entries)) return st;
                st.total = data.total;
                st.capacity = data.capacity;
                if (data.entries.length) {
                    // Newest first from backend. Prepend, cap at capacity.
                    st.entries = data.entries.concat(st.entries).slice(0, st.capacity || 200);
                    st.lastSeq = Math.max(st.lastSeq, data.entries[0].sequence || 0);
                }
                return st;
            }).catch(function () { return st; });
    }

    function startMockLogPolling(mockId) {
        var st = mockLogState[mockId] || (mockLogState[mockId] = { entries: [], total: 0, capacity: 0, lastSeq: 0 });
        if (st.pollTimer) return;
        var tick = function () {
            loadMockLog(mockId).then(function () {
                if (mockLogOpenFor === mockId && railMode === 'intercept') render();
            });
            st.pollTimer = setTimeout(tick, 2000);
        };
        tick();
    }

    function stopMockLogPolling(mockId) {
        var st = mockLogState[mockId];
        if (st && st.pollTimer) { clearTimeout(st.pollTimer); st.pollTimer = null; }
    }

    function stopMock(mockId) {
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId), { method: 'DELETE' })
            .then(function (r) {
                if (r.ok || r.status === 404) {
                    mocksList = mocksList.filter(function (m) { return m.mockId !== mockId; });
                    stopMockLogPolling(mockId);
                    delete mockLogState[mockId];
                    if (mockLogOpenFor === mockId) mockLogOpenFor = null;
                    if (railMode === 'intercept') render();
                    return true;
                }
                throw new Error('Stop failed (' + r.status + ')');
            }).catch(function (err) {
                toast(err.message, 'error');
                return false;
            });
    }

    // ---------- UI overlay ----------
    // Minimal full-page overlay (same display pattern as the Recordings
    // manager): table of running mocks + Stop per row + a Refresh
    // affordance. No start-from-here flow yet — start happens via the
    // "Run as mock" button on each Recording row.
    // renderMocksManager modal retired in #133 Phase 3.
    // openMocksManager() now jumps to railMode = 'mocks'.

    function openMocksManager() {
        // v2.2 — Mocks rail folded into Intercept → Mock servers
        // sub-tab. Callers that still invoke openMocksManager()
        // (recording's "Run as mock" success, sidebar 'new mock'
        // shortcut) land on the equivalent sub-tab.
        railMode = 'intercept';
        try { localStorage.setItem('bowire_rail_mode', 'intercept'); } catch { /* ignore */ }
        try { localStorage.setItem('bowire_intercept_sub_tab', 'mock-servers'); } catch { /* ignore */ }
        if (typeof sidebarView !== 'undefined') sidebarView = 'intercept';
        try { localStorage.setItem('bowire_sidebar_view', 'intercept'); } catch { /* ignore */ }
        // Best-effort sync the JS state in the rail fragment so the
        // upcoming render doesn't have to wait for a localStorage
        // re-read at module init time.
        if (typeof interceptSubView !== 'undefined') interceptSubView = 'mock-servers';
        loadMocks().then(function () { render(); });
    }

    // v2.2 — Mock-servers content lives inside the Intercept rail's
    // "Mock servers" sub-tab. The shared shim exposes (a) the same
    // mocksList / start / stop the Recordings rail's "Run as mock"
    // already relied on, plus (b) a renderRailMain hook the Intercept
    // sub-tab calls so the Mock-package can keep ownership of the
    // mock-host detail surface (URL card + log polling + stop).
    //
    // renderRailMain renders into the supplied pane and returns the
    // same pane (or a replacement node). When the Mock package isn't
    // referenced this shim simply isn't installed and the Intercept
    // sub-tab renders the "Mock package not loaded" empty state.
    // #560 — the "Start a schema mock" affordance at the top of the Mocks
    // rail. Collapsed by default; expands to a schema-kind picker + a paste
    // box + Start. Draft state lives in schemaMockDraft so a re-render (log
    // poll / list refresh) doesn't wipe an in-progress paste.
    function renderSchemaMockCard() {
        var card = el('div', {
            className: 'bowire-mocks-schema-card',
            style: 'margin-bottom:16px;padding:12px;border:1px solid var(--bowire-border-subtle);border-radius:8px'
        });
        var head = el('div', { style: 'display:flex;align-items:center;gap:8px;cursor:pointer' },
            el('span', { className: 'bowire-sources-section', style: 'margin:0', textContent: 'Start a schema mock' }),
            el('span', { className: 'bowire-home-section-count', textContent: schemaMockDraft.open ? 'hide' : 'new' })
        );
        head.onclick = function () { schemaMockDraft.open = !schemaMockDraft.open; render(); };
        card.appendChild(head);
        if (!schemaMockDraft.open) return card;

        card.appendChild(el('p', {
            className: 'bowire-sources-hint',
            textContent: 'Spin up a mock straight from a schema — no recording needed. Paste an OpenAPI document or a GraphQL SDL, then Start.'
        }));

        var kindSel = el('select', {
            className: 'bowire-mocks-schema-kind',
            style: 'margin:4px 0 8px;padding:4px'
        },
            el('option', { value: 'openapi', textContent: 'OpenAPI (YAML / JSON)' }),
            el('option', { value: 'graphql', textContent: 'GraphQL (SDL)' })
        );
        kindSel.value = schemaMockDraft.kind;
        kindSel.onchange = function () { schemaMockDraft.kind = kindSel.value; };

        var ta = el('textarea', {
            className: 'bowire-mocks-schema-input',
            rows: 7,
            placeholder: 'Paste your OpenAPI document or GraphQL SDL…',
            style: 'width:100%;box-sizing:border-box;font-family:var(--bowire-font-mono);font-size:12px'
        });
        ta.value = schemaMockDraft.text;
        ta.oninput = function () { schemaMockDraft.text = ta.value; };

        var startBtn = el('button', {
            className: 'bowire-empty-card-action',
            style: 'margin-top:8px',
            textContent: 'Start mock'
        });
        startBtn.onclick = function () {
            // Rejection already surfaced a toast inside startMockFromSchema;
            // swallow it here so it doesn't bubble as an unhandled rejection.
            startMockFromSchema(schemaMockDraft.kind, ta.value).catch(function () { });
        };

        card.appendChild(el('label', { className: 'bowire-mocks-schema-field', style: 'display:block' },
            el('span', { style: 'display:block;font-size:12px;margin-bottom:2px', textContent: 'Schema kind' }),
            kindSel));
        card.appendChild(ta);
        card.appendChild(startBtn);
        return card;
    }

    function renderMocksRailMain(pane) {
        if (!pane) return pane;
        // Workspace-prereq guard mirrors the legacy railMode='mocks'
        // arm: the mock list is workspace-scoped, so painting it
        // pre-workspace is misleading.
        if (typeof activeWorkspaceId !== 'undefined' && !activeWorkspaceId
            && typeof renderWorkspacePrereqEmpty === 'function') {
            pane.appendChild(renderWorkspacePrereqEmpty({
                icon: 'mock',
                railLabel: 'Mock servers',
                railBody: 'Mock servers spin up a fake host from a recording so your client can hit a stable URL instead of the real service.'
            }));
            return pane;
        }
        var wrap = el('div', { className: 'bowire-mocks-wrap bowire-main-pad' });

        var hasAny = Array.isArray(mocksList) && mocksList.length > 0;
        var hasPresets = typeof loadPresets === 'function'
            && (loadPresets('mocks') || []).length > 0;
        if (typeof renderPresetsBar === 'function' && (hasAny || hasPresets)) {
            try {
                wrap.appendChild(renderPresetsBar({
                    mode: 'mocks',
                    canSave: function () { return !!mockSelectedId; },
                    canSaveHint: 'Select a mock first',
                    snapshot: function () {
                        var sel = (mocksList || []).find(function (m) { return m.mockId === mockSelectedId; });
                        return {
                            mockId: sel ? sel.mockId : null,
                            port: sel ? sel.port : null,
                            recordingId: sel ? sel.recordingId : null,
                            recordingName: sel ? sel.recordingName : null,
                            logPolling: mockLogOpenFor === (sel && sel.mockId)
                        };
                    },
                    apply: function (cfg) {
                        if (!cfg) return;
                        if (cfg.mockId
                            && (mocksList || []).some(function (m) { return m.mockId === cfg.mockId; })) {
                            mockSelectedId = cfg.mockId;
                        } else if (cfg.recordingId) {
                            var byRec = (mocksList || []).find(function (m) {
                                return m.recordingId === cfg.recordingId;
                            });
                            if (byRec) mockSelectedId = byRec.mockId;
                        }
                        if (cfg.logPolling && mockSelectedId) {
                            mockLogOpenFor = mockSelectedId;
                            startMockLogPolling(mockSelectedId);
                        }
                    }
                }));
            } catch (e) { /* presets.js not loaded — skip */ }
        }

        // #560 — schema-mock start affordance, always available at the top
        // of the rail (works with zero recordings, unlike "Run as mock").
        wrap.appendChild(renderSchemaMockCard());

        var selected = (mocksList || []).find(function (m) { return m.mockId === mockSelectedId; });
        if (!selected) {
            wrap.appendChild(renderEmptyCard({
                icon: 'mock',
                headline: hasAny ? 'Pick a mock server' : 'No mock servers running',
                body: hasAny
                    ? 'Pick a running mock from the sidebar to see its URL, live request log, and stop control.'
                    : 'Mock servers are standalone replay hosts. Start one from a schema with "Start a schema mock" above (no recording needed), or switch to the Recordings rail and use "Run as mock" on a captured session. Looking for one-line response substitution inside the proxy / middleware pipeline? Use this rail’s Live overrides sub-tab.',
                actions: hasAny ? [] : [
                    {
                        label: 'Go to Recordings',
                        primary: true,
                        onClick: function () {
                            railMode = 'recordings';
                            try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                            render();
                        }
                    },
                    {
                        id: 'bowire-mocks-empty-tour-btn',
                        label: 'Take a tour',
                        onClick: function () {
                            if (typeof window !== 'undefined'
                                && typeof window.bowireStartBuildMockTour === 'function') {
                                window.bowireStartBuildMockTour({ force: true });
                            }
                        }
                    }
                ]
            }));
            pane.appendChild(wrap);
            return pane;
        }

        var url = 'http://127.0.0.1:' + selected.port;
        wrap.appendChild(el('h2', {
            className: 'bowire-sources-title',
            textContent: selected.recordingName || ('mock-' + selected.port)
        }));
        wrap.appendChild(el('p', {
            className: 'bowire-sources-subtitle',
            textContent: 'Mock host on port ' + selected.port + ' · started ' + (selected.startedAt || 'unknown')
        }));

        var urlCard = el('div', { className: 'bowire-mocks-url-card' });
        urlCard.appendChild(el('code', { className: 'bowire-mocks-url', textContent: url }));
        urlCard.appendChild(el('button', {
            className: 'bowire-empty-card-action',
            textContent: 'Copy URL',
            onClick: function () {
                if (navigator.clipboard) {
                    navigator.clipboard.writeText(url).then(function () {
                        toast('Mock URL copied: ' + url, 'success');
                    });
                }
            }
        }));
        urlCard.appendChild(el('a', {
            className: 'bowire-empty-card-action',
            href: url,
            target: '_blank',
            rel: 'noopener',
            textContent: 'Open in tab'
        }));
        // R3a — Intercept → Mock servers detail → Discover transition.
        // Add the mock's loopback URL to the active workspace's sources
        // and switch to the Discover rail focused on it. Uses the same
        // serverUrls + persistServerUrls + refreshSourceServices pattern
        // the manual "+ URL" flow uses (see request-builder.js's
        // auto-add helper) so discovery kicks off exactly the same way.
        urlCard.appendChild(el('button', {
            className: 'bowire-empty-card-action',
            textContent: 'Discover against this mock',
            title: 'Add this mock URL to the active workspace and jump to the Discover rail',
            onClick: function () {
                try {
                    if (typeof serverUrls !== 'undefined'
                        && Array.isArray(serverUrls)
                        && serverUrls.indexOf(url) < 0) {
                        serverUrls.push(url);
                        if (typeof connectionStatuses !== 'undefined') {
                            connectionStatuses[url] = 'disconnected';
                        }
                        if (typeof ensureAliasForUrl === 'function') ensureAliasForUrl(url);
                        if (typeof persistServerUrls === 'function') persistServerUrls();
                    }
                } catch (e) { /* fall through; rail jump still useful */ }
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch (_) { /* ignore */ }
                if (typeof setSidebarView === 'function') setSidebarView('services');
                if (typeof sourcesSelectedUrl !== 'undefined') sourcesSelectedUrl = url;
                if (typeof refreshSourceServices === 'function') {
                    try { refreshSourceServices(url); } catch (_) { /* ignore */ }
                } else if (typeof fetchServices === 'function') {
                    try { fetchServices(); } catch (_) { /* ignore */ }
                }
                render();
                if (typeof toast === 'function') {
                    toast(url + ' added to sources — open on Discover rail', 'success');
                }
            }
        }));
        urlCard.appendChild(el('button', {
            className: 'bowire-empty-card-action bowire-recording-action-danger',
            textContent: 'Stop mock',
            onClick: function () {
                stopMock(selected.mockId);
            }
        }));
        wrap.appendChild(urlCard);

        var logCard = el('div', { className: 'bowire-mocks-log-card', style: 'margin-top:16px' });
        var logOpen = mockLogOpenFor === selected.mockId;
        var logSt = mockLogState[selected.mockId] || { total: 0, entries: [] };
        logCard.appendChild(el('div', { className: 'bowire-sources-section', style: 'display:flex;align-items:center;gap:8px' },
            el('span', { textContent: 'Live request log' }),
            el('span', { className: 'bowire-home-section-count', textContent: logSt.total + ' request' + (logSt.total === 1 ? '' : 's') }),
            el('button', {
                className: 'bowire-empty-card-action',
                textContent: logOpen ? 'Pause polling' : 'Start polling',
                onClick: function () {
                    if (mockLogOpenFor === selected.mockId) {
                        mockLogOpenFor = null;
                        stopMockLogPolling(selected.mockId);
                    } else {
                        mockLogOpenFor = selected.mockId;
                        startMockLogPolling(selected.mockId);
                    }
                    render();
                }
            })
        ));
        if (logOpen) {
            if (!logSt.entries.length) {
                logCard.appendChild(el('p', {
                    className: 'bowire-sources-hint',
                    textContent: 'No requests yet. Fire one against ' + url + ' and it shows up here.'
                }));
            } else {
                var ul = el('ul', { className: 'bowire-mocks-log-list', style: 'margin:8px 0 0;padding:0;list-style:none' });
                logSt.entries.slice(0, 20).forEach(function (e) {
                    var li = el('li', { style: 'padding:6px 0;border-top:1px solid var(--bowire-border-subtle);font-size:12px;font-family:var(--bowire-font-mono)' });
                    li.appendChild(el('span', {
                        textContent: '[' + (e.timestamp || '?') + '] ' + (e.method || 'REQ') + ' ' + (e.path || '/') + ' → ' + (e.status || '?')
                    }));
                    // #170 audit trail — surface the injected fault inline
                    // so an operator sees WHY a request was slow / failed /
                    // truncated without cross-referencing the fault rules.
                    if (e.fault) {
                        li.appendChild(el('span', {
                            className: 'bowire-mocks-log-fault',
                            title: 'Injected fault',
                            textContent: ' ⚡ ' + e.fault
                        }));
                    }
                    ul.appendChild(li);
                });
                logCard.appendChild(ul);
            }
        }
        wrap.appendChild(logCard);

        // #561 — schema-mock refinement editors (per-field overrides +
        // per-method conditional rules). Only for schema-started mocks
        // (recordingId empty); they apply to a REST schema mock's stubs.
        if (!selected.recordingId) {
            wrap.appendChild(renderOverridesCard(selected));
            wrap.appendChild(renderRulesCard(selected));
            // The auth gate is pipeline-level, so it also covers GraphQL/gRPC
            // schema mocks (unlike the stub-based override/rule cards).
            wrap.appendChild(renderAuthCard(selected));
        }
        wrap.appendChild(renderFaultCard(selected, url));
        pane.appendChild(wrap);
        return pane;
    }

    // ---------- #561 schema-mock refinement editors ----------
    // Two cards on the schema-mock detail pane: per-field response overrides
    // and per-method conditional-response rules. Both edit one mock-config
    // artifact; Apply persists it (PUT /config) AND applies it live to the
    // running mock (POST /config/apply), reusing the mock matcher (a rule
    // becomes a higher-priority match stub — no restart).

    var mockConfigState_ = {};
    var mockRowSeq_ = 0;

    function mockConfigState(mockId) {
        return mockConfigState_[mockId] || (mockConfigState_[mockId] = {
            config: { configFormatVersion: 1, fieldOverrides: [], conditionalRules: [] },
            overridesOpen: false, rulesOpen: false, dirty: false, error: '', loaded: false
        });
    }

    function mockConfigQs() {
        var wsId = (typeof activeWorkspaceId !== 'undefined' && activeWorkspaceId) ? activeWorkspaceId : '';
        return wsId ? ('?workspaceId=' + encodeURIComponent(wsId)) : '';
    }

    // Stable per-row id so edit handlers re-resolve their target at event time.
    // morphdom reuses row DOM nodes positionally on re-render; a closure over the
    // array element would mutate a stale / removed object after add/remove (the
    // documented morphdom stale-handler pitfall), so every handler looks the row
    // up by _rid instead.
    function ridOf(item) { if (!item._rid) item._rid = 'row' + (++mockRowSeq_); return item._rid; }
    function findRow(list, rid) { return list.find(function (x) { return x._rid === rid; }); }

    function normalizeMockConfig(data) {
        data = data || {};
        var overrides = Array.isArray(data.fieldOverrides) ? data.fieldOverrides : [];
        var rules = Array.isArray(data.conditionalRules) ? data.conditionalRules : [];
        // Give each row a stable id + a raw-text edit buffer for its JSON value
        // (so a string like "12345" isn't silently retyped to a number).
        overrides.forEach(function (o) { ridOf(o); o._valueText = jsonDisplay(o.value); });
        rules.forEach(function (r) { ridOf(r); r.when = r.when || {}; r._responseText = jsonDisplay(r.response); });
        return {
            configFormatVersion: data.configFormatVersion || 1,
            source: data.source,
            fieldOverrides: overrides,
            conditionalRules: rules,
            auth: data.auth
        };
    }

    function loadMockConfig(mockId) {
        var st = mockConfigState(mockId);
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/config' + mockConfigQs())
            .then(function (r) { return r.ok ? r.json() : {}; })
            .then(function (data) { st.config = normalizeMockConfig(data); st.loaded = true; st.dirty = false; return st; })
            .catch(function () { st.loaded = true; return st; });
    }

    // #563 — credential-free list of the workspace's captured auth recordings,
    // for the auth-card picker. Degrades to an empty list on any error.
    function loadAuthRecordings() {
        return fetch(config.prefix + '/api/auth-recordings' + mockConfigQs())
            .then(function (r) { return r.ok ? r.json() : { recordings: [] }; })
            .then(function (d) { return Array.isArray(d.recordings) ? d.recordings : []; })
            .catch(function () { return []; });
    }

    // #563 — create/update a recording (parity with the CLI + MCP capture). The
    // credential is written to the workspace store; only the id is kept in the
    // mock config that references it.
    function saveAuthRecording(rec) {
        return fetch(config.prefix + '/api/auth-recordings/' + encodeURIComponent(rec.id) + mockConfigQs(), {
            method: 'PUT', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ id: rec.id, name: rec.name, scheme: rec.scheme, header: rec.header, credential: rec.credential })
        }).then(function (r) {
            if (!r.ok) return r.json().catch(function () { return { error: 'Save failed (' + r.status + ')' }; })
                .then(function (e) { throw new Error(e.error || 'Save failed'); });
            return r.json();
        });
    }

    function deleteAuthRecording(id) {
        return fetch(config.prefix + '/api/auth-recordings/' + encodeURIComponent(id) + mockConfigQs(), { method: 'DELETE' })
            .then(function (r) { return r.ok ? r.json() : { deleted: false }; });
    }

    // #563 — flow capture: Bowire RUNS the auth-flow definition (outbound HTTP)
    // and stores the captured token. Parity with `--flow` and the MCP tool.
    function captureAuthRecordingFromFlow(id, name, flowJson) {
        var qs = mockConfigQs();
        var url = config.prefix + '/api/auth-recordings/' + encodeURIComponent(id) + '/capture' + qs;
        if (name) url += (qs ? '&' : '?') + 'name=' + encodeURIComponent(name);
        return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: flowJson })
            .then(function (r) {
                if (!r.ok) return r.json().catch(function () { return { error: 'Capture failed (' + r.status + ')' }; })
                    .then(function (e) { throw new Error(e.error || 'Capture failed'); });
                return r.json();
            });
    }

    // Build the clean config to persist/apply: parse the raw-text buffers back
    // to JSON and drop the client-only fields (_rid / _valueText / _responseText).
    function serializeMockConfig(st) {
        return JSON.stringify({
            configFormatVersion: st.config.configFormatVersion || 1,
            source: st.config.source,
            auth: st.config.auth,
            fieldOverrides: st.config.fieldOverrides.map(function (o) {
                return { service: o.service, method: o.method, jsonPath: o.jsonPath, value: parseJsonLoose(o._valueText) };
            }),
            conditionalRules: st.config.conditionalRules.map(function (r) {
                var w = r.when || {};
                var when = { jsonPath: w.jsonPath };
                if (w.matches != null) when.matches = w.matches;
                else if (w.contains != null) when.contains = w.contains;
                else when.equals = w.equals != null ? w.equals : '';
                return { service: r.service, method: r.method, when: when, response: parseJsonLoose(r._responseText) };
            })
        });
    }

    function applyMockConfig(mockId) {
        var st = mockConfigState(mockId);
        var body = serializeMockConfig(st);
        // Persist to the workspace store, then apply live to the running mock.
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/config' + mockConfigQs(), {
            method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: body
        }).then(function (r) {
            if (!r.ok) return r.json().catch(function () { return { error: 'Save failed (' + r.status + ')' }; })
                .then(function (e) { throw new Error(e.error || 'Save failed'); });
            // Carry the workspace so #563 auth-recording resolution scopes to
            // this mock's own workspace instead of scanning arbitrarily.
            return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/config/apply' + mockConfigQs(), {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body
            });
        }).then(function (r) {
            if (r && !r.ok) return r.json().catch(function () { return { error: 'Apply failed (' + r.status + ')' }; })
                .then(function (e) { throw new Error(e.error || 'Apply failed'); });
            st.dirty = false; st.error = '';
            if (typeof toast === 'function') toast('Mock configuration applied', 'success');
            render();
        }).catch(function (err) { st.error = err.message || String(err); render(); });
    }

    // The value/response text buffer holds exactly what the operator typed;
    // parse to JSON on apply, or fall back to the raw string when it isn't valid
    // JSON (so a bare `shipped` becomes the JSON string "shipped").
    function parseJsonLoose(s) {
        if (s === '' || s == null) return undefined;
        try { return JSON.parse(s); } catch (e) { return s; }
    }
    function jsonDisplay(v) {
        if (v === undefined || v === null) return '';
        return (typeof v === 'string') ? v : JSON.stringify(v);
    }

    function cfgInput(value, placeholder, onInput) {
        var input = el('input', { className: 'bowire-flow-field-input', type: 'text', placeholder: placeholder || '', value: value == null ? '' : value });
        input.oninput = function () { onInput(input.value); };
        return input;
    }

    function cfgRemoveBtn(onClick) {
        var b = el('button', { className: 'bowire-empty-card-action bowire-recording-action-danger', textContent: 'Remove' });
        b.onclick = onClick;
        return b;
    }

    function cfgActions(st, mockId, onAdd, addLabel) {
        var actions = el('div', { style: 'display:flex;gap:8px;margin-top:10px;align-items:center' });
        var addBtn = el('button', { className: 'bowire-empty-card-action', textContent: '+ Add ' + addLabel });
        addBtn.onclick = onAdd;
        actions.appendChild(addBtn);
        // Always clickable — an in-place field edit sets dirty WITHOUT a render,
        // so a disabled-when-clean button would strand the edit.
        var applyBtn = el('button', { className: 'bowire-empty-card-action bowire-empty-card-action-primary', textContent: 'Apply' });
        applyBtn.onclick = function () { applyMockConfig(mockId); };
        actions.appendChild(applyBtn);
        if (st.dirty) actions.appendChild(el('span', { className: 'bowire-sources-hint', textContent: 'unsaved changes' }));
        return actions;
    }

    function cfgCardHead(title, count, unit, isOpen, onToggle) {
        return el('div', { className: 'bowire-sources-section', style: 'display:flex;align-items:center;gap:8px' },
            el('span', { textContent: title }),
            el('span', { className: 'bowire-home-section-count', textContent: count + ' ' + unit + (count === 1 ? '' : 's') }),
            (function () { var b = el('button', { className: 'bowire-empty-card-action', textContent: isOpen ? 'Hide' : 'Edit' }); b.onclick = onToggle; return b; })()
        );
    }

    // The editors apply to a REST (OpenAPI) schema mock's stubs; GraphQL/gRPC
    // schema mocks serve via a live handler that bypasses the stub middleware,
    // so an override/rule would silently no-op. The kind is known once the
    // config (with source.kind, seeded at start) has loaded.
    function isRestConfig(st) {
        var k = st.config.source && st.config.source.kind;
        return !k || k === 'openapi';
    }
    function restOnlyNotice() {
        return el('p', { className: 'bowire-sources-hint',
            textContent: 'Response overrides and conditional rules apply to REST (OpenAPI) schema mocks. This mock uses a different schema kind.' });
    }
    function markDirty(st) { st.dirty = true; }

    function renderOverridesCard(selected) {
        var st = mockConfigState(selected.mockId);
        var card = el('div', { className: 'bowire-mocks-log-card', style: 'margin-top:16px' });
        card.appendChild(cfgCardHead('Response overrides', st.config.fieldOverrides.length, 'field', st.overridesOpen, function () {
            st.overridesOpen = !st.overridesOpen;
            if (st.overridesOpen && !st.loaded) loadMockConfig(selected.mockId).then(render);
            render();
        }));
        if (!st.overridesOpen) return card;
        if (!st.loaded) { card.appendChild(el('p', { className: 'bowire-sources-hint', textContent: 'Loading…' })); return card; }
        if (!isRestConfig(st)) { card.appendChild(restOnlyNotice()); return card; }
        if (st.error) card.appendChild(el('p', { className: 'bowire-sources-hint', style: 'color:var(--bowire-danger)', textContent: st.error }));
        card.appendChild(el('p', { className: 'bowire-sources-hint',
            textContent: 'Override individual response field values by (service, method) and JSON path. Applies live to the REST schema mock.' }));
        if (!st.config.fieldOverrides.length) {
            card.appendChild(el('p', { className: 'bowire-sources-hint', textContent: 'No overrides — the mock serves the schema-generated response.' }));
        } else {
            st.config.fieldOverrides.forEach(function (ov) { card.appendChild(renderOverrideRow(st, ridOf(ov))); });
        }
        card.appendChild(cfgActions(st, selected.mockId, function () {
            var ov = { service: '*', method: '*', jsonPath: '$.field', _valueText: '' }; ridOf(ov);
            st.config.fieldOverrides.push(ov); markDirty(st); render();
        }, 'field override'));
        return card;
    }

    function renderOverrideRow(st, rid) {
        var ov = findRow(st.config.fieldOverrides, rid);
        var row = el('div', { className: 'bowire-mocks-fault-rule', style: 'display:flex;flex-wrap:wrap;gap:6px;align-items:center;padding:6px 0;border-top:1px solid var(--bowire-border-subtle)' });
        row.appendChild(cfgInput(ov.service, 'service (* = any)', function (v) { var o = findRow(st.config.fieldOverrides, rid); if (o) { o.service = v; markDirty(st); } }));
        row.appendChild(cfgInput(ov.method, 'method (* = any)', function (v) { var o = findRow(st.config.fieldOverrides, rid); if (o) { o.method = v; markDirty(st); } }));
        row.appendChild(cfgInput(ov.jsonPath, '$.path', function (v) { var o = findRow(st.config.fieldOverrides, rid); if (o) { o.jsonPath = v; markDirty(st); } }));
        row.appendChild(cfgInput(ov._valueText, 'value (JSON or text)', function (v) { var o = findRow(st.config.fieldOverrides, rid); if (o) { o._valueText = v; markDirty(st); } }));
        row.appendChild(cfgRemoveBtn(function () { st.config.fieldOverrides = st.config.fieldOverrides.filter(function (x) { return x._rid !== rid; }); markDirty(st); render(); }));
        return row;
    }

    function renderRulesCard(selected) {
        var st = mockConfigState(selected.mockId);
        var card = el('div', { className: 'bowire-mocks-log-card', style: 'margin-top:16px' });
        card.appendChild(cfgCardHead('Conditional rules', st.config.conditionalRules.length, 'rule', st.rulesOpen, function () {
            st.rulesOpen = !st.rulesOpen;
            if (st.rulesOpen && !st.loaded) loadMockConfig(selected.mockId).then(render);
            render();
        }));
        if (!st.rulesOpen) return card;
        if (!st.loaded) { card.appendChild(el('p', { className: 'bowire-sources-hint', textContent: 'Loading…' })); return card; }
        if (!isRestConfig(st)) { card.appendChild(restOnlyNotice()); return card; }
        if (st.error) card.appendChild(el('p', { className: 'bowire-sources-hint', style: 'color:var(--bowire-danger)', textContent: st.error }));
        card.appendChild(el('p', { className: 'bowire-sources-hint',
            textContent: 'When a request to (service, method) matches a body predicate, serve a response variant instead of the default. Distinct from fault injection — a real response, chosen by a higher-priority match.' }));
        if (!st.config.conditionalRules.length) {
            card.appendChild(el('p', { className: 'bowire-sources-hint', textContent: 'No conditional rules — every request gets the default (overridden) response.' }));
        } else {
            st.config.conditionalRules.forEach(function (rule) { card.appendChild(renderRuleRow(st, ridOf(rule))); });
        }
        card.appendChild(cfgActions(st, selected.mockId, function () {
            var rule = { service: '*', method: '*', when: { jsonPath: '$.field', equals: '' }, _responseText: '' }; ridOf(rule);
            st.config.conditionalRules.push(rule); markDirty(st); render();
        }, 'rule'));
        return card;
    }

    var RULE_OPS = [
        { value: 'equals', label: 'equals' },
        { value: 'contains', label: 'contains' },
        { value: 'matches', label: 'matches (regex)' }
    ];
    function currentRuleOp(when) { when = when || {}; return when.matches != null ? 'matches' : (when.contains != null ? 'contains' : 'equals'); }

    function renderRuleRow(st, rid) {
        var rule = findRow(st.config.conditionalRules, rid);
        rule.when = rule.when || {};
        var op = currentRuleOp(rule.when);
        var row = el('div', { className: 'bowire-mocks-fault-rule', style: 'display:flex;flex-wrap:wrap;gap:6px;align-items:center;padding:6px 0;border-top:1px solid var(--bowire-border-subtle)' });
        row.appendChild(cfgInput(rule.service, 'service (* = any)', function (v) { var r = findRow(st.config.conditionalRules, rid); if (r) { r.service = v; markDirty(st); } }));
        row.appendChild(cfgInput(rule.method, 'method (* = any)', function (v) { var r = findRow(st.config.conditionalRules, rid); if (r) { r.method = v; markDirty(st); } }));
        row.appendChild(el('span', { className: 'bowire-mocks-fault-field-label', textContent: 'when' }));
        row.appendChild(cfgInput(rule.when.jsonPath, '$.path', function (v) { var r = findRow(st.config.conditionalRules, rid); if (r) { (r.when = r.when || {}).jsonPath = v; markDirty(st); } }));
        row.appendChild(faultSelect(op, RULE_OPS, function (newOp) {
            var r = findRow(st.config.conditionalRules, rid); if (!r) return;
            var w = r.when || (r.when = {});
            var val = w.matches != null ? w.matches : (w.contains != null ? w.contains : (w.equals != null ? w.equals : ''));
            delete w.equals; delete w.contains; delete w.matches;
            w[newOp] = val;
            markDirty(st); render();
        }));
        // Re-read the CURRENT op live so an op switch (which re-keys `when`) can't
        // strand this handler on a deleted predicate key.
        row.appendChild(cfgInput(rule.when[op], 'value', function (v) {
            var r = findRow(st.config.conditionalRules, rid); if (!r) return;
            var w = r.when || (r.when = {});
            w[currentRuleOp(w)] = v; markDirty(st);
        }));
        row.appendChild(el('span', { className: 'bowire-mocks-fault-field-label', textContent: 'serve' }));
        row.appendChild(cfgInput(rule._responseText, 'response (JSON)', function (v) { var r = findRow(st.config.conditionalRules, rid); if (r) { r._responseText = v; markDirty(st); } }));
        row.appendChild(cfgRemoveBtn(function () { st.config.conditionalRules = st.config.conditionalRules.filter(function (x) { return x._rid !== rid; }); markDirty(st); render(); }));
        return row;
    }

    // ---------- #562 require-auth toggle ----------
    var AUTH_SCHEMES = [
        { value: 'bearer', label: 'Bearer token' },
        { value: 'apikey', label: 'API key' },
        { value: 'basic', label: 'Basic' }
    ];

    function renderAuthCard(selected) {
        var st = mockConfigState(selected.mockId);
        var auth = st.config.auth || (st.config.auth = {});
        var card = el('div', { className: 'bowire-mocks-log-card', style: 'margin-top:16px' });
        var toggleBtn = el('button', { className: 'bowire-empty-card-action', textContent: st.authOpen ? 'Hide' : 'Edit' });
        toggleBtn.onclick = function () {
            st.authOpen = !st.authOpen;
            if (st.authOpen && !st.loaded) loadMockConfig(selected.mockId).then(render);
            render();
        };
        card.appendChild(el('div', { className: 'bowire-sources-section', style: 'display:flex;align-items:center;gap:8px' },
            el('span', { textContent: 'Authentication' }),
            el('span', { className: 'bowire-home-section-count', textContent: auth.required ? 'required' : 'open' }),
            toggleBtn));
        if (!st.authOpen) return card;
        if (!st.loaded) { card.appendChild(el('p', { className: 'bowire-sources-hint', textContent: 'Loading…' })); return card; }
        if (st.error) card.appendChild(el('p', { className: 'bowire-sources-hint', style: 'color:var(--bowire-danger)', textContent: st.error }));

        var reqLabel = el('label', { style: 'display:flex;align-items:center;gap:6px;margin:4px 0' });
        var reqCheck = el('input', { type: 'checkbox' });
        reqCheck.checked = !!auth.required;
        reqCheck.onchange = function () { st.config.auth.required = reqCheck.checked; markDirty(st); render(); };
        reqLabel.appendChild(reqCheck);
        reqLabel.appendChild(el('span', { textContent: 'Require authentication — 401 before replay without a valid credential' }));
        card.appendChild(reqLabel);

        if (auth.required) {
            // #563 — pick a captured auth recording (credential resolved
            // server-side at apply-time) or supply an inline credential. Lazy-
            // load the workspace's recordings once; null marks the fetch in-flight.
            if (st.authRecordings === undefined) {
                st.authRecordings = null;
                loadAuthRecordings().then(function (list) { st.authRecordings = list; render(); });
            }
            var recs = Array.isArray(st.authRecordings) ? st.authRecordings : [];
            var recOpts = [{ value: '', label: 'Inline credential' }];
            var listed = false;
            recs.forEach(function (r) {
                recOpts.push({ value: r.id, label: r.name || r.id });
                if (r.id === auth.authRecordingId) listed = true;
            });
            // Keep a selected-but-not-yet-loaded id visible while the list loads.
            if (auth.authRecordingId && !listed) recOpts.push({ value: auth.authRecordingId, label: auth.authRecordingId });

            var recRow = el('div', { style: 'display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin:4px 0' });
            recRow.appendChild(el('span', { className: 'bowire-sources-hint', textContent: 'Credential source' }));
            recRow.appendChild(faultSelect(auth.authRecordingId || '', recOpts, function (v) {
                st.config.auth.authRecordingId = v || null;
                // Selecting a recording clears any stale inline credential so the
                // token is never persisted alongside the recording reference.
                if (v) st.config.auth.credential = '';
                markDirty(st); render();
            }));
            // #563 — create/remove recordings inline (CLI/UI/MCP parity: same store).
            var newBtn = el('button', { className: 'bowire-empty-card-action', textContent: st.newRecOpen ? 'Cancel' : '+ New recording' });
            newBtn.onclick = function () {
                st.newRecOpen = !st.newRecOpen;
                if (st.newRecOpen && !st.newRec) st.newRec = { id: '', name: '', scheme: 'bearer', header: '', credential: '' };
                render();
            };
            recRow.appendChild(newBtn);
            if (auth.authRecordingId) {
                recRow.appendChild(cfgRemoveBtn(function () {
                    var target = auth.authRecordingId;
                    deleteAuthRecording(target).then(function () {
                        st.config.auth.authRecordingId = null;
                        st.authRecordings = undefined; // force a reload of the picker
                        markDirty(st); render();
                    });
                }));
            }
            card.appendChild(recRow);

            if (st.newRecOpen) {
                var nr = st.newRec || (st.newRec = { id: '', name: '', scheme: 'bearer', header: '', credential: '', flow: '', mode: 'static' });
                var onCaptured = function () {
                    st.config.auth.authRecordingId = nr.id;
                    st.config.auth.credential = '';
                    st.newRec = null; st.newRecOpen = false; st.error = null;
                    st.authRecordings = undefined; // force reload so the new id appears
                    markDirty(st); render();
                };
                var form = el('div', { style: 'display:flex;flex-direction:column;gap:6px;margin:4px 0 8px 0' });
                var topRow = el('div', { style: 'display:flex;flex-wrap:wrap;gap:6px;align-items:center' });
                topRow.appendChild(cfgInput(nr.id, 'id (referenced by authRecordingId)', function (v) { nr.id = v; }));
                topRow.appendChild(cfgInput(nr.name, 'name (optional)', function (v) { nr.name = v; }));
                topRow.appendChild(faultSelect(nr.scheme || 'bearer', AUTH_SCHEMES, function (v) { nr.scheme = v; }));
                topRow.appendChild(cfgInput(nr.header, 'header (default Authorization)', function (v) { nr.header = v; }));
                topRow.appendChild(faultSelect(nr.mode || 'static',
                    [{ value: 'static', label: 'Static credential' }, { value: 'flow', label: 'From auth flow' }],
                    function (v) { nr.mode = v; render(); }));
                form.appendChild(topRow);

                if ((nr.mode || 'static') === 'flow') {
                    var ta = el('textarea', { className: 'bowire-flow-field-input',
                        placeholder: 'auth-flow definition JSON — Bowire runs it (outbound HTTP) and stores the captured token',
                        style: 'min-height:80px;font-family:monospace' });
                    ta.value = nr.flow || '';
                    ta.oninput = function () { nr.flow = ta.value; };
                    form.appendChild(ta);
                    var flowBtn = el('button', { className: 'bowire-empty-card-action bowire-empty-card-action-primary', textContent: 'Run flow & capture' });
                    flowBtn.onclick = function () {
                        if (!nr.id || !nr.flow) { st.error = 'A flow capture needs an id and the flow JSON.'; render(); return; }
                        captureAuthRecordingFromFlow(nr.id, nr.name, nr.flow).then(onCaptured)
                            .catch(function (e) { st.error = e.message || 'Capture failed'; render(); });
                    };
                    form.appendChild(flowBtn);
                } else {
                    var credRow = el('div', { style: 'display:flex;flex-wrap:wrap;gap:6px;align-items:center' });
                    credRow.appendChild(cfgInput(nr.credential, 'credential (stored locally)', function (v) { nr.credential = v; }));
                    var saveRecBtn = el('button', { className: 'bowire-empty-card-action bowire-empty-card-action-primary', textContent: 'Save recording' });
                    saveRecBtn.onclick = function () {
                        if (!nr.id || !nr.credential) { st.error = 'A recording needs an id and a credential.'; render(); return; }
                        saveAuthRecording(nr).then(onCaptured).catch(function (e) { st.error = e.message || 'Save failed'; render(); });
                    };
                    credRow.appendChild(saveRecBtn);
                    form.appendChild(credRow);
                }
                card.appendChild(form);
            }

            var usingRecording = !!auth.authRecordingId;
            card.appendChild(el('p', { className: 'bowire-sources-hint',
                textContent: usingRecording
                    ? 'The credential is resolved from the selected recording at apply-time; the scheme/header below apply unless the recording overrides them.'
                    : 'Bearer/Basic read the Authorization header; an API key reads the named header. Leave the credential blank to accept any credential of the scheme.' }));
            var row = el('div', { style: 'display:flex;flex-wrap:wrap;gap:6px;align-items:center' });
            row.appendChild(faultSelect(auth.scheme || 'bearer', AUTH_SCHEMES, function (v) { st.config.auth.scheme = v; markDirty(st); render(); }));
            row.appendChild(cfgInput(auth.header, 'header (default Authorization)', function (v) { st.config.auth.header = v; markDirty(st); }));
            if (!usingRecording) {
                row.appendChild(cfgInput(auth.credential, 'expected credential (blank = any)', function (v) { st.config.auth.credential = v; markDirty(st); }));
            }
            card.appendChild(row);
        }

        var actions = el('div', { style: 'display:flex;gap:8px;margin-top:10px;align-items:center' });
        var applyBtn = el('button', { className: 'bowire-empty-card-action bowire-empty-card-action-primary', textContent: 'Apply' });
        applyBtn.onclick = function () { applyMockConfig(selected.mockId); };
        actions.appendChild(applyBtn);
        if (st.dirty) actions.appendChild(el('span', { className: 'bowire-sources-hint', textContent: 'unsaved changes' }));
        card.appendChild(actions);
        return card;
    }

    // ---------- #170 fault-injection editor ----------

    // Human descriptions of the fault kinds, kept in sync with the C#
    // FaultKind enum (kebab-case-lower on the wire).
    var FAULT_KINDS = [
        { value: 'latency-only',     label: 'Latency only' },
        { value: 'error',            label: 'Error (short-circuit)' },
        { value: 'partial-response', label: 'Partial response' },
        { value: 'connection-drop',  label: 'Connection drop' }
    ];
    var FAULT_DISTS = [
        { value: 'fixed',       label: 'Fixed' },
        { value: 'uniform',     label: 'Uniform range' },
        { value: 'normal',      label: 'Normal (mean/stddev)' },
        { value: 'exponential', label: 'Exponential (mean)' }
    ];

    function faultState(mockId) {
        return mockFaultState[mockId] || (mockFaultState[mockId] = { rules: [], open: false, dirty: false, error: '', loaded: false });
    }

    function loadFaults(mockId) {
        var st = faultState(mockId);
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/faults')
            .then(function (r) { return r.ok ? r.json() : { rules: [] }; })
            .then(function (data) {
                st.rules = (data && Array.isArray(data.rules)) ? data.rules : [];
                st.loaded = true;
                st.dirty = false;
                return st;
            })
            .catch(function () { st.loaded = true; return st; });
    }

    function saveFaults(mockId) {
        var st = faultState(mockId);
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/faults', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ rules: st.rules })
        }).then(function (r) {
            if (!r.ok) {
                return r.json().catch(function () { return { error: 'Save failed (' + r.status + ')' }; })
                    .then(function (err) { throw new Error(err.error || 'Save failed'); });
            }
            st.dirty = false;
            st.error = '';
            toast('Fault rules applied to running mock', 'success');
            render();
        }).catch(function (err) {
            st.error = err.message || String(err);
            render();
        });
    }

    // Fault-rule editor card on the mock detail pane. Mirrors the
    // Live-request-log card's shape (section header + toggle + body) so
    // the two sit consistently. Rules apply to the RUNNING mock via
    // PUT — no restart. Empty rule list = injection off (default).
    function renderFaultCard(selected, url) {
        var st = faultState(selected.mockId);
        var card = el('div', { className: 'bowire-mocks-log-card', style: 'margin-top:16px' });

        var activeCount = st.rules.filter(function (r) { return r.enabled !== false; }).length;
        card.appendChild(el('div', { className: 'bowire-sources-section', style: 'display:flex;align-items:center;gap:8px' },
            el('span', { textContent: 'Fault injection' }),
            el('span', { className: 'bowire-home-section-count', textContent: activeCount + ' rule' + (activeCount === 1 ? '' : 's') }),
            el('button', {
                className: 'bowire-empty-card-action',
                textContent: st.open ? 'Hide' : 'Edit',
                onClick: function () {
                    st.open = !st.open;
                    if (st.open && !st.loaded) { loadFaults(selected.mockId).then(render); }
                    render();
                }
            })
        ));

        if (!st.open) return card;

        if (!st.loaded) {
            card.appendChild(el('p', { className: 'bowire-sources-hint', textContent: 'Loading rules…' }));
            return card;
        }

        if (st.error) {
            card.appendChild(el('p', { className: 'bowire-sources-hint', style: 'color:var(--bowire-danger)', textContent: st.error }));
        }

        if (!st.rules.length) {
            card.appendChild(el('p', { className: 'bowire-sources-hint',
                textContent: 'No fault rules — the mock replays faithfully. Add a rule to inject latency, errors, or truncated responses.' }));
        } else {
            st.rules.forEach(function (rule, idx) {
                card.appendChild(renderFaultRule(selected.mockId, rule, idx));
            });
        }

        var actions = el('div', { style: 'display:flex;gap:8px;margin-top:10px;align-items:center' });
        actions.appendChild(el('button', {
            className: 'bowire-empty-card-action',
            textContent: '+ Add rule',
            onClick: function () {
                st.rules.push({ method: '*', kind: 'error', rate: 1.0, errorStatusCode: 503, partialBytes: 1024 });
                st.dirty = true;
                render();
            }
        }));
        var applyBtn = el('button', {
            className: 'bowire-empty-card-action bowire-empty-card-action-primary',
            textContent: st.dirty ? 'Apply changes' : 'Applied',
            onClick: function () { if (st.dirty) saveFaults(selected.mockId); }
        });
        if (!st.dirty) applyBtn.disabled = true;
        actions.appendChild(applyBtn);
        card.appendChild(actions);
        return card;
    }

    function faultSelect(value, options, onChange) {
        var sel = el('select', { className: 'bowire-flow-field-input', onChange: function (e) { onChange(e.target.value); } });
        options.forEach(function (o) {
            var opt = el('option', { value: o.value, textContent: o.label });
            if (o.value === value) opt.selected = true;
            sel.appendChild(opt);
        });
        return sel;
    }

    function faultNumber(value, placeholder, onChange) {
        return el('input', {
            type: 'number', className: 'bowire-flow-field-input', style: 'max-width:90px',
            value: (value == null ? '' : String(value)), placeholder: placeholder || '',
            onInput: function (e) { onChange(parseFloat(e.target.value)); }
        });
    }

    function renderFaultRule(mockId, rule, idx) {
        var st = faultState(mockId);
        var row = el('div', { className: 'bowire-mocks-fault-rule' });
        var mark = function () { st.dirty = true; };

        var head = el('div', { className: 'bowire-mocks-fault-head' });
        var enabledBox = el('input', {
            type: 'checkbox', checked: rule.enabled !== false ? 'checked' : undefined, title: 'Enabled',
            onChange: function (e) { rule.enabled = e.target.checked; mark(); render(); }
        });
        head.appendChild(enabledBox);
        head.appendChild(el('input', {
            type: 'text', className: 'bowire-flow-field-input', style: 'flex:1', placeholder: 'Service/Method glob (e.g. UserService/*)',
            value: rule.method || '*', spellcheck: 'false',
            onInput: function (e) { rule.method = e.target.value; mark(); }
        }));
        head.appendChild(faultSelect(rule.kind || 'error', FAULT_KINDS, function (v) { rule.kind = v; mark(); render(); }));
        head.appendChild(el('button', {
            className: 'bowire-flow-card-action-btn', title: 'Remove rule', innerHTML: svgIcon('trash'),
            onClick: function () { st.rules.splice(idx, 1); mark(); render(); }
        }));
        row.appendChild(head);

        var opts = el('div', { className: 'bowire-mocks-fault-opts' });
        // Kind-specific knobs.
        if (rule.kind === 'error') {
            opts.appendChild(labelled('Status', faultNumber(rule.errorStatusCode || 503, '503', function (v) { rule.errorStatusCode = v | 0; mark(); })));
        }
        if (rule.kind === 'partial-response' || rule.kind === 'connection-drop') {
            opts.appendChild(labelled('Bytes', faultNumber(rule.partialBytes != null ? rule.partialBytes : 1024, '1024', function (v) { rule.partialBytes = v | 0; mark(); })));
        }
        if (rule.kind !== 'latency-only') {
            opts.appendChild(labelled('Rate', faultNumber(rule.rate != null ? rule.rate : 1.0, '1.0', function (v) { rule.rate = v; mark(); })));
        }
        // Latency shape — available on every kind.
        var lat = rule.latency || null;
        var latDist = lat ? lat.distribution : 'none';
        opts.appendChild(labelled('Latency', faultSelect(latDist, [{ value: 'none', label: 'None' }].concat(FAULT_DISTS), function (v) {
            if (v === 'none') { rule.latency = null; }
            else if (v === 'fixed') { rule.latency = { distribution: 'fixed', valueMs: 200 }; }
            else if (v === 'uniform') { rule.latency = { distribution: 'uniform', minMs: 100, maxMs: 500 }; }
            else if (v === 'normal') { rule.latency = { distribution: 'normal', meanMs: 200, stdDevMs: 50 }; }
            else { rule.latency = { distribution: 'exponential', meanMs: 200 }; }
            mark(); render();
        })));
        if (lat && lat.distribution === 'fixed') {
            opts.appendChild(labelled('ms', faultNumber(lat.valueMs || 0, '200', function (v) { lat.valueMs = v | 0; mark(); })));
        } else if (lat && lat.distribution === 'uniform') {
            opts.appendChild(labelled('min', faultNumber(lat.minMs || 0, '100', function (v) { lat.minMs = v | 0; mark(); })));
            opts.appendChild(labelled('max', faultNumber(lat.maxMs || 0, '500', function (v) { lat.maxMs = v | 0; mark(); })));
        } else if (lat && lat.distribution === 'normal') {
            opts.appendChild(labelled('mean', faultNumber(lat.meanMs || 0, '200', function (v) { lat.meanMs = v | 0; mark(); })));
            opts.appendChild(labelled('stddev', faultNumber(lat.stdDevMs || 0, '50', function (v) { lat.stdDevMs = v | 0; mark(); })));
        } else if (lat && lat.distribution === 'exponential') {
            opts.appendChild(labelled('mean', faultNumber(lat.meanMs || 0, '200', function (v) { lat.meanMs = v | 0; mark(); })));
        }
        row.appendChild(opts);
        return row;
    }

    function labelled(label, control) {
        return el('label', { className: 'bowire-mocks-fault-field' },
            el('span', { className: 'bowire-mocks-fault-field-label', textContent: label }),
            control);
    }

    // Expose for recording.js + render-sidebar.js + intercept-view.js
    // to call from their own UI hooks. renderRailMain is the v2.2 entry
    // point the Intercept rail's Mock-servers sub-tab uses.
    window.__bowireMocks = {
        list: function () { return mocksList; },
        load: loadMocks,
        startFromRecording: startMockFromRecording,
        startFromSchema: startMockFromSchema,
        stop: stopMock,
        open: openMocksManager,
        renderRailMain: renderMocksRailMain
    };
