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

    // R3b — Interceptor activation status. The Captured / Live
    // overrides / Settings sub-tabs only make sense when EITHER the
    // embedded middleware is wired in OR the standalone Tool has a
    // reverse-proxy running. The Mock-servers sub-tab is UNAFFECTED
    // (it works regardless of interceptor state). When the probe says
    // disabled, those three sub-tabs surface an activation empty-state
    // with the right CTA per deployment mode.
    //
    // State machine:
    //   null      — probe hasn't fired yet (initial). Render assumes
    //                "active" optimistically so we don't flash the
    //                empty state on first paint before the probe
    //                returns; the empty state appears after the probe
    //                fills in the data.
    //   { ... }   — last response from /api/intercepted/status.
    let interceptStatus = null;
    let interceptStatusLoading = false;

    function bowireInterceptLoadStatus() {
        if (interceptStatusLoading) return;
        interceptStatusLoading = true;
        var prefix = (typeof config !== 'undefined' && config && config.prefix) ? config.prefix : '';
        fetch(prefix + '/api/intercepted/status')
            .then(function (r) {
                if (r.status === 404 || r.status === 501) {
                    return { enabled: false, source: 'none', middlewareActive: false, reverseProxyCount: 0 };
                }
                return r.ok ? r.json() : null;
            })
            .catch(function () { return null; })
            .then(function (body) {
                interceptStatusLoading = false;
                if (body && typeof body === 'object') {
                    interceptStatus = body;
                } else {
                    // Probe failed entirely — assume disabled so the
                    // operator sees the activation copy rather than
                    // an empty-list mystery.
                    interceptStatus = { enabled: false, source: 'none', middlewareActive: false, reverseProxyCount: 0 };
                }
                render();
            });
    }

    // Re-probe whenever the reverse-proxy state changes — the standalone
    // start path doesn't know the rail is listening, so we lean on the
    // existing reverseProxyState global as the activation signal and
    // mirror it into interceptStatus on render.
    function bowireInterceptResolvedEnabled() {
        // Optimistic null → assume enabled so the first paint after a
        // probe hasn't returned yet doesn't flash the empty state.
        if (interceptStatus === null) return true;
        if (interceptStatus.enabled) return true;
        // Fold the JS-side reverse-proxy state in as a fallback — the
        // /api/intercepted/status server endpoint reports the live
        // registry count, but a freshly-started proxy in the same
        // session beats the probe by a heartbeat. Checking the local
        // reverseProxyState (refreshed by tools-reverse-proxy.js on
        // every start/stop) lets the rail unblock immediately.
        if (typeof reverseProxyState !== 'undefined'
            && reverseProxyState
            && Array.isArray(reverseProxyState.running)
            && reverseProxyState.running.length > 0) {
            return true;
        }
        return false;
    }

    function setInterceptSubView(next) {
        interceptSubView = next;
        try { localStorage.setItem('bowire_intercept_sub_tab', next); } catch { /* ignore */ }
        render();
    }

    function bowireInterceptIsEmbedded() {
        return (typeof uiMode !== 'undefined' && uiMode === 'embedded');
    }

    // R3b — Shared activation empty-state. Rendered in each of the
    // three interceptor-gated sub-tabs (Captured / Live overrides /
    // Settings) when neither the embedded middleware nor a standalone
    // reverse-proxy is live. The Mock-servers sub-tab is intentionally
    // not gated on this — mock servers are standalone replay hosts
    // spun up from recordings and work regardless of interceptor state.
    //
    // Activation copy adapts to deployment mode:
    //   embedded mode  → only the "add app.UseBowireInterceptor()" hint
    //                    (no CTA because the operator has to edit code)
    //   standalone     → both hints + a "Start Reverse-Proxy now" CTA
    //                    that opens the existing reverse-proxy modal
    function renderInterceptActivationEmptyState(subTabLabel) {
        var embedded = bowireInterceptIsEmbedded();
        var headline = 'No interceptor running';
        var body = 'Embed: add app.UseBowireInterceptor() to your host. '
            + 'Standalone: start the Reverse-Proxy from the topbar.';
        var actions = [];
        if (!embedded && typeof window !== 'undefined'
            && typeof window.bowireOpenReverseProxyModal === 'function') {
            actions.push({
                id: 'bowire-intercept-start-proxy-btn',
                label: t('intercept.startProxy'),
                primary: true,
                onClick: function () {
                    window.bowireOpenReverseProxyModal();
                    // Probe again after the operator finishes the modal —
                    // best-effort polling on the existing refresh helper
                    // so the rail unblocks as soon as a proxy is bound.
                    if (typeof window.bowireRefreshReverseProxies === 'function') {
                        setTimeout(function () {
                            window.bowireRefreshReverseProxies({ rerender: false });
                            bowireInterceptLoadStatus();
                        }, 500);
                    }
                }
            });
        }
        actions.push({
            label: t('intercept.recheck'),
            onClick: function () {
                interceptStatus = null;
                bowireInterceptLoadStatus();
            }
        });
        return renderEmptyCard({
            icon: 'plug',
            headline: headline + (subTabLabel ? ' — ' + subTabLabel : ''),
            body: body,
            actions: actions
        });
    }

    // ---- Sidebar — four sub-tabs ----

    function renderInterceptListInto(container) {
        // R3b — lazy-fire the status probe on first render so the
        // empty-state choice is data-driven by the host's actual
        // wiring. Subsequent renders consume the cached interceptStatus
        // without re-fetching; the existing Reconnect / start-proxy
        // affordances clear it when the operator changes the state.
        if (interceptStatus === null && !interceptStatusLoading) {
            bowireInterceptLoadStatus();
        }

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
                title: t('intercept.reconnectSource'),
                'aria-label': t('intercept.reconnect'),
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
                title: t('menu.moreActions'),
                'aria-label': t('menu.moreActions'),
                onClick: function (e) {
                    e.stopPropagation();
                    if (typeof showContextMenu !== 'function') return;
                    var r = e.currentTarget.getBoundingClientRect();
                    showContextMenu(r.left, r.bottom + 4, [{
                        label: t('intercept.clearFlows'),
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
                    label: t('main.moreTabs')
                });
            }
        });

        // R3b — gate the three interceptor-dependent sub-tabs on the
        // status probe. Mock-servers falls through to its own renderer
        // even when the interceptor isn't running. The tabs stay
        // visible (discoverability for operators learning what the
        // Intercept rail does); only the BODY swaps to the activation
        // empty-state.
        var interceptorEnabled = bowireInterceptResolvedEnabled();

        if (interceptSubView === 'live-overrides') {
            if (!interceptorEnabled) {
                container.appendChild(renderInterceptActivationEmptyState('Live overrides'));
                return;
            }
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
            if (!interceptorEnabled) {
                container.appendChild(renderInterceptActivationEmptyState('Settings'));
                return;
            }
            renderInterceptSettingsListInto(container);
            return;
        }
        // Captured
        if (!interceptorEnabled) {
            container.appendChild(renderInterceptActivationEmptyState('Captured'));
            return;
        }
        renderInterceptCapturedListBodyInto(container);
    }

    function renderInterceptCapturedListBodyInto(container) {
        if (typeof interceptedConnectionState === 'undefined') return;

        if (interceptedConnectionState === 'connecting') {
            container.appendChild(el('div', { className: 'bowire-loading', style: 'padding:24px' },
                el('div', { className: 'bowire-spinner' }),
                el('span', { className: 'bowire-loading-text', textContent: t('intercept.connectingSource') })
            ));
            return;
        }

        if (interceptedConnectionState === 'error') {
            container.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: t('intercept.sourceUnreachable'),
                body: t('intercept.unreachableBody', {
                    error: interceptedConnectionError || t('intercept.sourceEndpointsFailed')
                }),
                actions: [{
                    label: t('intercept.retry'),
                    primary: true,
                    onClick: function () { interceptedConnectionState = 'idle'; render(); }
                }]
            }));
            return;
        }

        if (!Array.isArray(interceptedFlows) || interceptedFlows.length === 0) {
            var emptyBody = bowireInterceptIsEmbedded()
                ? t('intercept.noTrafficEmbeddedBody')
                : t('intercept.noTrafficStandaloneBody');
            container.appendChild(renderEmptyCard({
                icon: 'trafficLight',
                headline: t('intercept.noTrafficYet'),
                body: emptyBody,
                actions: [
                    {
                        id: 'bowire-intercept-empty-tour-btn',
                        label: t('common.takeTour'),
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
                    },
                    // R3a — Captured row → Recordings transition.
                    // Right-click any captured flow to persist it as a
                    // recording in the active workspace. Reuses the
                    // existing bowireInterceptedSendToRecording helper
                    // — that path POSTs /flows/{id}/recording and
                    // imports the result into recordingsList — so the
                    // operator lands with a fresh recording open on
                    // the Recordings rail.
                    onContextMenu: function (e) {
                        if (typeof showContextMenu !== 'function') return;
                        e.preventDefault();
                        e.stopPropagation();
                        showContextMenu(e.clientX, e.clientY, [
                            {
                                label: t('intercept.saveAsRecording'),
                                icon: 'recording',
                                title: t('intercept.saveAsRecordingTitle'),
                                onClick: function () {
                                    if (typeof bowireInterceptedSendToRecording === 'function') {
                                        bowireInterceptedSendToRecording(flow.id);
                                    }
                                }
                            }
                        ]);
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
                        ? el('span', { className: 'bowire-proxy-list-tls', title: t('intercept.streamingResponse'), textContent: '↻' })
                        : null,
                    flow.mocked
                        ? el('span', { className: 'bowire-proxy-list-tls', title: t('intercept.fromOverride'), textContent: 'M' })
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
                headline: t('intercept.mockPackageMissing'),
                body: t('intercept.mockPackageMissingBodyCli'),
                actions: [{
                    label: t('intercept.setupDocs'),
                    primary: true,
                    onClick: function () { window.open('https://bowire.io/docs/setup/embedded.html', '_blank', 'noopener'); }
                }]
            }));
            return;
        }
        var list = (typeof window.__bowireMocks.list === 'function')
            ? (window.__bowireMocks.list() || [])
            : ((typeof mocksList !== 'undefined' && Array.isArray(mocksList)) ? mocksList : []);

        if (!list.length) {
            container.appendChild(renderEmptyCard({
                icon: 'mock',
                headline: t('intercept.noMockServers'),
                body: t('intercept.noMockServersBody'),
                actions: [{
                    label: t('hint.openRecordings'),
                    primary: true,
                    onClick: function () { railMode = 'recordings'; render(); }
                }]
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
                        deleteTitle: t('intercept.stopMockHost'),
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
        var headline = embedded ? t('intercept.embeddedHeadline') : t('intercept.standaloneHeadline');
        var body = embedded
            ? t('intercept.embeddedBody')
            : t('intercept.standaloneBody');

        container.appendChild(renderEmptyCard({
            icon: 'plug',
            headline: headline,
            body: body,
            actions: [{
                label: t('intercept.docs'),
                primary: true,
                onClick: function () { window.open('https://bowire.io/docs/features/', '_blank', 'noopener'); }
            }]
        }));
    }

    // ---- Main pane ----

    function renderInterceptMainPane() {
        const pane = el('div', { className: 'bowire-env-editor-main' });

        // R3b — Mirror the sidebar's activation gate so the main pane
        // doesn't show a misleading "select a flow…" / "no override
        // rule selected" empty when nothing can be selected. Mock
        // servers stays unaffected.
        var interceptorEnabled = bowireInterceptResolvedEnabled();

        if (interceptSubView === 'settings') {
            if (!interceptorEnabled) {
                pane.appendChild(renderInterceptActivationEmptyState(null));
                return pane;
            }
            return renderInterceptSettingsMainPane(pane);
        }
        if (interceptSubView === 'live-overrides') {
            if (!interceptorEnabled) {
                pane.appendChild(renderInterceptActivationEmptyState(null));
                return pane;
            }
            if (typeof renderInterceptedMocksMainPane === 'function') {
                return renderInterceptedMocksMainPane(pane);
            }
            return pane;
        }
        if (interceptSubView === 'mock-servers') {
            return renderInterceptMockServersMainPane(pane);
        }

        // Captured (flows) — reuse the intercepted flow detail surface.
        if (!interceptorEnabled) {
            pane.appendChild(renderInterceptActivationEmptyState(null));
            return pane;
        }
        if (typeof interceptedConnectionState !== 'undefined'
            && interceptedConnectionState === 'error') {
            pane.appendChild(renderEmptyCard({
                icon: 'plug',
                headline: t('intercept.noSourceConnection'),
                body: t('intercept.retryHint')
            }));
            return pane;
        }

        if (typeof interceptedFlowSelectedId === 'undefined' || !interceptedFlowSelectedId) {
            pane.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: t('intercept.pickFlow'),
                body: t('intercept.pickFlowBody')
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
                headline: t('intercept.flowGone'),
                body: t('intercept.flowGoneBody')
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
                title: t('intercept.toRecordingTitle'),
                onClick: function () {
                    if (typeof bowireInterceptedSendToRecording === 'function') {
                        bowireInterceptedSendToRecording(interceptedFlowSelectedId);
                    }
                }
            }, el('span', { textContent: t('intercept.sendToRecording') })),
            el('button', {
                id: 'bowire-intercept-override-btn',
                className: 'bowire-env-editor-action-btn',
                title: t('intercept.overrideTitle'),
                onClick: function () {
                    if (typeof bowireInterceptedSeedMockFromFlow === 'function') {
                        bowireInterceptedSeedMockFromFlow(interceptedFlowSelectedId);
                    }
                }
            }, el('span', { textContent: t('intercept.overrideRoute') }))
        );
        pane.appendChild(actionRow);

        const meta = el('div', { className: 'bowire-proxy-detail-meta' });
        function _interceptMetaCell(label, value) {
            return el('div', { className: 'bowire-proxy-detail-meta-cell' },
                el('div', { className: 'bowire-proxy-detail-meta-label', textContent: label }),
                el('div', { className: 'bowire-proxy-detail-meta-value', textContent: value })
            );
        }
        meta.appendChild(_interceptMetaCell(t('intercept.metaStatus'), String(flow.responseStatus || (flow.error ? 'ERR' : '…'))));
        meta.appendChild(_interceptMetaCell(t('intercept.metaScheme'), flow.scheme || 'http'));
        meta.appendChild(_interceptMetaCell(t('intercept.metaLatency'), (flow.latencyMs || 0) + ' ms'));
        meta.appendChild(_interceptMetaCell(t('intercept.metaCaptured'), flow.capturedAt ? new Date(flow.capturedAt).toLocaleTimeString() : ''));
        if (flow.streaming) meta.appendChild(_interceptMetaCell(t('intercept.metaMode'), t('intercept.modeStreaming')));
        if (flow.mocked) meta.appendChild(_interceptMetaCell(t('intercept.metaSource'), t('intercept.sourceOverride')));
        if (flow.error) meta.appendChild(_interceptMetaCell(t('intercept.metaError'), flow.error));
        pane.appendChild(meta);

        if (!detail) {
            pane.appendChild(el('div', { style: 'padding:24px; opacity:0.7', textContent: t('intercept.loadingPayload') }));
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
                headline: t('intercept.mockPackageMissing'),
                body: t('intercept.mockPackageMissingBody')
            }));
            return pane;
        }
        pane.appendChild(renderEmptyCard({
            icon: 'mock',
            headline: t('intercept.pickMockServer'),
            body: t('intercept.pickMockServerBody')
        }));
        return pane;
    }

    function renderInterceptSettingsMainPane(pane) {
        var embedded = bowireInterceptIsEmbedded();

        if (embedded) {
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.deployment') }),
                el('div', { className: 'bowire-env-editor-field-value', textContent: t('intercept.embeddedNote') })
            ));
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.middleware') }),
                el('div', { className: 'bowire-env-editor-field-value',
                    textContent: t('intercept.middlewareNote') })
            ));
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.endpointBase') }),
                el('div', { className: 'bowire-env-editor-field-value', textContent: t('intercept.endpointBaseNote') })
            ));
            pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
                el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.docsLabel') }),
                el('button', {
                    className: 'bowire-env-editor-action-btn',
                    onClick: function () {
                        if (typeof helpOpenDrawer === 'function') {
                            helpOpenDrawer('features/proxy');
                        }
                    }
                }, el('span', { textContent: t('intercept.openDocs') }))
            ));
            return pane;
        }

        var currentUrl = (typeof bowireProxyEffectiveApiUrl === 'function')
            ? bowireProxyEffectiveApiUrl() : '';

        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.deployment') }),
            el('div', { className: 'bowire-env-editor-field-value', textContent: t('intercept.standaloneNote') })
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.sidecarUrl') }),
            el('div', { className: 'bowire-env-editor-field-value', textContent: currentUrl || 'http://127.0.0.1:8889 (loopback default)' })
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: '' }),
            el('button', {
                className: 'bowire-env-editor-action-btn',
                onClick: function () {
                    if (typeof bowirePrompt !== 'function') return;
                    bowirePrompt(t('intercept.sidecarPrompt'), {
                        title: t('intercept.sidecarTitle'),
                        defaultValue: currentUrl || '',
                        placeholder: 'http://localhost:8889',
                        confirmText: t('common.save')
                    }).then(function (val) {
                        if (val === null || val === undefined) return;
                        if (typeof bowireProxySetApiUrl === 'function') {
                            bowireProxySetApiUrl(String(val).trim());
                        }
                        render();
                    });
                }
            }, el('span', { textContent: t('intercept.editUrl') }))
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.cliSubcommands') }),
            el('div', { className: 'bowire-env-editor-field-value',
                textContent: t('intercept.cliNote') })
        ));
        pane.appendChild(el('div', { className: 'bowire-env-editor-field-row' },
            el('label', { className: 'bowire-env-editor-field-label', textContent: t('intercept.docsLabel') }),
            el('button', {
                className: 'bowire-env-editor-action-btn',
                onClick: function () {
                    if (typeof helpOpenDrawer === 'function') {
                        helpOpenDrawer('features/proxy');
                    }
                }
            }, el('span', { textContent: t('intercept.openDocs') }))
        ));
        return pane;
    }

    // #306 / #314 — Intercept owns its sidebar shell + main container and
    // registers both on the rail-renderer seam; core stops naming
    // 'intercept' in its dispatch.
    function renderInterceptSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        var list = el('div', { id: 'bowire-sidebar-list-intercept', className: 'bowire-service-list' });
        renderInterceptListInto(list);
        sidebar.appendChild(list);
        return sidebar;
    }
    function renderInterceptMain() {
        // Reproduce the morphdom-keyed id core built in mainViewKey:
        // bowire-main-intercept-<subview>-<flowid>.
        var sub = (typeof interceptSubView !== 'undefined' ? interceptSubView : 'captured');
        var flowId = (typeof interceptedFlowSelectedId !== 'undefined' ? (interceptedFlowSelectedId || 'none') : 'none');
        var main = el('div', { id: 'bowire-main-intercept-' + sub + '-' + flowId, className: 'bowire-main' });
        main.appendChild(renderInterceptMainPane());
        return main;
    }
    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.interceptSidebar = renderInterceptSidebar;
        window.__bowireRailRenderers.interceptMain = renderInterceptMain;
    }

