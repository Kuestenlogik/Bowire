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

        wrap.appendChild(renderFaultCard(selected, url));
        pane.appendChild(wrap);
        return pane;
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
        stop: stopMock,
        open: openMocksManager,
        renderRailMain: renderMocksRailMain
    };
