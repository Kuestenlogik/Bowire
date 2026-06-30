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

        var selected = (mocksList || []).find(function (m) { return m.mockId === mockSelectedId; });
        if (!selected) {
            wrap.appendChild(renderEmptyCard({
                icon: 'mock',
                headline: hasAny ? 'Pick a mock server' : 'No mock servers running',
                body: hasAny
                    ? 'Pick a running mock from the sidebar to see its URL, live request log, and stop control.'
                    : 'Mock servers are standalone replay hosts spun up from a recording — switch to the Recordings rail and use "Run as mock" on any session. Looking for one-line response substitution inside the proxy / middleware pipeline? Use this rail’s Live overrides sub-tab.',
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
                    li.textContent = '[' + (e.timestamp || '?') + '] ' + (e.method || 'REQ') + ' ' + (e.path || '/') + ' → ' + (e.status || '?');
                    ul.appendChild(li);
                });
                logCard.appendChild(ul);
            }
        }
        wrap.appendChild(logCard);
        pane.appendChild(wrap);
        return pane;
    }

    // Expose for recording.js + render-sidebar.js + intercept-view.js
    // to call from their own UI hooks. renderRailMain is the v2.2 entry
    // point the Intercept rail's Mock-servers sub-tab uses.
    window.__bowireMocks = {
        list: function () { return mocksList; },
        load: loadMocks,
        startFromRecording: startMockFromRecording,
        stop: stopMock,
        open: openMocksManager,
        renderRailMain: renderMocksRailMain
    };
