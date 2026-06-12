    // ---- Empty-State Landing Page ----
    //
    // The Bowire main pane shows a context-sensitive landing page whenever
    // no method is selected. detectLandingState() reads the current global
    // state (serverUrls, services, isLoadingServices, discoveryErrors,
    // selectedProtocol, sourceMode, config.lockServerUrl) and returns one
    // of seven state strings, each rendered by a dedicated function below.
    //
    // States, in detection precedence (first match wins):
    //
    //   wrong-protocol-tab    Legacy ID from when Bowire had one tab per
    //                         protocol. Today: the active protocol filter
    //                         excludes every discovered service. Shows
    //                         one-click buttons that switch the filter to
    //                         a protocol that has hits.
    //   multi-url-partial     Multi-URL setup with at least one URL in error
    //                         state but at least one URL with services —
    //                         show per-URL status table + "select a method"
    //                         hint at the bottom.
    //   loading               Discovery in flight — animated spinner.
    //   discovery-failed      Locked-mode discovery failed (no services
    //                         after fetchServices completed) — error card
    //                         with the actual error message + troubleshoot
    //                         bullets + ".proto upload" alternative.
    //   editable-no-services  Editable-mode with URLs configured but
    //                         nothing discovered (and no in-flight call) —
    //                         per-URL Connect/Retry list + upload card.
    //   first-run             No URLs, no proto uploads — welcome hero
    //                         with the two onboarding CTAs.
    //   ready                 Default — services discovered, just no method
    //                         picked yet. The rich landing with logo,
    //                         service summary, recent history, tips, footer.

    function detectLandingState() {
        var hasServices = services.length > 0;
        var hasUploads = services.some(function (s) { return s.isUploaded === true; });
        var hasUrls = serverUrls.length > 0;

        // 1. Wrong protocol tab — services exist, current tab is empty
        if (hasServices && selectedProtocol) {
            var protocolHits = services.filter(function (s) { return s.source === selectedProtocol; });
            if (protocolHits.length === 0) return 'wrong-protocol-tab';
        }

        // 2. Multi-URL partial — some URLs broken but at least one has services
        if (serverUrls.length > 1) {
            var hasError = serverUrls.some(function (u) { return connectionStatuses[u] === 'error'; });
            if (hasError && hasServices) return 'multi-url-partial';
        }

        // 3. Loading — discovery in flight, no services yet
        if ((hasUrls || hasUploads) && !hasServices && isLoadingServices) {
            return 'loading';
        }

        // 4. Locked-mode discovery failed
        if (config.lockServerUrl && !hasServices && !isLoadingServices) {
            var errorKeys = Object.keys(discoveryErrors);
            if (errorKeys.length > 0) return 'discovery-failed';
        }

        // 5. Editable-mode, server configured, no services discovered
        if (!config.lockServerUrl && (hasUrls || hasUploads) && !hasServices && !isLoadingServices) {
            return 'editable-no-services';
        }

        // 6. First-run — empty slate. No URLs, no uploads, AND no
        //    services (embedded hosts auto-populate services[] via
        //    the in-process EndpointDataSource scan — those users
        //    don't need the "Connect to server" / "Upload schema"
        //    onboarding CTAs because Bowire is already showing them
        //    a populated sidebar; they belong in the `ready` state
        //    instead). First-run is strictly for the standalone
        //    `bowire` tool launched without `--url`, where the
        //    sidebar is genuinely empty and the user needs an
        //    entry point.
        if (!hasUrls && !hasUploads && !hasServices && !isLoadingServices) {
            return 'first-run';
        }

        // 7. Ready — services discovered, just waiting for method selection
        return 'ready';
    }

    function renderLandingPage(parent) {
        var landing = el('div', { className: 'bowire-landing' });
        var state = detectLandingState();
        switch (state) {
            case 'wrong-protocol-tab':   renderStateWrongProtocolTab(landing); break;
            case 'multi-url-partial':    renderStateMultiUrlPartial(landing); break;
            case 'loading':              renderStateLoading(landing); break;
            case 'discovery-failed':     renderStateDiscoveryFailed(landing); break;
            case 'editable-no-services': renderStateEditableNoServices(landing); break;
            case 'first-run':            renderStateFirstRun(landing); break;
            case 'ready':                renderStateReady(landing); break;
            default:                     renderStateReady(landing); break;
        }
        parent.appendChild(landing);
    }

    // ---- State 7: ready (most common, richest) ----

    function renderStateReady(parent) {
        // Discover-Ready landing now follows the rail-empty-card pattern
        // every other rail uses: compass icon (rail identity) + headline
        // + body + action links. Recent calls and tips render as separate
        // portal sections below — same visual rhythm as Home's bands.
        var card = el('div', { className: 'bowire-landing-card' });

        var summary = buildServiceSummary();
        var connectLine = buildConnectedHeadline();
        var bodyText = (connectLine === 'Pick a method from the sidebar')
            ? 'Pick a method from the sidebar tree to compose a request and invoke it. The sidebar lists every service and method we discovered.'
            : connectLine + (summary ? ' — ' + summary + '.' : '.');

        card.appendChild(renderEmptyCard({
            icon: 'compass',
            headline: 'Discover',
            body: bodyText
        }));

        // Recent history quick-recall (filtered to current servers).
        // Each row is its own action — clicking jumps into the request.
        var recents = getRecentHistoryForCurrentServers(5);
        if (recents.length > 0) {
            card.appendChild(el('div', {
                className: 'bowire-landing-section-title',
                textContent: 'Resume a recent call'
            }));
            var list = el('div', { className: 'bowire-landing-history' });
            for (var i = 0; i < recents.length; i++) {
                list.appendChild(renderRecentHistoryRow(recents[i]));
            }
            card.appendChild(list);
        }

        // Tips section. Same uppercase-section-title shape as the
        // home portal band-titles so the page reads as one continuous
        // column.
        card.appendChild(el('div', {
            className: 'bowire-landing-section-title',
            textContent: 'Tips'
        }));
        var tips = el('div', { className: 'bowire-landing-tips' });
        tips.appendChild(renderTipLine('search', 'Press Shift+/ to focus the command palette'));
        tips.appendChild(renderTipLine('send', 'Press Ctrl+Enter to invoke the selected method'));
        tips.appendChild(renderTipLine('repeat', 'Press R to repeat the last call'));
        card.appendChild(tips);

        renderLandingHelpFooter(card);
        parent.appendChild(card);
    }

    // ---- State 6: first-run ----

    function renderStateFirstRun(parent) {
        // #291 — first-run welcome moved to the Home rail mode. From
        // Discover we point the operator at Home so the onboarding
        // stays consolidated on one surface. Same shape as every other
        // rail's empty state (icon + headline + body + actions) so
        // first impressions across rails read consistently.
        var card = el('div', { className: 'bowire-landing-card' });
        card.appendChild(renderEmptyCard({
            icon: 'compass',
            headline: 'Discover is empty',
            body: 'Pick a workspace and add a URL or schema file from there — Home walks you through the first steps.',
            actions: [{
                label: 'Open Home',
                primary: true,
                onClick: function () {
                    railMode = 'home';
                    try { localStorage.setItem('bowire_rail_mode', 'home'); } catch { /* ignore */ }
                    render();
                }
            }, {
                label: 'Open Workspace',
                onClick: function () {
                    railMode = 'workspaces';
                    try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                    if (typeof workspacesSelectedId !== 'undefined') workspacesSelectedId = activeWorkspaceId;
                    render();
                }
            }]
        }));
        renderLandingHelpFooter(card);
        parent.appendChild(card);
    }

    // #291 — Welcome hero rendered inside the Home rail mode when no
    // URLs / uploads / services are configured. Same hero + tagline
    // as the old Discover first-run, but the CTAs navigate into the
    // workspace-detail pane (where #155 Phase 1 put URL + schema
    // management) instead of Discover's retired source selector.
    function renderHomeWelcomeHero(parent) {
        var hero = el('div', { className: 'bowire-landing-hero' });
        // #167 followup — logo + big "Welcome" headline removed per UX
        // direction: Home is a portal to start a use case or resume
        // recent work, not a billboard. A short subtitle is enough
        // anchor for first-time operators; the action cards below carry
        // the actual entry points.
        hero.appendChild(el('div', { className: 'bowire-landing-hero-tagline',
            textContent: 'Pick what you want to do — or jump back into recent activity below.' }));

        var grid = el('div', { className: 'bowire-landing-cta-grid' });
        grid.appendChild(renderFirstRunCard(
            'briefcase',
            'Configure your workspace',
            'A workspace is your project folder — URLs, environments, secrets, recordings all live in it. Open the workspace detail to add your first source.',
            'Open workspace',
            function () {
                railMode = 'workspaces';
                try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                if (typeof workspacesSelectedId !== 'undefined') {
                    workspacesSelectedId = activeWorkspaceId;
                }
                render();
            }
        ));
        grid.appendChild(renderFirstRunCard(
            'compass',
            'Browse Discover',
            'Already pointed Bowire at a service? Jump straight into Discover to pick a method and send your first request.',
            'Open Discover',
            function () {
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                sidebarView = 'services';
                render();
            }
        ));
        hero.appendChild(grid);

        renderLandingHelpFooter(hero);
        parent.appendChild(hero);
    }

    // Expose to other fragments (render-main.js's Home rail renders it
    // when first-run state holds).
    if (typeof window !== 'undefined') {
        window.bowireRenderHomeWelcomeHero = renderHomeWelcomeHero;
        window.bowireDetectLandingState = detectLandingState;
    }

    // ---- State 3: loading ----

    function renderStateLoading(parent) {
        var card = el('div', { className: 'bowire-landing-card bowire-landing-loading' });
        card.appendChild(el('div', { className: 'bowire-landing-spinner' }));

        var label = 'Discovering services\u2026';
        card.appendChild(el('div', { className: 'bowire-landing-loading-label', textContent: label }));
        card.appendChild(el('div', { className: 'bowire-landing-loading-hint',
            textContent: 'First connection can take a few seconds. If discovery fails, ensure your server has reflection / OpenAPI / GraphQL introspection enabled.' }));
        parent.appendChild(card);
    }

    // ---- State 4: discovery-failed (locked-mode) ----

    function renderStateDiscoveryFailed(parent) {
        var card = el('div', { className: 'bowire-landing-card bowire-landing-error' });

        var firstUrl = serverUrls[0] || '(unknown)';
        var errMsg = discoveryErrors[firstUrl] || Object.values(discoveryErrors)[0] || 'Connection failed';

        card.appendChild(el('div', { className: 'bowire-landing-error-icon', innerHTML: svgIcon('disconnect') }));
        card.appendChild(el('div', { className: 'bowire-landing-error-title',
            textContent: 'Could not discover services at ' + firstUrl }));
        card.appendChild(el('div', { className: 'bowire-landing-error-message', textContent: errMsg }));

        var listTitle = el('div', { className: 'bowire-landing-section-title',
            textContent: 'Common causes:' });
        card.appendChild(listTitle);

        var bullets = el('ul', { className: 'bowire-landing-troubleshoot' });
        bullets.appendChild(el('li', { textContent: 'gRPC server: enable Grpc.AspNetCore.Server.Reflection' }));
        bullets.appendChild(el('li', { textContent: 'REST server: ensure /swagger.json or /openapi.json is reachable' }));
        bullets.appendChild(el('li', { textContent: 'GraphQL server: __schema introspection must not be disabled' }));
        bullets.appendChild(el('li', { textContent: 'Network: server is reachable from this machine' }));
        card.appendChild(bullets);

        renderLandingHelpFooter(card);
        parent.appendChild(card);
    }

    // ---- State 5: editable-no-services ----

    function renderStateEditableNoServices(parent) {
        var card = el('div', { className: 'bowire-landing-card' });

        card.appendChild(el('div', { className: 'bowire-landing-section-title',
            textContent: 'No services discovered yet' }));
        card.appendChild(el('div', { className: 'bowire-landing-help-text',
            textContent: 'Bowire is configured but none of your servers responded with discoverable services. Verify each connection below or upload a schema as a fallback.' }));

        if (serverUrls.length > 0) {
            var statusList = el('div', { className: 'bowire-landing-status-list' });
            for (var i = 0; i < serverUrls.length; i++) {
                statusList.appendChild(renderUrlStatusRow(serverUrls[i]));
            }
            card.appendChild(statusList);
        }

        card.appendChild(el('div', { className: 'bowire-landing-divider' }));
        card.appendChild(el('div', { className: 'bowire-landing-help-text',
            textContent: 'Or upload a schema file (.proto, OpenAPI, GraphQL SDL) to discover services without a live server connection.' }));
        var btn = el('button', {
            className: 'bowire-landing-cta-secondary',
            onClick: function () {
                if (typeof sourceMode !== 'undefined') {
                    sourceMode = 'proto';
                    try { localStorage.setItem(SOURCE_MODE_KEY, 'proto'); } catch { /* ignore */ }
                }
                render();
            }
        },
            el('span', { innerHTML: svgIcon('upload') }),
            el('span', { textContent: 'Upload schema' })
        );
        card.appendChild(btn);

        renderLandingHelpFooter(card);
        parent.appendChild(card);
    }

    // ---- State 1: wrong-protocol-tab ----

    function renderStateWrongProtocolTab(parent) {
        var card = el('div', { className: 'bowire-landing-card' });

        var currentProtoName = (protocols.find(function (p) { return p.id === selectedProtocol; }) || {}).name || selectedProtocol;
        card.appendChild(el('div', { className: 'bowire-landing-section-title',
            textContent: 'No ' + currentProtoName + ' services found' }));
        card.appendChild(el('div', { className: 'bowire-landing-help-text',
            textContent: 'Your discovery URL didn\'t return any ' + currentProtoName + ' services. Switch to a protocol tab that has hits:' }));

        var switchGrid = el('div', { className: 'bowire-landing-protocol-switch' });
        for (var i = 0; i < protocols.length; i++) {
            (function (p) {
                if (p.id === selectedProtocol) return;
                if (!isProtocolEnabled(p.id)) return;
                var hits = services.filter(function (s) { return s.source === p.id; }).length;
                if (hits === 0) return;
                switchGrid.appendChild(el('button', {
                    className: 'bowire-landing-protocol-switch-btn',
                    onClick: function () {
                        // Switch the user to this protocol as the single
                        // active filter. Clearing + re-adding keeps the
                        // semantics identical to the pre-chip era for this
                        // specific code path (the landing empty-state
                        // nudge is a "take me there" jump, not an
                        // additive filter move).
                        protocolFilter.clear();
                        protocolFilter.add(p.id);
                        persistProtocolFilter();
                        refreshSelectedProtocolFromFilter();
                        render();
                    }
                },
                    el('span', { className: 'bowire-landing-protocol-switch-name', textContent: p.name }),
                    el('span', { className: 'bowire-landing-protocol-switch-count', textContent: String(hits) })
                ));
            })(protocols[i]);
        }
        card.appendChild(switchGrid);

        card.appendChild(el('div', { className: 'bowire-landing-help-text bowire-landing-help-text-muted',
            textContent: 'Wrong protocol? Server-side reflection / introspection might not be enabled for ' + currentProtoName + '.' }));

        renderLandingHelpFooter(card);
        parent.appendChild(card);
    }

    // ---- State 2: multi-url-partial ----

    function renderStateMultiUrlPartial(parent) {
        var card = el('div', { className: 'bowire-landing-card' });

        var connected = serverUrls.filter(function (u) { return connectionStatuses[u] === 'connected'; }).length;
        card.appendChild(el('div', { className: 'bowire-landing-section-title',
            textContent: connected + ' of ' + serverUrls.length + ' discovery URLs connected' }));

        var statusList = el('div', { className: 'bowire-landing-status-list' });
        for (var i = 0; i < serverUrls.length; i++) {
            statusList.appendChild(renderUrlStatusRow(serverUrls[i]));
        }
        card.appendChild(statusList);

        card.appendChild(el('div', { className: 'bowire-landing-divider' }));
        card.appendChild(el('div', { className: 'bowire-landing-section-title',
            textContent: 'Pick a method from the sidebar to invoke against any of the connected URLs.' }));

        renderLandingHelpFooter(card);
        parent.appendChild(card);
    }

    // ---- Shared sub-renderers ----

    function renderUrlStatusRow(url) {
        var status = connectionStatuses[url] || 'disconnected';
        var row = el('div', { className: 'bowire-landing-status-row bowire-landing-status-' + status });
        row.appendChild(el('span', { className: 'bowire-landing-status-dot' }));
        row.appendChild(el('span', { className: 'bowire-landing-status-url', textContent: url || '(empty)' }));

        var label = status === 'connected' ? 'Connected'
                  : status === 'connecting' ? 'Connecting…'
                  : status === 'error' ? (discoveryErrors[url] || 'Failed')
                  : 'Disconnected';
        row.appendChild(el('span', { className: 'bowire-landing-status-label', textContent: label }));

        if (status === 'error' || status === 'disconnected') {
            row.appendChild(el('button', {
                className: 'bowire-landing-status-retry',
                textContent: 'Retry',
                onClick: function () { fetchServices(); }
            }));
        }
        return row;
    }

    function renderFirstRunCard(iconName, title, description, ctaLabel, ctaHandler) {
        return el('div', { className: 'bowire-landing-cta-card' },
            el('div', { className: 'bowire-landing-cta-icon', innerHTML: svgIcon(iconName) }),
            el('div', { className: 'bowire-landing-cta-title', textContent: title }),
            el('div', { className: 'bowire-landing-cta-desc', textContent: description }),
            el('button', {
                className: 'bowire-landing-cta-primary',
                onClick: ctaHandler,
                textContent: ctaLabel
            })
        );
    }

    function renderRecentHistoryRow(h) {
        var row = el('button', {
            className: 'bowire-landing-history-row',
            onClick: function () { selectFromHistoryEntry(h); }
        });

        // Method-type badge (Unary / ServerStreaming / ClientStreaming / Duplex)
        if (h.methodType) {
            row.appendChild(el('span', {
                className: 'bowire-landing-history-badge',
                dataset: { type: methodBadgeType({ methodType: h.methodType }) },
                textContent: methodBadgeLabel(h.methodType)
            }));
        }

        row.appendChild(el('span', { className: 'bowire-landing-history-method',
            textContent: (h.service || '') + ' / ' + (h.method || '') }));

        var timeText = getRelativeTime(h.timestamp);
        row.appendChild(el('span', { className: 'bowire-landing-history-time', textContent: timeText }));
        return row;
    }

    function renderTipLine(iconName, text) {
        return el('div', { className: 'bowire-landing-tip' },
            el('span', { className: 'bowire-landing-tip-icon', innerHTML: svgIcon(iconName) }),
            el('span', { className: 'bowire-landing-tip-text', textContent: text })
        );
    }

    function renderLandingHelpFooter(parent) {
        var footer = el('div', { className: 'bowire-landing-footer' });
        footer.appendChild(el('button', {
            className: 'bowire-landing-footer-btn',
            textContent: 'Take the guided tour →',
            onClick: function () {
                if (typeof startTour === 'function') startTour();
            }
        }));
        footer.appendChild(el('a', {
            className: 'bowire-landing-footer-btn',
            href: 'https://bowire.io/docs/',
            target: '_blank',
            rel: 'noopener',
            textContent: 'Open docs →'
        }));
        parent.appendChild(footer);
    }

    // ---- Helpers ----

    function buildConnectedHeadline() {
        // "Connected (embedded)" only makes sense when Bowire is mounted
        // inside the user's own app — services come from the in-process
        // EndpointDataSource scan, not from a URL. In standalone mode an
        // empty serverUrls list just means "the user hasn't pointed at
        // anything yet"; calling that "Connected (embedded)" is wrong
        // and confusing.
        if (serverUrls.length === 0) {
            return uiMode === 'embedded' ? 'Connected (embedded)' : 'Pick a method from the sidebar';
        }
        if (serverUrls.length === 1) return 'Connected to ' + serverUrls[0];
        return 'Connected to ' + serverUrls.length + ' URLs';
    }

    function buildServiceSummary() {
        var serviceCount = services.length;
        var methodCount = 0;
        var protoIds = new Set();
        for (var i = 0; i < services.length; i++) {
            var s = services[i];
            if (Array.isArray(s.methods)) methodCount += s.methods.length;
            if (s.source) protoIds.add(s.source);
        }
        var parts = [];
        parts.push(serviceCount + ' service' + (serviceCount === 1 ? '' : 's'));
        parts.push(methodCount + ' method' + (methodCount === 1 ? '' : 's'));
        if (protoIds.size > 0) {
            var protoNames = [];
            protoIds.forEach(function (id) {
                var p = protocols.find(function (pp) { return pp.id === id; });
                protoNames.push((p && p.name) || id);
            });
            parts.push(protoNames.join(' + '));
        }
        return parts.join(' · ');
    }

    /**
     * Pull the most recent history entries that point at services currently
     * loaded — so the recall list never offers a method the user can't
     * actually click. Returns at most `limit` entries.
     */
    function getRecentHistoryForCurrentServers(limit) {
        var all = getHistory();
        var result = [];
        for (var i = 0; i < all.length && result.length < limit; i++) {
            var h = all[i];
            // Service must exist in the current discovery
            var svc = services.find(function (s) { return s.name === h.service; });
            if (!svc) continue;
            // Method must exist on that service
            if (Array.isArray(svc.methods) && !svc.methods.some(function (m) { return m.name === h.method; })) continue;
            result.push(h);
        }
        return result;
    }

    function selectFromHistoryEntry(h) {
        var svc = services.find(function (s) { return s.name === h.service; });
        if (!svc) return;
        var method = (svc.methods || []).find(function (m) { return m.name === h.method; });
        if (!method) return;
        openTab(svc, method);
    }

    function getRelativeTime(ts) {
        if (!ts) return '';
        var now = Date.now();
        var diff = Math.max(0, now - ts);
        var sec = Math.floor(diff / 1000);
        if (sec < 60) return 'just now';
        var min = Math.floor(sec / 60);
        if (min < 60) return min + ' min ago';
        var hr = Math.floor(min / 60);
        if (hr < 24) return hr + ' hour' + (hr === 1 ? '' : 's') + ' ago';
        var day = Math.floor(hr / 24);
        if (day === 1) return 'yesterday';
        if (day < 7) return day + ' days ago';
        var date = new Date(ts);
        return date.toLocaleDateString();
    }
