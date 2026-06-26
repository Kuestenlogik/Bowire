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

    // ---- Sidebar list ----

    function renderInterceptedListInto(container) {
        if (interceptedConnectionState === 'idle') {
            // Auto-connect on first paint. Same-origin fetch — no
            // external URL to validate first.
            bowireInterceptedConnect();
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
                        : null
                ));
            })(interceptedFlows[i]);
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
            }, el('span', { textContent: 'Send to recording' }))
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
