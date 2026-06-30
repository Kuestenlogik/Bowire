    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // v2.2 rail-IA refactor — Workbench "Intercept" rail.
    //
    // Replaces the previous Mocks + Traffic rails (and the already-hidden
    // Intercepted + Proxy descriptors). One rail, four sub-tabs in a
    // locked order: Captured | Live overrides | Mock servers | Settings.
    //
    //   Captured       — passive observation of UseBowireInterceptor()
    //                    flows; was Traffic → "Flows".
    //   Live overrides — selective response substitution inside the
    //                    interceptor pipeline; was Traffic → "Mock Rules".
    //   Mock servers   — standalone mock-server-from-recording hosts;
    //                    was the entire Mocks rail. Rendered by the
    //                    Mock package's JS fragment when present;
    //                    degrades to "Mock package not loaded" when not.
    //   Settings       — interceptor config (was Traffic → "Settings");
    //                    adapts header + form to Standalone vs Embedded
    //                    via __BOWIRE_CONFIG__.embeddedMode.
    //
    // Architecture for the Mock-servers sub-tab: option C from the audit.
    // The Mock package still owns mocks.js + the per-mock log polling /
    // start-from-recording flow; this rail's renderer pokes the global
    // window.__bowireMocks shim that mocks.js installs. When the Mock
    // package isn't in the host's reference set, the global is absent
    // and the sub-tab renders an empty state pointing operators at the
    // package. Keeps the Mock package decoupled (still pure CLI
    // citizen) without introducing a new extension-point seam.
    // ------------------------------------------------------------------

    // 'captured' | 'live-overrides' | 'mock-servers' | 'settings'
    let interceptSubView = (function () {
        try {
            var stored = localStorage.getItem('bowire_intercept_sub_tab');
            if (stored === 'captured' || stored === 'live-overrides'
                || stored === 'mock-servers' || stored === 'settings') return stored;
        } catch { /* ignore */ }
        return 'captured';
    })();

    function setInterceptSubView(next) {
        interceptSubView = next;
        try { localStorage.setItem('bowire_intercept_sub_tab', next); } catch { /* ignore */ }
        render();
    }

    function bowireInterceptIsEmbedded() {
        return (typeof uiMode !== 'undefined' && uiMode === 'embedded');
    }

    // ---- Sidebar — four sub-tabs ----

    function renderInterceptListInto(container) {
        // Lazy-load flow snapshot + live-override rules through the
        // existing intercepted helpers. They auto-connect to
        // /api/intercepted/* on the same workbench origin.
        if (typeof bowireInterceptedConnect === 'function'
            && typeof interceptedConnectionState !== 'undefined'
            && interceptedConnectionState === 'idle') {
            bowireInterceptedConnect();
        }
        if (typeof bowireInterceptedLoadMocks === 'function'
            && typeof interceptedMocksLoaded !== 'undefined'
            && !interceptedMocksLoaded) {
            bowireInterceptedLoadMocks();
        }
        // Mock-servers list — same lazy pull pattern. Only fires when
        // the Mock package's shim is installed.
        if (typeof window !== 'undefined'
            && window.__bowireMocks
            && typeof window.__bowireMocks.load === 'function') {
            try { window.__bowireMocks.load(); } catch { /* ignore */ }
        }

        var flowCount = (typeof interceptedFlows !== 'undefined'
            && Array.isArray(interceptedFlows)) ? interceptedFlows.length : 0;
        var overrideCount = (typeof interceptedMockRules !== 'undefined'
            && Array.isArray(interceptedMockRules)) ? interceptedMockRules.length : 0;
        var serverCount = (typeof window !== 'undefined' && window.__bowireMocks
            && typeof window.__bowireMocks.list === 'function')
            ? (window.__bowireMocks.list() || []).length
            : ((typeof mocksList !== 'undefined' && Array.isArray(mocksList)) ? mocksList.length : 0);

        function _subTabBtn(id, label, count) {
            var children = [el('span', { textContent: label })];
            if (typeof count === 'number') {
                children.push(el('span', {
                    className: 'bowire-rail-subtab-meta',
                    textContent: count ? String(count) : ''
                }));
            }
            return el('button', {
                className: 'bowire-rail-subtab' + (interceptSubView === id ? ' active' : ''),
                onClick: function () { setInterceptSubView(id); }
            }, children);
        }

        var tabStrip = el('div', { id: 'bowire-intercept-subtabs', className: 'bowire-rail-subtabs' },
            _subTabBtn('captured',       'Captured',       flowCount),
            _subTabBtn('live-overrides', 'Live overrides', overrideCount),
            _subTabBtn('mock-servers',   'Mock servers',   serverCount),
            _subTabBtn('settings',       'Settings')
        );
        var actionsBar = el('div', { className: 'bowire-rail-subtabs-actions' },
            el('button', {
                type: 'button',
                className: 'bowire-rail-subtabs-action',
                title: 'Reconnect to the traffic source',
                'aria-label': 'Reconnect',
                onClick: function () {
                    if (typeof interceptedConnectionState !== 'undefined') {
                        interceptedConnectionState = 'idle';
                    }
                    render();
                }
            }, el('span', { innerHTML: svgIcon('replay') })),
            el('button', {
                type: 'button',
                className: 'bowire-rail-subtabs-action',
                title: 'More actions…',
                'aria-label': 'More actions',
                onClick: function (e) {
                    e.stopPropagation();
                    if (typeof showContextMenu !== 'function') return;
                    var r = e.currentTarget.getBoundingClientRect();
                    showContextMenu(r.left, r.bottom + 4, [{
                        label: 'Clear all flows',
                        icon: 'trash',
                        danger: true,
                        onClick: function () {
                            if (typeof bowireInterceptedClearFlows === 'function') {
                                bowireInterceptedClearFlows();
                            }
                        }
                    }]);
                }
            }, el('span', { innerHTML: svgIcon('dots') }))
        );
        var stripRow = el('div', { className: 'bowire-rail-subtabs-row' }, tabStrip, actionsBar);
        container.appendChild(stripRow);
        requestAnimationFrame(function () {
            var live = document.getElementById('bowire-intercept-subtabs');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, {
                    tabSelector: '.bowire-rail-subtab',
                    label: 'More tabs'
                });
            }
        });

        if (interceptSubView === 'live-overrides') {
            if (typeof renderInterceptedMocksListInto === 'function') {
                renderInterceptedMocksListInto(container);
            }
            return;
        }
        if (interceptSubView === 'mock-servers') {
            renderInterceptMockServersListInto(container);
            return;
        }
        if (interceptSubView === 'settings') {
            renderInterceptSettingsListInto(container);
            return;
        }
        // Captured
        renderInterceptCapturedListBodyInto(container);
    }

    function renderInterceptCapturedListBodyInto(container) {
        if (typeof interceptedConnectionState === 'undefined') return;

        if (interceptedConnectionState === 'connecting') {
            container.appendChild(el('div', { className: 'bowire-loading', style: 'padding:24px' },
                el('div', { className: 'bowire-spinner' }),
                el('span', { className: 'bowire-loading-text', textContent: 'Connecting to traffic source…' })
            ));
            return;
        }

        if (interceptedConnectionState === 'error') {
            container.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'Traffic source not reachable',
                body: (interceptedConnectionError || 'Failed to reach the traffic endpoints.')
                    + ' This rail talks to the same host that serves the workbench — check the host is running.',
                actions: [{
                    label: 'Retry',
                    primary: true,
                    onClick: function () { interceptedConnectionState = 'idle'; render(); }
                }]
            }));
            return;
        }

        if (!Array.isArray(interceptedFlows) || interceptedFlows.length === 0) {
            var emptyBody = bowireInterceptIsEmbedded()
                ? 'Add app.UseBowireInterceptor() to this host\'s pipeline, then drive any request through it from any client. Captured flows land here in real time.'
                : 'Point your client at the bowire proxy / bowire interceptor CLI sidecar to start capturing. Captured flows land here in real time.';
            container.appendChild(renderEmptyCard({
                icon: 'trafficLight',
                headline: 'No traffic yet',
                body: emptyBody,
                actions: [
                    {
                        id: 'bowire-intercept-empty-tour-btn',
                        label: 'Take a tour',
                        onClick: function () {
                            if (typeof window !== 'undefined'
                                && typeof window.bowireStartCaptureTrafficTour === 'function') {
                                window.bowireStartCaptureTrafficTour({ force: true });
                            }
                        }
                    }
                ]
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
                    id: 'bowire-intercept-flow-' + flow.id,
                    className: 'bowire-proxy-list-item' + (isActive ? ' selected' : ''),
                    onClick: function () {
                        interceptedFlowSelectedId = flow.id;
                        if (typeof bowireInterceptedEnsureDetail === 'function') {
                            bowireInterceptedEnsureDetail(flow.id);
                        }
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
                        ? el('span', { className: 'bowire-proxy-list-tls', title: 'Served from override rule', textContent: 'M' })
                        : null
                ));
            })(interceptedFlows[i]);
        }
    }

    function renderInterceptMockServersListInto(container) {
        // The Mock package's shim installs window.__bowireMocks. When
        // it's missing the Mock package isn't referenced; render an
        // empty state so operators see WHY there's no list rather than
        // a silently-empty pane.
        if (typeof window === 'undefined' || !window.__bowireMocks) {
            container.appendChild(renderEmptyCard({
                icon: 'mock',
                headline: 'Mock package not loaded',
                body: 'Reference Kuestenlogik.Bowire.Mock from the host to enable standalone mock servers. The standalone Bowire CLI ships with it included.'
            }));
            return;
        }
        var list = (typeof window.__bowireMocks.list === 'function')
            ? (window.__bowireMocks.list() || [])
            : ((typeof mocksList !== 'undefined' && Array.isArray(mocksList)) ? mocksList : []);

        if (!list.length) {
            container.appendChild(renderEmptyCard({
                icon: 'mock',
                headline: 'No mock servers running',
                body: 'Mock servers are standalone replay hosts spun up from a recording. Open the Recordings rail and use "Run as mock" on any session to start one.'
            }));
            return;
        }
        for (var i = 0; i < list.length; i++) {
            (function (m) {
                var isActive = (typeof mockSelectedId !== 'undefined' && mockSelectedId === m.mockId);
                // Reuse the shared sidebar list-item helper so the row
                // gets the same hover-reveal stop button + selection
                // chrome as every other sidebar list (Recordings,
                // Workspaces, &c.).
                if (typeof renderSidebarListItem === 'function') {
                    container.appendChild(renderSidebarListItem({
                        id: 'bowire-intercept-mock-server-' + m.mockId,
                        name: m.recordingName || ('mock-' + m.port),
                        meta: 'port ' + m.port,
                        selected: isActive,
                        onClick: function () {
                            if (typeof mockSelectedId !== 'undefined') mockSelectedId = m.mockId;
                            render();
                        },
                        deleteTitle: 'Stop mock host',
                        onDelete: function () {
                            if (window.__bowireMocks && typeof window.__bowireMocks.stop === 'function') {
                                window.__bowireMocks.stop(m.mockId);
                                if (typeof mockSelectedId !== 'undefined' && mockSelectedId === m.mockId) {
                                    mockSelectedId = null;
                                }
                            }
                        }
                    }));
                } else {
                    container.appendChild(el('div', {
                        id: 'bowire-intercept-mock-server-' + m.mockId,
                        className: 'bowire-proxy-list-item' + (isActive ? ' selected' : ''),
                        onClick: function () {
                            if (typeof mockSelectedId !== 'undefined') mockSelectedId = m.mockId;
                            render();
                        }
                    },
                        el('span', { className: 'bowire-proxy-list-method', textContent: 'MOCK' }),
                        el('span', {
                            className: 'bowire-proxy-list-status bowire-proxy-status-ok',
                            textContent: String(m.port || '?')
                        }),
                        el('span', {
                            className: 'bowire-proxy-list-url',
                            textContent: m.recordingName || ('mock-' + m.port),
                            title: m.recordingName || ('mock-' + m.port)
                        })
                    ));
                }
            })(list[i]);
        }
    }

    function renderInterceptSettingsListInto(container) {
        var embedded = bowireInterceptIsEmbedded();
        var headline = embedded ? 'Embedded middleware' : 'Standalone proxy';
        var body = embedded
            ? 'Bowire is mounted in-process via MapBowire(). Wire UseBowireInterceptor() into the pipeline to capture flows. The main pane shows the live middleware status.'
            : 'Bowire runs standalone. Captured flows arrive from the bowire proxy / bowire interceptor CLI sidecar. The main pane lets you point the rail at a remote sidecar URL.';

        container.appendChild(renderEmptyCard({
            icon: 'plug',
            headline: headline,
            body: body
        }));
    }

    // ---- Main pane ----

    function renderInterceptMainPane() {
        const pane = el('div', { className: 'bowire-env-editor-main' });

        if (interceptSubView === 'settings') {
            return renderInterceptSettingsMainPane(pane);
        }
        if (interceptSubView === 'live-overrides') {
            if (typeof renderInterceptedMocksMainPane === 'function') {
                return renderInterceptedMocksMainPane(pane);
            }
            return pane;
        }
        if (interceptSubView === 'mock-servers') {
            return renderInterceptMockServersMainPane(pane);
        }

        // Captured (flows) — reuse the intercepted flow detail surface.
        if (typeof interceptedConnectionState !== 'undefined'
            && interceptedConnectionState === 'error') {
            pane.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: 'No traffic source connection',
                body: 'Click Retry in the sidebar — this rail talks to the same host that serves the workbench.'
            }));
            return pane;
        }

        if (typeof interceptedFlowSelectedId === 'undefined' || !interceptedFlowSelectedId) {
            pane.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'Pick a captured flow',
                body: 'Select a row in the sidebar to inspect the request / response, or send it into the recording pipeline.'
            }));
            return pane;
        }

        const summary = Array.isArray(interceptedFlows)
            ? interceptedFlows.find(function (f) { return f.id === interceptedFlowSelectedId; })
            : null;
        const detail = (typeof interceptedFlowDetailCache === 'object' && interceptedFlowDetailCache)
            ? interceptedFlowDetailCache[interceptedFlowSelectedId]
            : null;
        if (!summary && !detail) {
            pane.appendChild(renderEmptyCard({
                icon: 'history',
                headline: 'Flow no longer available',
                body: 'The capture ring buffer evicted this entry. Pick a more recent flow from the sidebar.'
            }));
            return pane;
        }

        const flow = detail || summary;
        const actionRow = el('div', { className: 'bowire-env-editor-header' },
            el('h2', { className: 'bowire-env-editor-title', textContent: (flow.method || 'GET') + ' ' + (flow.path || flow.url || '') }),
            el('span', { style: 'flex:1' }),
            el('button', {
                id: 'bowire-intercept-send-rec-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Convert this captured flow into a Bowire recording you can replay, fuzz, or include in a test collection.',
                onClick: function () {
                    if (typeof bowireInterceptedSendToRecording === 'function') {
                        bowireInterceptedSendToRecording(interceptedFlowSelectedId);
                    }
                }
            }, el('span', { textContent: 'Send to recording' })),
            el('button', {
                id: 'bowire-intercept-override-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Seed a live-override rule from this flow. The interceptor will serve the captured response in place of the upstream endpoint until the rule is paused or removed.',
                onClick: function () {
                    if (typeof bowireInterceptedSeedMockFromFlow === 'function') {
                        bowireInterceptedSeedMockFromFlow(interceptedFlowSelectedId);
                    }
                }
            }, el('span', { textContent: 'Override this route' }))
        );
        pane.appendChild(actionRow);

        const meta = el('div', { className: 'bowire-proxy-detail-meta' });
        function _interceptMetaCell(label, value) {
            return el('div', { className: 'bowire-proxy-detail-meta-cell' },
                el('div', { className: 'bowire-proxy-detail-meta-label', textContent: label }),
                el('div', { className: 'bowire-proxy-detail-meta-value', textContent: value })
            );
        }
        meta.appendChild(_interceptMetaCell('Status', String(flow.responseStatus || (flow.error ? 'ERR' : '…'))));
        meta.appendChild(_interceptMetaCell('Scheme', flow.scheme || 'http'));
        meta.appendChild(_interceptMetaCell('Latency', (flow.latencyMs || 0) + ' ms'));
        meta.appendChild(_interceptMetaCell('Captured', flow.capturedAt ? new Date(flow.capturedAt).toLocaleTimeString() : ''));
        if (flow.streaming) meta.appendChild(_interceptMetaCell('Mode', 'streaming'));
        if (flow.mocked) meta.appendChild(_interceptMetaCell('Source', 'override rule'));
        if (flow.error) meta.appendChild(_interceptMetaCell('Error', flow.error));
        pane.appendChild(meta);

        if (!detail) {
            pane.appendChild(el('div', { style: 'padding:24px; opacity:0.7', textContent: 'Loading full payload…' }));
            return pane;
        }

        if (typeof renderHttpExchange === 'function') {
            pane.appendChild(renderHttpExchange(detail));
        }
        return pane;
    }

    function renderInterceptMockServersMainPane(pane) {
        // Delegate to the Mock package's main-pane renderer when present.
        // The package installs window.__bowireMocks.renderRailMain at
        // load time; falling back to the inline empty state keeps the
        // rail useful even when only the package's CSS-side state is
        // there (no .NET assembly reference).
        if (typeof window !== 'undefined'
            && window.__bowireMocks
            && typeof window.__bowireMocks.renderRailMain === 'function') {
            try {
                var rendered = window.__bowireMocks.renderRailMain(pane);
                if (rendered) return rendered;
            } catch (e) { /* fall through */ }
        }
        if (typeof window === 'undefined' || !window.__bowireMocks) {
            pane.appendChild(renderEmptyCard({
                icon: 'mock',
                headline: 'Mock package not loaded',
                body: 'Reference Kuestenlogik.Bowire.Mock from the host to enable standalone mock servers.'
            }));
            return pane;
        }
        pane.appendChild(renderEmptyCard({
            icon: 'mock',
            headline: 'Pick a mock server',
            body: 'Pick a running mock from the sidebar to see its URL, live request log, and stop control.'
        }));
        return pane;
    }

    function renderInterceptSettingsMainPane(pane) {
        var embedded = bowireInterceptIsEmbedded();

        if (embedded) {
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: 'Deployment' }),
                el('div', { className: 'bowire-env-editor-field-value', textContent: 'Embedded — Bowire is mounted in-process via MapBowire().' })
            ));
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: 'Middleware' }),
                el('div', { className: 'bowire-env-editor-field-value', textContent: 'Add app.UseBowireInterceptor() to the host\'s pipeline. When wired, every request flowing through the pipeline is captured.' })
            ));
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: 'Endpoint base' }),
                el('div', { className: 'bowire-env-editor-field-value', textContent: '/api/intercepted/* (alias /api/traffic/*) on this same origin.' })
            ));
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: 'Docs' }),
                el('button', {
                    className: 'bowire-env-editor-action-btn',
                    onClick: function () {
                        if (typeof helpOpenDrawer === 'function') {
                            helpOpenDrawer('features/proxy');
                        }
                    }
                }, el('span', { textContent: 'Open Intercept docs' }))
            ));
            return pane;
        }

        var currentUrl = (typeof bowireProxyEffectiveApiUrl === 'function')
            ? bowireProxyEffectiveApiUrl() : '';

        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: 'Deployment' }),
            el('div', { className: 'bowire-env-editor-field-value', textContent: 'Standalone — Bowire runs as a CLI tool (bowire proxy or bowire interceptor).' })
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: 'External sidecar URL' }),
            el('div', { className: 'bowire-env-editor-field-value', textContent: currentUrl || 'http://127.0.0.1:8889 (loopback default)' })
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: '' }),
            el('button', {
                className: 'bowire-env-editor-action-btn',
                onClick: function () {
                    if (typeof bowirePrompt !== 'function') return;
                    bowirePrompt('Sidecar API URL', {
                        title: 'Intercept sidecar',
                        defaultValue: currentUrl || '',
                        placeholder: 'http://localhost:8889',
                        confirmText: 'Save'
                    }).then(function (val) {
                        if (val === null || val === undefined) return;
                        if (typeof bowireProxySetApiUrl === 'function') {
                            bowireProxySetApiUrl(String(val).trim());
                        }
                        render();
                    });
                }
            }, el('span', { textContent: 'Edit URL…' }))
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: 'CLI subcommands' }),
            el('div', { className: 'bowire-env-editor-field-value', textContent: 'bowire proxy (MITM) and bowire interceptor (reverse-proxy edge) both populate the Intercept rail.' })
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: 'Docs' }),
            el('button', {
                className: 'bowire-env-editor-action-btn',
                onClick: function () {
                    if (typeof helpOpenDrawer === 'function') {
                        helpOpenDrawer('features/proxy');
                    }
                }
            }, el('span', { textContent: 'Open Intercept docs' }))
        ));
        return pane;
    }
