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
    //
    // #299 — Embedded mode story. When the workbench is mounted inside
    // an ASP.NET host via MapBowire() there is no `bowire proxy`
    // sidecar to default to: the loopback URL would always fail and
    // the "start the proxy with `bowire proxy`" hint is meaningless
    // because the operator isn't running the CLI. Two adjustments:
    //   (1) Embedded + no workspace override ⇒ skip auto-connect and
    //       render a "CLI-only or external URL" empty state with a
    //       jump to docs/features/proxy.md.
    //   (2) Workspace settings expose an "External proxy endpoint"
    //       field that routes the rail at a remote `bowire proxy`
    //       running on another host. When set, embedded mode behaves
    //       like standalone — connect, stream, capture.
    // Standalone behaviour is unchanged: no override ⇒ loopback
    // default, error hint still says "start the proxy with `bowire
    // proxy` in a terminal".
    // ------------------------------------------------------------------

    const BOWIRE_PROXY_DEFAULT_API_URL = 'http://127.0.0.1:8889';

    let proxyFlows = [];                 // newest-first snapshot
    let proxyFlowSelectedId = null;
    let proxyConnectionState = 'idle';   // 'idle' | 'connecting' | 'connected' | 'error'
    let proxyConnectionError = null;
    let proxyEventSource = null;

    /**
     * Effective Proxy-API base URL. Resolution order:
     *   1. Per-workspace external endpoint (set in workspace settings).
     *   2. CLI loopback default (standalone mode only).
     *   3. Empty string in embedded mode with no override — signals
     *      "no place to talk to" to the rest of the view, which then
     *      renders the embedded-mode empty state instead of trying
     *      to fetch.
     */
    function bowireProxyEffectiveApiUrl() {
        var override = (typeof getWorkspaceProxyEndpoint === 'function')
            ? getWorkspaceProxyEndpoint() : '';
        if (override) return override;
        var embedded = (typeof uiMode !== 'undefined' && uiMode === 'embedded');
        if (embedded) return '';
        return BOWIRE_PROXY_DEFAULT_API_URL;
    }

    function bowireProxyIsConfigured() {
        return !!bowireProxyEffectiveApiUrl();
    }

    function bowireProxySetApiUrl(url) {
        if (typeof setWorkspaceProxyEndpoint === 'function') {
            setWorkspaceProxyEndpoint(null, (url || '').trim());
        }
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
        // #299 — Nothing to connect to (embedded host, no external
        // endpoint configured). Leave the state at 'idle' so the
        // sidebar renders the embedded empty state rather than
        // firing a doomed fetch against an empty base URL.
        var apiUrl = bowireProxyEffectiveApiUrl();
        if (!apiUrl) return;

        proxyConnectionState = 'connecting';
        proxyConnectionError = null;
        render();

        try {
            const resp = await fetch(apiUrl + '/api/proxy/flows');
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
            proxyEventSource = new EventSource(apiUrl + '/api/proxy/stream');
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
        var apiUrl = bowireProxyEffectiveApiUrl();
        if (!apiUrl) return null;
        try {
            const resp = await fetch(apiUrl + '/api/proxy/flows/' + encodeURIComponent(id));
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
        var apiUrl = bowireProxyEffectiveApiUrl();
        if (!apiUrl) return;
        try {
            const resp = await fetch(apiUrl + '/api/proxy/flows', { method: 'DELETE' });
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
        var apiUrl = bowireProxyEffectiveApiUrl();
        if (!apiUrl) return;
        try {
            const resp = await fetch(apiUrl + '/api/proxy/flows/' + encodeURIComponent(id) + '/recording',
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
        // #299 — Resolve the effective API URL up-front: every empty-
        // state / error branch below either prints it or decides
        // whether the rail can connect at all.
        var apiUrl = bowireProxyEffectiveApiUrl();
        var embedded = (typeof uiMode !== 'undefined' && uiMode === 'embedded');

        if (proxyConnectionState === 'idle' && apiUrl) {
            // Auto-connect on first paint of the proxy view. Skipped
            // when there's no place to talk to (embedded + no
            // external endpoint configured) — the empty-state branch
            // below handles that case.
            bowireProxyConnect();
        }

        // Unified sidebar toolbar — overflow holds Clear all and
        // the API-URL editor (the latter is settings, not action,
        // so it lives behind the ⋮).
        if (typeof renderSidebarToolbar === 'function') {
            container.appendChild(renderSidebarToolbar({
                title: 'Proxy',
                actions: [
                    {
                        icon: 'replay',
                        title: 'Reconnect to the proxy',
                        onClick: function () {
                            proxyConnectionState = 'idle';
                            render();
                        }
                    }
                ],
                overflow: [
                    {
                        label: 'Clear all flows',
                        danger: true,
                        onClick: function () { bowireProxyClearFlows(); }
                    },
                    { separator: true },
                    {
                        label: 'Edit API URL…',
                        onClick: function () {
                            bowirePrompt('Proxy API URL', {
                                title: 'Proxy connection',
                                defaultValue: apiUrl || '',
                                placeholder: 'http://localhost:8889',
                                confirmText: 'Save'
                            }).then(function (val) {
                                if (val === null || val === undefined) return;
                                bowireProxySetApiUrl(String(val).trim());
                                render();
                            });
                        }
                    }
                ]
            }));
        }

        // #299 — Embedded host with no external endpoint configured.
        // There's no in-host proxy listener (the standalone CLI's
        // `bowire proxy` isn't applicable here), so the rail surfaces
        // a doc-link + "set the URL" affordance instead of pointing
        // the operator at a command they can't run.
        if (embedded && !apiUrl) {
            container.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'Proxy runs outside this host',
                body: 'Proxy mode runs in the standalone Bowire CLI or against an external proxy URL. '
                    + 'This host (embedded MapBowire) doesn\'t expose a proxy listener.',
                actions: [
                    {
                        label: 'Set external proxy URL…',
                        primary: true,
                        onClick: function () {
                            bowirePrompt('External proxy endpoint', {
                                title: 'Proxy connection',
                                defaultValue: '',
                                placeholder: 'http://proxy.example.internal:8889',
                                confirmText: 'Save'
                            }).then(function (val) {
                                if (val === null || val === undefined) return;
                                bowireProxySetApiUrl(String(val).trim());
                                proxyConnectionState = 'idle';
                                render();
                            });
                        }
                    },
                    {
                        label: 'Read the docs',
                        onClick: function () {
                            if (typeof helpOpenDrawer === 'function') {
                                helpOpenDrawer('features/proxy');
                            }
                        }
                    }
                ]
            }));
            return;
        }

        if (proxyConnectionState === 'connecting') {
            container.appendChild(el('div', { className: 'bowire-loading', style: 'padding:24px' },
                el('div', { className: 'bowire-spinner' }),
                el('span', { className: 'bowire-loading-text', textContent: 'Connecting to proxy…' })
            ));
            return;
        }

        if (proxyConnectionState === 'error') {
            // Standalone: the CLI hint is the right next step.
            // Embedded with an external URL set: the operator can't
            // run `bowire proxy` here — point them at the URL field
            // instead.
            var errBody;
            if (embedded) {
                errBody = (proxyConnectionError || 'Could not connect to ' + apiUrl)
                    + ' — check the external proxy URL is running and reachable from this host.';
            } else {
                errBody = (proxyConnectionError || 'Could not connect to ' + apiUrl)
                    + ' — start the proxy with `bowire proxy` in a terminal, then retry.';
            }
            container.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'Proxy not reachable',
                body: errBody,
                actions: [{
                    label: 'Retry',
                    primary: true,
                    onClick: function () { proxyConnectionState = 'idle'; render(); }
                }]
            }));
            return;
        }

        if (proxyFlows.length === 0) {
            container.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'Waiting for traffic',
                body: 'Point your browser / client at ' + apiUrl.replace(/:\d+$/, ':8888') + ' to start capturing — captured flows land here in real time.'
            }));
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

        // Bottom Clear-all + API-URL row retired — both affordances
        // live in the sidebar toolbar (overflow ⋮) at the top now.
    }

    // ---- Main pane ----

    function renderProxyMainPane() {
        const pane = el('div', { className: 'bowire-env-editor-main' });
        var embedded = (typeof uiMode !== 'undefined' && uiMode === 'embedded');
        var apiUrl = bowireProxyEffectiveApiUrl();

        // #299 — Embedded + no external URL: mirror the sidebar's
        // "CLI-only or external URL" message so an operator who clicks
        // through to the main pane (e.g. from a deep-link) doesn't see
        // a stale or misleading hint.
        if (embedded && !apiUrl) {
            pane.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'Proxy runs outside this host',
                body: 'Proxy mode runs in the standalone Bowire CLI or against an external proxy URL. '
                    + 'Set an external proxy endpoint in Workspace Settings → General to wire this rail to a remote `bowire proxy`.'
            }));
            return pane;
        }

        if (proxyConnectionState === 'error') {
            // Embedded hosts can't run `bowire proxy` in-process — the
            // hint that worked for the standalone CLI is meaningless
            // here. Show a host-appropriate next step.
            var mainErrBody = embedded
                ? 'Check the external proxy URL in Workspace Settings, then click Retry in the sidebar.'
                : 'Run `bowire proxy` in a terminal, then click Retry in the sidebar.';
            pane.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'No proxy connection',
                body: mainErrBody
            }));
            return pane;
        }

        if (!proxyFlowSelectedId) {
            pane.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'Pick a captured flow',
                body: 'Select a row in the sidebar to inspect the request / response, or send it into the recording pipeline.'
            }));
            return pane;
        }

        const summary = proxyFlows.find(function (f) { return f.id === proxyFlowSelectedId; });
        const detail = proxyFlowDetailCache[proxyFlowSelectedId];
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
