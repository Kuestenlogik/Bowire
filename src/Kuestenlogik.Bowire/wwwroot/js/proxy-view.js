    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // Tier-3 Stage C — Workbench "Proxy" view.
    //
    // Reads captured flows from a sidecar `bowire proxy` API (default
    // http://127.0.0.1:8889/api/proxy) — the same endpoints the CLI's
    // ProxyCommand mounts. Keeps the listing live via the SSE stream so
    // a request hitting the proxy shows up immediately, without polling.
    //
    // Per-flow actions:
    //   - Send to recording: POST /api/proxy/flows/{id}/recording, push
    //     the resulting BowireRecording into the user's local recording
    //     store (the existing "save as recording" path).
    //   - Fuzz this field: jumps into the Tier-2 right-click semantics
    //     fuzz menu by hydrating the workbench's freeform request from
    //     the flow.
    //   - Clear: DELETE /api/proxy/flows.
    //
    // The Proxy view is intentionally optional — when the proxy isn't
    // running the connect-attempt fails fast and the view shows a
    // start-it-with-this-command hint instead of an empty list.
    // ------------------------------------------------------------------

    const BOWIRE_PROXY_API_KEY = 'bowire_proxy_api_url';
    const BOWIRE_PROXY_DEFAULT_API_URL = 'http://127.0.0.1:8889';

    let proxyFlows = [];                 // newest-first snapshot
    let proxyFlowSelectedId = null;
    let proxyConnectionState = 'idle';   // 'idle' | 'connecting' | 'connected' | 'error'
    let proxyConnectionError = null;
    let proxyEventSource = null;
    let proxyApiUrl = (function () {
        try { return localStorage.getItem(BOWIRE_PROXY_API_KEY) || BOWIRE_PROXY_DEFAULT_API_URL; }
        catch { return BOWIRE_PROXY_DEFAULT_API_URL; }
    })();

    function bowireProxySetApiUrl(url) {
        proxyApiUrl = (url || '').trim() || BOWIRE_PROXY_DEFAULT_API_URL;
        try { localStorage.setItem(BOWIRE_PROXY_API_KEY, proxyApiUrl); } catch {}
        bowireProxyDisconnect();
    }

    function bowireProxyDisconnect() {
        if (proxyEventSource) {
            try { proxyEventSource.close(); } catch {}
            proxyEventSource = null;
        }
        proxyConnectionState = 'idle';
    }

    /**
     * Fetch the newest-first snapshot from the proxy + subscribe to the
     * live SSE feed. Idempotent — calling again while already connected
     * is a no-op. Render is called whenever the state shifts so the
     * sidebar badge + main pane both stay current.
     */
    async function bowireProxyConnect() {
        if (proxyConnectionState === 'connecting' || proxyConnectionState === 'connected') return;
        proxyConnectionState = 'connecting';
        proxyConnectionError = null;
        render();

        try {
            const resp = await fetch(proxyApiUrl + '/api/proxy/flows');
            if (!resp.ok) throw new Error('HTTP ' + resp.status);
            const body = await resp.json();
            proxyFlows = Array.isArray(body.flows) ? body.flows : [];
            proxyConnectionState = 'connected';
        } catch (ex) {
            proxyConnectionState = 'error';
            proxyConnectionError = ex && ex.message ? ex.message : String(ex);
            render();
            return;
        }

        try {
            proxyEventSource = new EventSource(proxyApiUrl + '/api/proxy/stream');
            proxyEventSource.addEventListener('flow', function (e) {
                try {
                    const summary = JSON.parse(e.data);
                    proxyFlows = [summary].concat(proxyFlows.filter(function (f) { return f.id !== summary.id; }));
                    render();
                } catch (err) { /* drop malformed frame */ }
            });
            proxyEventSource.onerror = function () {
                proxyConnectionState = 'error';
                proxyConnectionError = 'live stream disconnected';
                render();
            };
        } catch (ex) {
            // EventSource construction failures land here on browsers
            // that block cross-origin SSE — flag the connection but
            // keep the snapshot we already loaded usable.
            proxyConnectionState = 'error';
            proxyConnectionError = 'live stream unavailable: ' + (ex && ex.message ? ex.message : String(ex));
        }
        render();
    }

    /**
     * Fetch the full flow detail (headers + bodies) on demand. The
     * snapshot endpoint deliberately keeps the listing tiny; the detail
     * pane pulls the bigger payload only for the selected flow.
     */
    async function bowireProxyFetchFlowDetail(id) {
        try {
            const resp = await fetch(proxyApiUrl + '/api/proxy/flows/' + encodeURIComponent(id));
            if (!resp.ok) return null;
            return await resp.json();
        } catch { return null; }
    }

    let proxyFlowDetailCache = {};   // id → detail JSON
    async function bowireProxyEnsureDetail(id) {
        if (proxyFlowDetailCache[id]) return proxyFlowDetailCache[id];
        const detail = await bowireProxyFetchFlowDetail(id);
        if (detail) proxyFlowDetailCache[id] = detail;
        render();
        return detail;
    }

    async function bowireProxyClearFlows() {
        try {
            const resp = await fetch(proxyApiUrl + '/api/proxy/flows', { method: 'DELETE' });
            if (resp.ok) {
                proxyFlows = [];
                proxyFlowDetailCache = {};
                proxyFlowSelectedId = null;
                render();
            }
        } catch { /* surface via toast in future iteration */ }
    }

    /**
     * Project a captured flow into a BowireRecording (server-side) and
     * import the result into the local recording store. The Tier-3
     * differentiator vs. Burp: captured traffic flows directly into the
     * workbench's recording / fuzz / scan pipeline.
     */
    async function bowireProxySendToRecording(id) {
        try {
            const resp = await fetch(proxyApiUrl + '/api/proxy/flows/' + encodeURIComponent(id) + '/recording',
                { method: 'POST' });
            if (!resp.ok) return;
            const recording = await resp.json();
            // Append to the local recording store + persist. The
            // recording-list view in the sidebar picks it up on the
            // next render. Same path the in-app recorder uses.
            if (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) {
                recordingsList.push(recording);
                if (typeof persistRecordings === 'function') persistRecordings();
            }
            render();
        } catch { /* toast pending */ }
    }

    // ---- Sidebar list ----

    function renderProxyListInto(container) {
        if (proxyConnectionState === 'idle') {
            // Auto-connect on first paint of the proxy view.
            bowireProxyConnect();
        }

        if (proxyConnectionState === 'connecting') {
            container.appendChild(el('div', { className: 'bowire-loading', style: 'padding:24px' },
                el('div', { className: 'bowire-spinner' }),
                el('span', { className: 'bowire-loading-text', textContent: 'Connecting to proxy…' })
            ));
            return;
        }

        if (proxyConnectionState === 'error') {
            container.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding:24px' },
                el('div', { className: 'bowire-empty-title', textContent: 'Proxy not reachable' }),
                el('div', { className: 'bowire-empty-desc', textContent: proxyConnectionError || 'Could not connect to ' + proxyApiUrl }),
                el('div', { className: 'bowire-empty-desc', style: 'margin-top:8px;font-family:monospace;font-size:11px;opacity:0.7',
                    textContent: 'Start the proxy with:  bowire proxy' }),
                el('button', {
                    id: 'bowire-proxy-retry-btn',
                    className: 'bowire-recording-action-btn',
                    style: 'margin-top:12px',
                    onClick: function () { proxyConnectionState = 'idle'; render(); }
                }, el('span', { textContent: 'Retry' }))
            ));
            return;
        }

        if (proxyFlows.length === 0) {
            container.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding:32px' },
                el('div', { className: 'bowire-empty-title', textContent: 'Waiting for traffic' }),
                el('div', { className: 'bowire-empty-desc', textContent: 'Point your browser / client at ' + proxyApiUrl.replace(/:\d+$/, ':8888') + ' to start capturing.' })
            ));
            return;
        }

        for (let i = 0; i < proxyFlows.length; i++) {
            (function (flow) {
                const isActive = proxyFlowSelectedId === flow.id;
                const statusClass = flow.responseStatus >= 500 ? 'bowire-proxy-status-err'
                                  : flow.responseStatus >= 400 ? 'bowire-proxy-status-warn'
                                  : flow.responseStatus >= 200 ? 'bowire-proxy-status-ok'
                                  : 'bowire-proxy-status-pending';
                container.appendChild(el('div', {
                    id: 'bowire-proxy-flow-' + flow.id,
                    className: 'bowire-proxy-list-item' + (isActive ? ' selected' : ''),
                    onClick: function () {
                        proxyFlowSelectedId = flow.id;
                        bowireProxyEnsureDetail(flow.id);
                        render();
                    }
                },
                    el('span', { className: 'bowire-proxy-list-method', textContent: flow.method || 'GET' }),
                    el('span', { className: 'bowire-proxy-list-status ' + statusClass,
                        textContent: flow.responseStatus > 0 ? String(flow.responseStatus) : (flow.error ? 'ERR' : '…') }),
                    el('span', { className: 'bowire-proxy-list-url', textContent: flow.url || '', title: flow.url || '' }),
                    flow.scheme === 'https'
                        ? el('span', { className: 'bowire-proxy-list-tls', title: 'MITM-intercepted HTTPS', textContent: '🔓' })
                        : null
                ));
            })(proxyFlows[i]);
        }

        container.appendChild(el('div', { style: 'padding:8px; display:flex; gap:6px; flex-direction:column' },
            el('button', {
                id: 'bowire-proxy-clear-btn',
                className: 'bowire-recording-action-btn',
                onClick: function () { bowireProxyClearFlows(); }
            }, el('span', { textContent: 'Clear all' })),
            el('div', { style: 'display:flex; gap:6px; align-items:center; margin-top:4px; font-size:11px; color:var(--bowire-text-secondary)' },
                el('span', { textContent: 'API:' }),
                el('input', {
                    id: 'bowire-proxy-api-url',
                    type: 'text',
                    className: 'bowire-input',
                    style: 'flex:1; font-size:11px; padding:2px 4px; font-family:monospace',
                    value: proxyApiUrl,
                    spellcheck: 'false',
                    onChange: function (e) { bowireProxySetApiUrl(e.target.value); render(); }
                })
            )
        ));
    }

    // ---- Main pane ----

    function renderProxyMainPane() {
        const pane = el('div', { className: 'bowire-env-editor-main' });

        if (proxyConnectionState === 'error') {
            pane.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding:48px' },
                el('div', { className: 'bowire-empty-title', textContent: 'No proxy connection' }),
                el('div', { className: 'bowire-empty-desc', textContent: 'Run `bowire proxy` in a terminal, then click Retry in the sidebar.' })
            ));
            return pane;
        }

        if (!proxyFlowSelectedId) {
            pane.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding:48px' },
                el('div', { className: 'bowire-empty-title', textContent: 'Pick a captured flow' }),
                el('div', { className: 'bowire-empty-desc', textContent: 'Select a row in the sidebar to inspect the request / response, or to send it into the recording pipeline.' })
            ));
            return pane;
        }

        const summary = proxyFlows.find(function (f) { return f.id === proxyFlowSelectedId; });
        const detail = proxyFlowDetailCache[proxyFlowSelectedId];
        if (!summary && !detail) {
            pane.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding:48px',
                textContent: 'Flow no longer available (evicted from ring buffer).' }));
            return pane;
        }

        const flow = detail || summary;

        // Header bar
        const header = el('div', { className: 'bowire-env-editor-header' },
            el('h2', { className: 'bowire-env-editor-title', textContent: (flow.method || 'GET') + ' ' + (flow.url || '') }),
            el('span', { style: 'flex:1' }),
            el('button', {
                id: 'bowire-proxy-send-rec-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Convert this captured flow into a Bowire recording you can replay, fuzz, or include in a test collection.',
                onClick: function () { bowireProxySendToRecording(proxyFlowSelectedId); }
            }, el('span', { textContent: 'Send to recording' }))
        );
        pane.appendChild(header);

        // Metadata row
        const meta = el('div', { className: 'bowire-proxy-detail-meta' });
        meta.appendChild(metaCell('Status', String(flow.responseStatus || (flow.error ? 'ERR' : '…'))));
        meta.appendChild(metaCell('Scheme', flow.scheme || 'http'));
        meta.appendChild(metaCell('Latency', (flow.latencyMs || 0) + ' ms'));
        meta.appendChild(metaCell('Captured', flow.capturedAt ? new Date(flow.capturedAt).toLocaleTimeString() : ''));
        if (flow.error) meta.appendChild(metaCell('Error', flow.error));
        pane.appendChild(meta);

        if (!detail) {
            pane.appendChild(el('div', { style: 'padding:24px; opacity:0.7', textContent: 'Loading full payload…' }));
            return pane;
        }

        // Request + response side by side
        pane.appendChild(renderHttpExchange(detail));
        return pane;
    }

    function metaCell(label, value) {
        return el('div', { className: 'bowire-proxy-detail-meta-cell' },
            el('div', { className: 'bowire-proxy-detail-meta-label', textContent: label }),
            el('div', { className: 'bowire-proxy-detail-meta-value', textContent: value })
        );
    }

    function renderHttpExchange(detail) {
        const wrap = el('div', { className: 'bowire-proxy-detail-exchange' });
        wrap.appendChild(renderExchangeSide('Request', detail.requestHeaders, detail.requestBody, detail.requestBodyBase64));
        wrap.appendChild(renderExchangeSide('Response', detail.responseHeaders, detail.responseBody, detail.responseBodyBase64));
        return wrap;
    }

    function renderExchangeSide(title, headers, bodyText, bodyBase64) {
        const side = el('div', { className: 'bowire-proxy-detail-side' });
        side.appendChild(el('div', { className: 'bowire-proxy-detail-side-title', textContent: title }));

        const hdrList = el('div', { className: 'bowire-proxy-detail-headers' });
        const list = Array.isArray(headers) ? headers : [];
        if (list.length === 0) {
            hdrList.appendChild(el('div', { className: 'bowire-proxy-detail-hdr-empty', textContent: '(no headers)' }));
        } else {
            for (let i = 0; i < list.length; i++) {
                const pair = list[i];
                hdrList.appendChild(el('div', { className: 'bowire-proxy-detail-hdr' },
                    el('span', { className: 'bowire-proxy-detail-hdr-key', textContent: pair.key + ':' }),
                    el('span', { className: 'bowire-proxy-detail-hdr-value', textContent: pair.value })
                ));
            }
        }
        side.appendChild(hdrList);

        const bodyBox = el('div', { className: 'bowire-proxy-detail-body' });
        if (bodyText) {
            bodyBox.appendChild(el('pre', { className: 'bowire-proxy-detail-body-pre', textContent: bodyText }));
        } else if (bodyBase64) {
            bodyBox.appendChild(el('div', { className: 'bowire-proxy-detail-body-binary' },
                el('div', { textContent: '(binary payload — base64)' }),
                el('pre', { className: 'bowire-proxy-detail-body-pre', style: 'opacity:0.7',
                    textContent: bodyBase64.length > 2048 ? bodyBase64.slice(0, 2048) + '…' : bodyBase64 })
            ));
        } else {
            bodyBox.appendChild(el('div', { className: 'bowire-proxy-detail-body-empty', textContent: '(empty body)' }));
        }
        side.appendChild(bodyBox);
        return side;
    }
