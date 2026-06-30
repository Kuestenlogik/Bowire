    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // #153 — Workbench "Intercepted" rail.
    //
    // Twin of proxy-view.js. Reads flows from the in-process interceptor
    // — every request flowing through the host where Bowire is embedded
    // and `app.UseBowireInterceptor()` is on. Same JSON shape as the
    // proxy rail (method / url / scheme / responseStatus / latencyMs /
    // requestHeaders / requestBody / …) so the detail-pane renderer
    // doesn't have to branch on the source.
    //
    // Talks to /api/intercepted/* on the same origin as the workbench
    // — no external proxy URL, no cert-trust dance. Live updates flow
    // over the /api/intercepted/stream SSE channel.
    //
    // The endpoints always mount (they live inside MapBowire); when
    // the host never called UseBowireInterceptor() the store stays
    // empty and the rail surfaces a "no traffic yet" empty card with
    // a hint about wiring the middleware on.
    // ------------------------------------------------------------------

    let interceptedFlows = [];                 // newest-first snapshot
    let interceptedFlowSelectedId = null;
    let interceptedConnectionState = 'idle';   // 'idle' | 'connecting' | 'connected' | 'error'
    let interceptedConnectionError = null;
    let interceptedEventSource = null;
    let interceptedFlowDetailCache = {};       // id → detail JSON

    // Phase D (#308) — in-process mock-injection rules.
    // The Intercepted rail's "Mocks" sub-tab is the CRUD surface.
    let interceptedMockRules = [];             // [{ id, name, pathPattern, method, ... }]
    let interceptedMocksEnabled = true;        // master toggle (BowireInterceptorOptions.MocksEnabled)
    let interceptedSubView = 'flows';          // 'flows' | 'mocks'
    let interceptedMocksLoaded = false;
    let interceptedMockSelectedId = null;
    let interceptedMockEditing = null;          // draft rule object or null

    function bowireInterceptedApiBase() {
        // Always the same-origin workbench mount. config.prefix is the
        // bowire base path ("/bowire" by default in embedded mode,
        // empty in standalone). Endpoints live under that prefix.
        var prefix = (typeof config === 'object' && config && typeof config.prefix === 'string')
            ? config.prefix : '';
        return prefix.replace(/\/$/, '');
    }

    function bowireInterceptedDisconnect() {
        if (interceptedEventSource) {
            try { interceptedEventSource.close(); } catch {}
            interceptedEventSource = null;
        }
        interceptedConnectionState = 'idle';
    }

    async function bowireInterceptedConnect() {
        if (interceptedConnectionState === 'connecting' || interceptedConnectionState === 'connected') return;
        var base = bowireInterceptedApiBase();
        interceptedConnectionState = 'connecting';
        interceptedConnectionError = null;
        render();

        try {
            const resp = await fetch(base + '/api/intercepted/flows');
            if (!resp.ok) throw new Error('HTTP ' + resp.status);
            const body = await resp.json();
            interceptedFlows = Array.isArray(body.flows) ? body.flows : [];
            interceptedConnectionState = 'connected';
        } catch (ex) {
            interceptedConnectionState = 'error';
            interceptedConnectionError = ex && ex.message ? ex.message : String(ex);
            render();
            return;
        }

        try {
            interceptedEventSource = new EventSource(base + '/api/intercepted/stream');
            interceptedEventSource.addEventListener('flow', function (e) {
                try {
                    const summary = JSON.parse(e.data);
                    interceptedFlows = [summary].concat(
                        interceptedFlows.filter(function (f) { return f.id !== summary.id; }));
                    render();
                } catch (err) { /* drop malformed frame */ }
            });
            interceptedEventSource.onerror = function () {
                interceptedConnectionState = 'error';
                interceptedConnectionError = 'live stream disconnected';
                render();
            };
        } catch (ex) {
            interceptedConnectionState = 'error';
            interceptedConnectionError = 'live stream unavailable: ' + (ex && ex.message ? ex.message : String(ex));
        }
        render();
    }

    async function bowireInterceptedFetchFlowDetail(id) {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(base + '/api/intercepted/flows/' + encodeURIComponent(id));
            if (!resp.ok) return null;
            return await resp.json();
        } catch { return null; }
    }

    async function bowireInterceptedEnsureDetail(id) {
        if (interceptedFlowDetailCache[id]) return interceptedFlowDetailCache[id];
        const detail = await bowireInterceptedFetchFlowDetail(id);
        if (detail) interceptedFlowDetailCache[id] = detail;
        render();
        return detail;
    }

    async function bowireInterceptedClearFlows() {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(base + '/api/intercepted/flows', { method: 'DELETE' });
            if (resp.ok) {
                interceptedFlows = [];
                interceptedFlowDetailCache = {};
                interceptedFlowSelectedId = null;
                render();
            }
        } catch { /* surface via toast in a later polish pass */ }
    }

    async function bowireInterceptedSendToRecording(id) {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(
                base + '/api/intercepted/flows/' + encodeURIComponent(id) + '/recording',
                { method: 'POST' });
            if (!resp.ok) return;
            const recording = await resp.json();
            if (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) {
                recordingsList.push(recording);
                if (typeof persistRecordings === 'function') persistRecordings();
            }
            render();
        } catch { /* toast pending */ }
    }

    // ---- Phase D — mock-injection rules ----

    async function bowireInterceptedLoadMocks() {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(base + '/api/intercepted/mocks');
            if (!resp.ok) return;
            const body = await resp.json();
            interceptedMockRules = Array.isArray(body.rules) ? body.rules : [];
            interceptedMocksEnabled = body.enabled !== false;
            interceptedMocksLoaded = true;
            render();
        } catch { /* leave previous state in place; toast pending */ }
    }

    async function bowireInterceptedToggleMocks(enabled) {
        var base = bowireInterceptedApiBase();
        // Optimistic flip — UI feels live, server is the source of truth.
        interceptedMocksEnabled = !!enabled;
        render();
        try {
            const resp = await fetch(base + '/api/intercepted/mocks/enabled', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled: !!enabled }),
            });
            if (!resp.ok) return;
            const body = await resp.json();
            interceptedMocksEnabled = body.enabled !== false;
            render();
        } catch { /* leave optimistic flip in place */ }
    }

    async function bowireInterceptedSaveMockRule(rule) {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(base + '/api/intercepted/mocks', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(rule),
            });
            if (!resp.ok) return null;
            const saved = await resp.json();
            interceptedMockRules = [saved].concat(
                interceptedMockRules.filter(function (r) { return r.id !== saved.id; }));
            render();
            return saved;
        } catch { return null; }
    }

    async function bowireInterceptedDeleteMockRule(id) {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(base + '/api/intercepted/mocks/' + encodeURIComponent(id), { method: 'DELETE' });
            if (!resp.ok) return;
            interceptedMockRules = interceptedMockRules.filter(function (r) { return r.id !== id; });
            render();
        } catch { /* toast pending */ }
    }

    async function bowireInterceptedSeedMockFromFlow(flowId) {
        var base = bowireInterceptedApiBase();
        try {
            const resp = await fetch(
                base + '/api/intercepted/flows/' + encodeURIComponent(flowId) + '/mock',
                { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
            if (!resp.ok) return null;
            const rule = await resp.json();
            interceptedMockRules = [rule].concat(
                interceptedMockRules.filter(function (r) { return r.id !== rule.id; }));
            // Auto-flip to the Mocks tab so the operator sees the rule
            // they just seeded.
            interceptedSubView = 'mocks';
            if (typeof toast === 'function') toast('Mock rule seeded from flow', 'success');
            render();
            return rule;
        } catch { return null; }
    }

    // Compose with the existing Mocks rail (#36 / #56): turn a recording
    // step into an interceptor mock rule. The workbench-side
    // recordingsList holds the canonical recording shape — we lift the
    // first step's HTTP method + path + response into the rule.
    async function bowireInterceptedSeedMockFromRecording(recordingId) {
        if (typeof recordingsList === 'undefined' || !Array.isArray(recordingsList)) return null;
        const rec = recordingsList.find(function (r) { return r && r.id === recordingId; });
        if (!rec || !Array.isArray(rec.steps) || rec.steps.length === 0) return null;
        const step = rec.steps[0];
        const rule = {
            name: 'from-recording-' + (rec.name || rec.id || '').slice(0, 40),
            pathPattern: step.httpPath ? step.httpPath.split('?')[0] : '*',
            method: step.httpVerb || step.method || '*',
            responseStatus: 200,
            responseBody: step.response || '',
            enabled: true,
        };
        return await bowireInterceptedSaveMockRule(rule);
    }

    // ---- Sidebar list ----

    function renderInterceptedListInto(container) {
        if (interceptedConnectionState === 'idle') {
            // Auto-connect on first paint. Same-origin fetch — no
            // external URL to validate first.
            bowireInterceptedConnect();
        }
        // Lazy-load mock rules once — the toggle + rule count live in
        // the toolbar so we need the snapshot even while the user is on
        // the flows sub-tab.
        if (!interceptedMocksLoaded) {
            bowireInterceptedLoadMocks();
        }

        if (typeof renderSidebarToolbar === 'function') {
            container.appendChild(renderSidebarToolbar({
                title: 'Intercepted',
                actions: [
                    {
                        icon: 'replay',
                        title: 'Reconnect to the interceptor',
                        onClick: function () {
                            interceptedConnectionState = 'idle';
                            render();
                        }
                    }
                ],
                overflow: [
                    {
                        label: 'Clear all flows',
                        danger: true,
                        onClick: function () { bowireInterceptedClearFlows(); }
                    }
                ]
            }));
        }

        // Sub-tab strip: Flows | Mocks. Same recessed-strip pattern the
        // Workbench's other rails use for sub-tabs.
        var tabStrip = el('div', { id: 'bowire-intercepted-subtabs', className: 'bowire-rail-subtabs' },
            el('button', {
                className: 'bowire-rail-subtab' + (interceptedSubView === 'flows' ? ' active' : ''),
                onClick: function () { interceptedSubView = 'flows'; render(); }
            }, el('span', { textContent: 'Flows' }),
                el('span', { className: 'bowire-rail-subtab-meta', textContent: interceptedFlows.length ? String(interceptedFlows.length) : '' })),
            el('button', {
                className: 'bowire-rail-subtab' + (interceptedSubView === 'mocks' ? ' active' : ''),
                onClick: function () { interceptedSubView = 'mocks'; render(); }
            }, el('span', { textContent: 'Mocks' }),
                el('span', { className: 'bowire-rail-subtab-meta', textContent: interceptedMockRules.length ? String(interceptedMockRules.length) : '' }))
        );
        container.appendChild(tabStrip);
        // Overflow popover — same affordance as Traffic's twin strip.
        // Look up the LIVE element after morphdom commits.
        requestAnimationFrame(function () {
            var live = document.getElementById('bowire-intercepted-subtabs');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, {
                    tabSelector: '.bowire-rail-subtab',
                    label: 'More tabs'
                });
            }
        });

        if (interceptedSubView === 'mocks') {
            renderInterceptedMocksListInto(container);
            return;
        }

        if (interceptedConnectionState === 'connecting') {
            container.appendChild(el('div', { className: 'bowire-loading', style: 'padding:24px' },
                el('div', { className: 'bowire-spinner' }),
                el('span', { className: 'bowire-loading-text', textContent: 'Connecting to interceptor…' })
            ));
            return;
        }

        if (interceptedConnectionState === 'error') {
            container.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'Interceptor not reachable',
                body: (interceptedConnectionError || 'Failed to reach the interceptor endpoints.')
                    + ' This rail talks to the same host that serves the workbench — check the host is running.',
                actions: [{
                    label: 'Retry',
                    primary: true,
                    onClick: function () { interceptedConnectionState = 'idle'; render(); }
                }]
            }));
            return;
        }

        if (interceptedFlows.length === 0) {
            container.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'No traffic yet',
                body: 'Add app.UseBowireInterceptor() to this host\'s pipeline, then drive any request through it from any client. '
                    + 'Captured flows land here in real time.'
            }));
            return;
        }

        for (let i = 0; i < interceptedFlows.length; i++) {
            (function (flow) {
                const isActive = interceptedFlowSelectedId === flow.id;
                const statusClass = flow.responseStatus >= 500 ? 'bowire-proxy-status-err'
                                  : flow.responseStatus >= 400 ? 'bowire-proxy-status-warn'
                                  : flow.responseStatus >= 200 ? 'bowire-proxy-status-ok'
                                  : 'bowire-proxy-status-pending';
                container.appendChild(el('div', {
                    id: 'bowire-intercepted-flow-' + flow.id,
                    className: 'bowire-proxy-list-item' + (isActive ? ' selected' : ''),
                    onClick: function () {
                        interceptedFlowSelectedId = flow.id;
                        bowireInterceptedEnsureDetail(flow.id);
                        render();
                    }
                },
                    el('span', { className: 'bowire-proxy-list-method', textContent: flow.method || 'GET' }),
                    el('span', {
                        className: 'bowire-proxy-list-status ' + statusClass,
                        textContent: flow.responseStatus > 0 ? String(flow.responseStatus) : (flow.error ? 'ERR' : '…')
                    }),
                    el('span', {
                        className: 'bowire-proxy-list-url',
                        textContent: flow.path || flow.url || '',
                        title: flow.url || ''
                    }),
                    flow.streaming
                        ? el('span', { className: 'bowire-proxy-list-tls', title: 'Streaming response', textContent: '↻' })
                        : null,
                    flow.mocked
                        ? el('span', { className: 'bowire-proxy-list-tls', title: 'Served from mock rule', textContent: 'M' })
                        : null
                ));
            })(interceptedFlows[i]);
        }
    }

    // ---- Mocks sub-tab list ----

    function renderInterceptedMocksListInto(container) {
        // Master toggle row — flips BowireInterceptorOptions.MocksEnabled
        // on the server. Without this on, rules are kept but the matcher
        // is skipped (free per-request check beyond the null compare).
        var toggleRow = el('div', { className: 'bowire-rail-subtab-toggle' },
            el('label', { className: 'bowire-switch' },
                el('input', {
                    type: 'checkbox',
                    checked: interceptedMocksEnabled ? true : undefined,
                    onChange: function (e) { bowireInterceptedToggleMocks(e.target.checked); }
                }),
                el('span', { className: 'bowire-switch-track' })
            ),
            el('span', {
                className: 'bowire-rail-subtab-toggle-label',
                textContent: interceptedMocksEnabled ? 'Mock injection ON' : 'Mock injection OFF'
            })
        );
        container.appendChild(toggleRow);

        // New-rule affordance: opens a blank-rule editor in the main pane.
        container.appendChild(el('button', {
            className: 'bowire-rail-add-btn',
            onClick: function () {
                interceptedMockEditing = {
                    name: '',
                    pathPattern: '/api/*',
                    method: 'GET',
                    responseStatus: 200,
                    responseBody: '{}',
                    enabled: true,
                };
                render();
            }
        }, el('span', { textContent: '+ New mock rule' })));

        if (interceptedMockRules.length === 0) {
            container.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'No mock rules',
                // Cross-link to the Mock servers sub-tab so the
                // operator understands the orthogonal split: rules here
                // substitute single responses inside the proxy /
                // middleware pipeline; Mock servers spin up a standalone
                // host that replays a recording end-to-end. Same word
                // "mock" — different verb.
                body: 'Add a rule, or open a captured flow and click "Mock this route" to seed one from the response. Looking for a standalone mock server that replays a whole recording? See the Mock servers sub-tab.'
            }));
            return;
        }

        for (let i = 0; i < interceptedMockRules.length; i++) {
            (function (rule) {
                const isActive = interceptedMockSelectedId === rule.id;
                container.appendChild(el('div', {
                    id: 'bowire-intercepted-mock-' + rule.id,
                    className: 'bowire-proxy-list-item' + (isActive ? ' selected' : ''),
                    onClick: function () {
                        interceptedMockSelectedId = rule.id;
                        interceptedMockEditing = null;
                        render();
                    }
                },
                    el('span', { className: 'bowire-proxy-list-method', textContent: rule.method || '*' }),
                    el('span', {
                        className: 'bowire-proxy-list-status ' + (rule.enabled ? 'bowire-proxy-status-ok' : 'bowire-proxy-status-pending'),
                        textContent: String(rule.responseStatus || 200)
                    }),
                    el('span', {
                        className: 'bowire-proxy-list-url',
                        textContent: rule.pathPattern || '*',
                        title: rule.name || rule.pathPattern || ''
                    }),
                    !rule.enabled
                        ? el('span', { className: 'bowire-proxy-list-tls', title: 'Rule paused', textContent: '⏸' })
                        : null
                ));
            })(interceptedMockRules[i]);
        }
    }

    // ---- Main pane ----

    function renderInterceptedMainPane() {
        const pane = el('div', { className: 'bowire-env-editor-main' });

        if (interceptedConnectionState === 'error') {
            pane.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'No interceptor connection',
                body: 'Click Retry in the sidebar — this rail talks to the same host that serves the workbench.'
            }));
            return pane;
        }

        if (interceptedSubView === 'mocks') {
            return renderInterceptedMocksMainPane(pane);
        }

        if (!interceptedFlowSelectedId) {
            pane.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'Pick an intercepted flow',
                body: 'Select a row in the sidebar to inspect the request / response, or send it into the recording pipeline.'
            }));
            return pane;
        }

        const summary = interceptedFlows.find(function (f) { return f.id === interceptedFlowSelectedId; });
        const detail = interceptedFlowDetailCache[interceptedFlowSelectedId];
        if (!summary && !detail) {
            pane.appendChild(renderEmptyCard({
                icon: 'history',
                headline: 'Flow no longer available',
                body: 'The capture ring buffer evicted this entry. Pick a more recent flow from the sidebar.'
            }));
            return pane;
        }

        const flow = detail || summary;

        // Header bar
        const header = el('div', { className: 'bowire-env-editor-header' },
            el('h2', { className: 'bowire-env-editor-title', textContent: (flow.method || 'GET') + ' ' + (flow.path || flow.url || '') }),
            el('span', { style: 'flex:1' }),
            el('button', {
                id: 'bowire-intercepted-send-rec-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Convert this intercepted flow into a Bowire recording you can replay, fuzz, or include in a test collection.',
                onClick: function () { bowireInterceptedSendToRecording(interceptedFlowSelectedId); }
            }, el('span', { textContent: 'Send to recording' })),
            el('button', {
                id: 'bowire-intercepted-mock-this-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Seed a mock-injection rule from this flow. The interceptor will serve the captured response in place of the upstream endpoint until the rule is paused or removed.',
                onClick: function () { bowireInterceptedSeedMockFromFlow(interceptedFlowSelectedId); }
            }, el('span', { textContent: 'Mock this route' }))
        );
        pane.appendChild(header);

        // Metadata row — reuse the proxy detail metric cells for visual parity.
        const meta = el('div', { className: 'bowire-proxy-detail-meta' });
        function _interceptedMetaCell(label, value) {
            return el('div', { className: 'bowire-proxy-detail-meta-cell' },
                el('div', { className: 'bowire-proxy-detail-meta-label', textContent: label }),
                el('div', { className: 'bowire-proxy-detail-meta-value', textContent: value })
            );
        }
        meta.appendChild(_interceptedMetaCell('Status', String(flow.responseStatus || (flow.error ? 'ERR' : '…'))));
        meta.appendChild(_interceptedMetaCell('Scheme', flow.scheme || 'http'));
        meta.appendChild(_interceptedMetaCell('Latency', (flow.latencyMs || 0) + ' ms'));
        meta.appendChild(_interceptedMetaCell('Captured', flow.capturedAt ? new Date(flow.capturedAt).toLocaleTimeString() : ''));
        if (flow.streaming) meta.appendChild(_interceptedMetaCell('Mode', 'streaming'));
        if (flow.mocked) meta.appendChild(_interceptedMetaCell('Source', 'mock rule'));
        if (flow.error) meta.appendChild(_interceptedMetaCell('Error', flow.error));
        pane.appendChild(meta);

        if (!detail) {
            pane.appendChild(el('div', { style: 'padding:24px; opacity:0.7', textContent: 'Loading full payload…' }));
            return pane;
        }

        // Request + response side by side. Reuse the proxy exchange
        // renderer so the two rails look identical — the only
        // difference between them is the rail icon + the source of
        // truth (CLI proxy vs. in-process middleware).
        if (typeof renderHttpExchange === 'function') {
            pane.appendChild(renderHttpExchange(detail));
        }
        return pane;
    }

    // ---- Mocks sub-tab main pane ----

    function renderInterceptedMocksMainPane(pane) {
        // Editor takes precedence over selection: clicking "+ New mock
        // rule" puts a draft on the editor surface; clicking a rule in
        // the sidebar swaps to view-mode for that rule.
        var rule = interceptedMockEditing
            || interceptedMockRules.find(function (r) { return r.id === interceptedMockSelectedId; });

        if (!rule) {
            pane.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'Mock injection',
                body: 'Pick a rule on the left, click "+ New mock rule" to author one, or open a captured flow on the Flows tab and click "Mock this route".'
            }));
            return pane;
        }

        const isDraft = rule === interceptedMockEditing;

        // Header with name + save / delete actions.
        const header = el('div', { className: 'bowire-env-editor-header' },
            el('h2', { className: 'bowire-env-editor-title', textContent: isDraft ? 'New mock rule' : (rule.name || 'Mock rule') }),
            el('span', { style: 'flex:1' }),
            !isDraft ? el('button', {
                className: 'bowire-env-editor-action-btn',
                title: rule.enabled ? 'Pause this rule (keep the definition, skip the matcher)' : 'Resume this rule',
                onClick: function () {
                    bowireInterceptedSaveMockRule(Object.assign({}, rule, { enabled: !rule.enabled }));
                }
            }, el('span', { textContent: rule.enabled ? 'Pause' : 'Resume' })) : null,
            !isDraft ? el('button', {
                className: 'bowire-env-editor-action-btn',
                title: 'Delete this mock rule',
                onClick: function () { bowireInterceptedDeleteMockRule(rule.id); }
            }, el('span', { textContent: 'Delete' })) : null,
            isDraft ? el('button', {
                className: 'bowire-env-editor-action-btn',
                title: 'Save the new rule. Existing matching requests start mocking from the next call.',
                onClick: function () {
                    bowireInterceptedSaveMockRule(interceptedMockEditing).then(function (saved) {
                        if (saved) {
                            interceptedMockEditing = null;
                            interceptedMockSelectedId = saved.id;
                            render();
                        }
                    });
                }
            }, el('span', { textContent: 'Save' })) : null
        );
        pane.appendChild(header);

        // Field editor — names are explicit so the operator can see how
        // pattern + method shape the matcher.
        function _fieldRow(label, input) {
            return el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: label }),
                input
            );
        }

        var nameInput = el('input', {
            type: 'text',
            className: 'bowire-env-editor-field-input',
            value: rule.name || '',
            placeholder: 'Display name',
            onChange: function (e) {
                if (isDraft) { interceptedMockEditing.name = e.target.value; render(); }
                else { bowireInterceptedSaveMockRule(Object.assign({}, rule, { name: e.target.value })); }
            }
        });
        var pathInput = el('input', {
            type: 'text',
            className: 'bowire-env-editor-field-input',
            value: rule.pathPattern || '',
            placeholder: '/api/users/*',
            onChange: function (e) {
                if (isDraft) { interceptedMockEditing.pathPattern = e.target.value; render(); }
                else { bowireInterceptedSaveMockRule(Object.assign({}, rule, { pathPattern: e.target.value })); }
            }
        });
        var methodInput = el('input', {
            type: 'text',
            className: 'bowire-env-editor-field-input',
            value: rule.method || '*',
            placeholder: 'GET / POST / *',
            onChange: function (e) {
                if (isDraft) { interceptedMockEditing.method = e.target.value; render(); }
                else { bowireInterceptedSaveMockRule(Object.assign({}, rule, { method: e.target.value })); }
            }
        });
        var statusInput = el('input', {
            type: 'number',
            className: 'bowire-env-editor-field-input',
            value: String(rule.responseStatus || 200),
            onChange: function (e) {
                var n = parseInt(e.target.value, 10) || 200;
                if (isDraft) { interceptedMockEditing.responseStatus = n; render(); }
                else { bowireInterceptedSaveMockRule(Object.assign({}, rule, { responseStatus: n })); }
            }
        });
        var delayInput = el('input', {
            type: 'number',
            className: 'bowire-env-editor-field-input',
            value: String(rule.delayMs || 0),
            onChange: function (e) {
                var n = parseInt(e.target.value, 10) || 0;
                if (isDraft) { interceptedMockEditing.delayMs = n; render(); }
                else { bowireInterceptedSaveMockRule(Object.assign({}, rule, { delayMs: n })); }
            }
        });
        var bodyInput = el('textarea', {
            className: 'bowire-env-editor-field-input bowire-env-editor-field-body',
            rows: 12,
            value: rule.responseBody || '',
            placeholder: '{\n  "ok": true\n}',
            onChange: function (e) {
                if (isDraft) { interceptedMockEditing.responseBody = e.target.value; render(); }
                else { bowireInterceptedSaveMockRule(Object.assign({}, rule, { responseBody: e.target.value })); }
            }
        });

        pane.appendChild(_fieldRow('Name', nameInput));
        pane.appendChild(_fieldRow('Path pattern', pathInput));
        pane.appendChild(_fieldRow('Method', methodInput));
        pane.appendChild(_fieldRow('Status code', statusInput));
        pane.appendChild(_fieldRow('Delay (ms)', delayInput));
        pane.appendChild(_fieldRow('Response body', bodyInput));

        // Recordings → mock seeding affordance: compose with the
        // existing Mocks rail by listing live recordings as one-click
        // sources. Only shown for the New-rule draft so the operator
        // doesn't accidentally clobber an existing rule.
        if (isDraft && typeof recordingsList !== 'undefined' && Array.isArray(recordingsList) && recordingsList.length > 0) {
            var seedBlock = el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: 'Seed from recording' }),
                (function () {
                    var sel = el('select', { className: 'bowire-env-editor-field-input' },
                        el('option', { value: '', textContent: '— pick a recording —' })
                    );
                    recordingsList.forEach(function (rec) {
                        if (!rec || !rec.id) return;
                        sel.appendChild(el('option', { value: rec.id, textContent: rec.name || rec.id }));
                    });
                    sel.onchange = function (e) {
                        if (!e.target.value) return;
                        bowireInterceptedSeedMockFromRecording(e.target.value);
                    };
                    return sel;
                })()
            );
            pane.appendChild(seedBlock);
        }

        return pane;
    }
