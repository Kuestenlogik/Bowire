    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // #315 — Workbench "Traffic" rail.
    //
    // Unifies the previous Proxy + Intercepted rails. A given Bowire
    // process is NEVER both Standalone AND Embedded at the same time —
    // the deployment shape is fixed by how Bowire was launched. The
    // Traffic rail reads __BOWIRE_CONFIG__.embeddedMode (set from
    // BowireOptions.Mode) on every render and adapts:
    //
    //   Standalone (uiMode === 'standalone' / 'standalone-locked'):
    //     header → "Standalone proxy mode"
    //     Settings sub-tab → loopback / external proxy URL config
    //   Embedded (uiMode === 'embedded'):
    //     header → "Embedded middleware mode"
    //     Settings sub-tab → middleware status (UseBowireInterceptor?)
    //
    // Flows + Mock Rules sub-tabs render IDENTICALLY across deployments.
    // Both read from the same /api/intercepted/* store (aliased to
    // /api/traffic/* — either path resolves the same response).
    //
    // The Flows + Mock Rules handlers are intentionally delegated to the
    // existing intercepted-view.js implementations (bowireIntercepted*)
    // because (a) the underlying store + endpoint surface is the same,
    // and (b) keeping one source of truth for the flow / rule rendering
    // avoids two parallel code paths drifting apart. The legacy
    // sidebarView='intercepted' + 'proxy' code paths stay registered
    // for the obsolete-window release; the boot migration rewrites
    // 'proxy' / 'intercepted' → 'traffic' on first paint so new users
    // land on this surface.
    // ------------------------------------------------------------------

    let trafficSubView = 'flows';        // 'flows' | 'mocks' | 'settings'

    function bowireTrafficIsEmbedded() {
        return (typeof uiMode !== 'undefined' && uiMode === 'embedded');
    }

    function bowireTrafficModeLabel() {
        return bowireTrafficIsEmbedded()
            ? 'Embedded middleware mode'
            : 'Standalone proxy mode';
    }

    // ---- Sidebar (Flows | Mock Rules | Settings sub-tabs) ----

    function renderTrafficListInto(container) {
        // Lazy-load flow snapshot + mock rules through the existing
        // intercepted helpers. They auto-connect to /api/intercepted/*
        // on the same workbench origin — endpoints respond regardless
        // of whether UseBowireInterceptor() was called (empty store
        // surfaces an empty-state card).
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

        // Top toolbar with the 'Traffic' title retired — read as an
        // inconsistent extra header next to Home (no header at all).
        // Operator feedback: 'warum hat traffic so eine überschrift?
        // home sieht anders aus.' The Reconnect + Clear-all-flows
        // actions move into the right-aligned action group on the
        // sub-tab strip below so they stay one click away.

        // Mode banner retired — no other rail carries a sub-headline
        // above its sub-tab strip, and the operator preference was
        // 'entweder alle mit headline oder keiner'. Mode information
        // (Standalone vs Embedded) now lives ONLY in the Settings
        // sub-tab, where it sits alongside the listen-port / upstream
        // / middleware-status config that already adapts per mode.

        // Sub-tab strip: Flows | Mock Rules | Settings. Same recessed-
        // strip pattern the other rails use.
        var flowCount = (typeof interceptedFlows !== 'undefined'
            && Array.isArray(interceptedFlows)) ? interceptedFlows.length : 0;
        var mockCount = (typeof interceptedMockRules !== 'undefined'
            && Array.isArray(interceptedMockRules)) ? interceptedMockRules.length : 0;

        var tabStrip = el('div', { id: 'bowire-traffic-subtabs', className: 'bowire-rail-subtabs' },
            el('button', {
                className: 'bowire-rail-subtab' + (trafficSubView === 'flows' ? ' active' : ''),
                onClick: function () { trafficSubView = 'flows'; render(); }
            }, el('span', { textContent: 'Flows' }),
                el('span', { className: 'bowire-rail-subtab-meta', textContent: flowCount ? String(flowCount) : '' })),
            el('button', {
                className: 'bowire-rail-subtab' + (trafficSubView === 'mocks' ? ' active' : ''),
                onClick: function () { trafficSubView = 'mocks'; render(); }
            }, el('span', { textContent: 'Mock Rules' }),
                el('span', { className: 'bowire-rail-subtab-meta', textContent: mockCount ? String(mockCount) : '' })),
            el('button', {
                className: 'bowire-rail-subtab' + (trafficSubView === 'settings' ? ' active' : ''),
                onClick: function () { trafficSubView = 'settings'; render(); }
            }, el('span', { textContent: 'Settings' }))
        );
        // Right-aligned action group on its own row container. The
        // Reconnect + more-actions buttons replace the dropped top
        // toolbar (operator: 'warum hat traffic so eine überschrift?
        // home sieht anders aus.'). Wrapping the tab strip + actions
        // in a flex row puts them on the same horizontal line without
        // fighting the .bowire-rail-subtab flex:1 sizing.
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
        // Overflow popover — three subtabs rarely overflow but the
        // helper makes the strip behave consistently when the rail
        // is narrowed below ~360 px. Look up the LIVE strip after
        // morphdom commits, not the JS-tree `tabStrip` reference
        // which can become detached.
        requestAnimationFrame(function () {
            var live = document.getElementById('bowire-traffic-subtabs');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, {
                    tabSelector: '.bowire-rail-subtab',
                    label: 'More tabs'
                });
            }
        });

        if (trafficSubView === 'mocks') {
            if (typeof renderInterceptedMocksListInto === 'function') {
                renderInterceptedMocksListInto(container);
            }
            return;
        }

        if (trafficSubView === 'settings') {
            renderTrafficSettingsListInto(container);
            return;
        }

        // Flows tab — reuse the intercepted flow list renderer for the
        // body. The toolbar above already wired the title + reconnect
        // affordance, so we render the body-only variant.
        renderTrafficFlowsListBodyInto(container);
    }

    function renderTrafficFlowsListBodyInto(container) {
        // Mirrors intercepted-view.js's body branches but without
        // re-rendering the toolbar (already painted above).
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
            var emptyBody = bowireTrafficIsEmbedded()
                ? 'Add app.UseBowireInterceptor() to this host\'s pipeline, then drive any request through it from any client. Captured flows land here in real time.'
                : 'Point your client at the bowire proxy / bowire interceptor CLI sidecar to start capturing. Captured flows land here in real time.';
            container.appendChild(renderEmptyCard({
                // Match the rail-strip glyph (Traffic uses
                // trafficLight, not the older 'globe' the
                // Intercepted rail inherited). Operator feedback:
                // 'welcome bei traffic hat anderes symbol (globus?
                // es müsste traffic light sein wie auf dem rail)'.
                icon: 'trafficLight',
                headline: 'No traffic yet',
                body: emptyBody,
                actions: [
                    // Per-rail welcome tour: explains the two
                    // deployment shapes (embedded / standalone) and
                    // the capture loop. There's no primary CTA on
                    // this card because the operator's action is
                    // outside Bowire (wire up the interceptor + drive
                    // traffic), so the tour CTA stands alone.
                    {
                        id: 'bowire-traffic-empty-tour-btn',
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
                    id: 'bowire-traffic-flow-' + flow.id,
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
                        ? el('span', { className: 'bowire-proxy-list-tls', title: 'Served from mock rule', textContent: 'M' })
                        : null
                ));
            })(interceptedFlows[i]);
        }
    }

    function renderTrafficSettingsListInto(container) {
        // Sidebar Settings sub-tab is intentionally a deep-link card —
        // the actual editor lives in the main pane where the field
        // widths make sense. This card is a status-glance + jump.
        var embedded = bowireTrafficIsEmbedded();
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

    function renderTrafficMainPane() {
        const pane = el('div', { className: 'bowire-env-editor-main' });

        // Headline retired. Operator: 'traffic rail: Traffic —
        // Standalone proxy mode überschrift wird nicht benötigt.
        // ist auch anders als bei den anderen rail welcome pages
        // […] bei den anderen rails ist dort keine headline.'
        // Deployment-mode signalling moves into the empty-card body
        // copy (or a small chip in the sub-tabs strip) when actually
        // needed; the rail title in the rail strip + the workbench
        // tab strip are enough to identify the surface.

        if (trafficSubView === 'settings') {
            return renderTrafficSettingsMainPane(pane);
        }

        if (trafficSubView === 'mocks') {
            // Delegate to the intercepted Mocks main pane — the editor
            // shape is identical (path pattern / method / response).
            if (typeof renderInterceptedMocksMainPane === 'function') {
                return renderInterceptedMocksMainPane(pane);
            }
            return pane;
        }

        // Flows main pane — reuse the intercepted flow detail surface so
        // the request / response renderer + Send-to-recording + Mock-
        // this-route affordances stay in one place.
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
                id: 'bowire-traffic-send-rec-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Convert this captured flow into a Bowire recording you can replay, fuzz, or include in a test collection.',
                onClick: function () {
                    if (typeof bowireInterceptedSendToRecording === 'function') {
                        bowireInterceptedSendToRecording(interceptedFlowSelectedId);
                    }
                }
            }, el('span', { textContent: 'Send to recording' })),
            el('button', {
                id: 'bowire-traffic-mock-this-btn',
                className: 'bowire-env-editor-action-btn',
                title: 'Seed a mock-injection rule from this flow. The interceptor will serve the captured response in place of the upstream endpoint until the rule is paused or removed.',
                onClick: function () {
                    if (typeof bowireInterceptedSeedMockFromFlow === 'function') {
                        bowireInterceptedSeedMockFromFlow(interceptedFlowSelectedId);
                    }
                }
            }, el('span', { textContent: 'Mock this route' }))
        );
        pane.appendChild(actionRow);

        // Metadata row + exchange — reuse the proxy-view helpers so the
        // visual shape stays uniform across rails.
        const meta = el('div', { className: 'bowire-proxy-detail-meta' });
        function _trafficMetaCell(label, value) {
            return el('div', { className: 'bowire-proxy-detail-meta-cell' },
                el('div', { className: 'bowire-proxy-detail-meta-label', textContent: label }),
                el('div', { className: 'bowire-proxy-detail-meta-value', textContent: value })
            );
        }
        meta.appendChild(_trafficMetaCell('Status', String(flow.responseStatus || (flow.error ? 'ERR' : '…'))));
        meta.appendChild(_trafficMetaCell('Scheme', flow.scheme || 'http'));
        meta.appendChild(_trafficMetaCell('Latency', (flow.latencyMs || 0) + ' ms'));
        meta.appendChild(_trafficMetaCell('Captured', flow.capturedAt ? new Date(flow.capturedAt).toLocaleTimeString() : ''));
        if (flow.streaming) meta.appendChild(_trafficMetaCell('Mode', 'streaming'));
        if (flow.mocked) meta.appendChild(_trafficMetaCell('Source', 'mock rule'));
        if (flow.error) meta.appendChild(_trafficMetaCell('Error', flow.error));
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

    function renderTrafficSettingsMainPane(pane) {
        var embedded = bowireTrafficIsEmbedded();

        if (embedded) {
            // Embedded — show middleware status. The operator can't flip
            // the middleware on from the browser; the surface is a
            // read-only diagnostic + doc-link.
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
                el('div', { className: 'bowire-env-editor-field-value', textContent: '/api/traffic/* (alias of /api/intercepted/*) on this same origin.' })
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
                }, el('span', { textContent: 'Open Traffic docs' }))
            ));
            return pane;
        }

        // Standalone — the rail can also point at a remote bowire proxy
        // sidecar via the workspace-level external endpoint. Reuse the
        // existing proxy-view helpers so there is one source of truth
        // for that URL across rails.
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
                        title: 'Traffic sidecar',
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
            el('div', { className: 'bowire-env-editor-field-value', textContent: 'bowire proxy (MITM) and bowire interceptor (reverse-proxy edge) both populate the Traffic rail.' })
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
            }, el('span', { textContent: 'Open Traffic docs' }))
        ));
        return pane;
    }
