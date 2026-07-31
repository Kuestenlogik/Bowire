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

        // 5b. Embedded host whose own /api/services failed. #534 — this
        //     case used to fall straight through to 'first-run' below and
        //     render the welcome hero: an embedded host has no serverUrls
        //     and (usually) no config.lockServerUrl, so check 4 never
        //     fired and the 502 was completely hidden. The failure is
        //     filed under the '(embedded)' key api.js writes for the
        //     URL-less host probe. Scoped to uiMode === 'embedded' on
        //     purpose: in standalone the same key can be set by the
        //     no-URL host probe while the operator is simply between
        //     URLs, and first-run is the right screen for that.
        if (uiMode === 'embedded' && !hasServices && !isLoadingServices
            && discoveryErrors['(embedded)']) {
            return 'discovery-failed';
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
        var state = detectLandingState();
        // #301 followup — for genuinely empty states (no list, no tips,
        // just one empty-card) we host the card in a bowire-main-pad so
        // the shared :has(> .bowire-empty-card:only-child) centring rule
        // kicks in. Discover's welcome then matches Recordings / Mocks /
        // Compose / Flows / Home no-workspace instead of floating
        // left-aligned. Populated states (ready with tips + history,
        // multi-url-partial with status list, &c) keep the bowire-landing
        // shell so their multi-child layout reads as a portal column.
        var emptyOnlyStates = { 'first-run': true };
        var wrapperClass = emptyOnlyStates[state] ? 'bowire-main-pad' : 'bowire-landing';
        var landing = el('div', { className: wrapperClass });
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
            icon: 'discover',
            headline: 'Discover',
            body: bodyText
        }));

        // A partially-faulted probe lands HERE, not on the failure card:
        // it produced services, so detectLandingState correctly classifies
        // it 'ready'. Without this the sidebar fills up and nothing says a
        // server's tools are missing — #544's bug reproduced one layer up.
        // The same collapsed disclosure the failure card uses, so there is
        // one diagnostics surface, not two.
        var degradedKeys = [];
        if (typeof discoveryAttempts !== 'undefined' && typeof urlDiscoveryDegraded === 'function') {
            var keys = Object.keys(discoveryAttempts);
            for (var d = 0; d < keys.length; d++) {
                if (urlDiscoveryDegraded(keys[d])) degradedKeys.push(keys[d]);
            }
        }
        if (degradedKeys.length > 0) {
            card.appendChild(el('div', {
                className: 'bowire-landing-section-title',
                textContent: 'Discovery is incomplete'
            }));
            for (var k = 0; k < degradedKeys.length; k++) {
                var diag = renderDiscoveryDiagnostics(degradedKeys[k]);
                if (diag) card.appendChild(diag);
            }
        }

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

        parent.appendChild(card);
    }

    // ---- State 6: first-run ----

    function renderStateFirstRun(parent) {
        // #291 — first-run welcome moved to the Home rail mode. From
        // Discover we point the operator at Home so the onboarding
        // stays consolidated on one surface. Same shape as every other
        // rail's empty state (icon + headline + body + actions) so
        // first impressions across rails read consistently.
        //
        // #301 followup — empty-card is appended directly to parent
        // (which is the bowire-main-pad wrapper chosen by
        // renderLandingPage for purely empty states) so the shared
        // :has(> .bowire-empty-card:only-child) centring rule fires.
        // The previous bowire-landing-card wrapper would have broken
        // the direct-child selector and left the welcome top-left.
        // #537 — a host with a catalogue has a better first move than
        // "go to Home and find the add button": browse the services it
        // already knows about. The catalogue action takes over `primary`
        // in that case; with no catalogue the card is byte-identical to
        // what it always was. Guarded per symbol — catalogue.js is a
        // later fragment and render() has no try/catch.
        var _catN = (typeof catalogueEntryCount === 'function') ? catalogueEntryCount() : 0;
        var _canBrowseCatalogue = _catN > 0
            && typeof catalogueVisibility === 'function' && catalogueVisibility() === 'editable'
            && typeof openCatalogueBrowserDialog === 'function';
        var _firstRunActions = [];
        if (_canBrowseCatalogue) {
            _firstRunActions.push({
                label: 'Browse catalogue (' + _catN + ')',
                primary: true,
                onClick: function () { openCatalogueBrowserDialog({}); }
            });
        }
        _firstRunActions.push({
            label: 'Open Home',
            primary: !_canBrowseCatalogue,
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
        }, {
            // Discover first-run is the same state Home shows
            // when no URLs / uploads / services exist, so we
            // route the tour CTA through the canonical getting-
            // started walkthrough (workspace → URL → discover →
            // execute) instead of duplicating those steps in a
            // Discover-only variant.
            id: 'bowire-discover-empty-tour-btn',
            label: 'Take a tour',
            onClick: function () {
                if (typeof window !== 'undefined'
                    && typeof window.bowireStartGettingStartedTour === 'function') {
                    window.bowireStartGettingStartedTour({ force: true });
                }
            }
        });

        parent.appendChild(renderEmptyCard({
            icon: 'discover',
            headline: 'Discover is empty',
            body: _canBrowseCatalogue
                ? ((typeof catalogueProviderLabel === 'function' && catalogueProviderLabel())
                    || 'The catalogue') + ' knows about ' + _catN + ' service'
                    + (_catN === 1 ? '' : 's') + ' — pick the ones you want to work with.'
                : 'Pick a workspace and add a URL or schema file from there.',
            actions: _firstRunActions
        }));
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
        // #537 — when the host curated a catalogue, the first tile is
        // "here are the services we already know about", not "go build a
        // workspace". The workspace card keeps its place right after it.
        // With no catalogue this block is skipped and the hero is exactly
        // what it was.
        var heroCatN = (typeof catalogueEntryCount === 'function') ? catalogueEntryCount() : 0;
        if (heroCatN > 0
            && typeof catalogueVisibility === 'function' && catalogueVisibility() === 'editable'
            && typeof openCatalogueBrowserDialog === 'function') {
            grid.appendChild(renderFirstRunCard(
                'server',
                'Browse your service catalogue',
                ((typeof catalogueProviderLabel === 'function' && catalogueProviderLabel())
                    || 'Your catalogue') + ' lists ' + heroCatN + ' service'
                    + (heroCatN === 1 ? '' : 's') + ' this install can reach. Add the ones you need — no URLs to type.',
                'Open catalogue',
                function () { openCatalogueBrowserDialog({}); }
            ));
        }
        grid.appendChild(renderFirstRunCard(
            'layers',
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

        // One key for both the headline and the diagnostics lookup. An
        // embedded host has no serverUrls at all — its failure is filed
        // under the literal '(embedded)' key api.js writes.
        var diagKey = serverUrls[0] || Object.keys(discoveryErrors)[0] || '(embedded)';
        var errMsg = discoveryErrors[diagKey] || Object.values(discoveryErrors)[0] || 'Connection failed';
        var titleTarget = diagKey === '(embedded)' ? 'this host' : diagKey;

        card.appendChild(el('div', { className: 'bowire-landing-error-icon', innerHTML: svgIcon('disconnect') }));
        card.appendChild(el('div', { className: 'bowire-landing-error-title',
            textContent: 'Could not discover services at ' + titleTarget }));
        card.appendChild(el('div', { className: 'bowire-landing-error-message', textContent: errMsg }));

        var diag = renderDiscoveryDiagnostics(diagKey);
        if (diag) {
            // A concrete per-plugin list strictly dominates the generic
            // advice below, so we show one or the other — never both.
            card.appendChild(diag);
            parent.appendChild(card);
            return;
        }

        var listTitle = el('div', { className: 'bowire-landing-section-title',
            textContent: 'Common causes:' });
        card.appendChild(listTitle);

        var bullets = el('ul', { className: 'bowire-landing-troubleshoot' });
        bullets.appendChild(el('li', { textContent: 'gRPC server: enable Grpc.AspNetCore.Server.Reflection' }));
        bullets.appendChild(el('li', { textContent: 'REST server: ensure /swagger.json or /openapi.json is reachable' }));
        bullets.appendChild(el('li', { textContent: 'GraphQL server: __schema introspection must not be disabled' }));
        bullets.appendChild(el('li', { textContent: 'Network: server is reachable from this machine' }));
        card.appendChild(bullets);

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

        parent.appendChild(card);
    }

    // ---- Shared sub-renderers ----

    // #534 — per-plugin discovery diagnostics.
    //
    // Before this, a discovery that came back empty told the operator
    // "HTTP 502 Bad Gateway" and nothing else: which of the twelve loaded
    // plugins even got a turn, which one refused the connection, which one
    // ran fine and simply didn't recognise the URL — all of that was in the
    // problem+json body the UI never read. This renders it.
    //
    // Collapsed by default (one line: "12 plugins probed · 3 failed") so
    // the failure card doesn't become a wall of text; the Sources rail
    // passes forceOpen because that pane IS the diagnosis surface.
    //
    // Returns null when there is nothing to show. render() has no
    // try/catch — a throw here blanks the entire workbench — so every
    // read is defensive and every caller must tolerate null.
    function renderDiscoveryDiagnostics(key, opts) {
        if (!key) return null;
        if (typeof discoveryAttempts === 'undefined') return null;
        var attempts = discoveryAttempts[key];
        if (!Array.isArray(attempts) || attempts.length === 0) return null;

        var forceOpen = !!(opts && opts.forceOpen);
        var open = forceOpen
            || (typeof discoveryDiagnosticsOpen !== 'undefined' && discoveryDiagnosticsOpen.has(key));
        var failed = attempts.filter(_diagIsFailure).length;
        var degraded = attempts.filter(_diagIsPartial).length;

        var wrap = el('div', {
            id: 'bowire-diag-' + _diagKeySlug(key),
            className: 'bowire-diag'
        });

        if (!forceOpen) {
            // MORPHDOM: the key is read off the element at CLICK time, not
            // captured in this closure. morphdom preserves the existing
            // node (and its listener) while copying the new node's
            // attributes, so a closure would keep toggling whichever URL
            // this row rendered for FIRST — wrong the moment the status
            // list reorders or the operator switches source.
            wrap.appendChild(el('button', {
                className: 'bowire-diag-toggle' + (open ? ' is-open' : ''),
                'data-bowire-diag-key': key,
                'aria-expanded': open ? 'true' : 'false',
                onClick: function (e) {
                    var k = e.currentTarget.getAttribute('data-bowire-diag-key');
                    if (!k) return;
                    if (discoveryDiagnosticsOpen.has(k)) discoveryDiagnosticsOpen.delete(k);
                    else discoveryDiagnosticsOpen.add(k);
                    render();
                }
            },
                el('span', { className: 'bowire-diag-toggle-chevron', innerHTML: svgIcon('chevron') }),
                el('span', {
                    textContent: attempts.length + ' plugin' + (attempts.length === 1 ? '' : 's')
                        + ' probed'
                        + (degraded > 0 ? ' · ' + degraded + ' degraded' : '')
                        + ' · ' + failed + ' failed'
                })
            ));
        }

        if (!open) return wrap;

        var list = el('div', { className: 'bowire-diag-list' });
        var ordered = attempts.slice().sort(function (a, b) {
            return _diagOutcomeRank(a) - _diagOutcomeRank(b);
        });
        for (var i = 0; i < ordered.length; i++) {
            list.appendChild(_renderDiagRow(ordered[i]));
        }
        wrap.appendChild(list);

        var hint = (typeof discoveryHints !== 'undefined') ? discoveryHints[key] : null;
        if (hint) {
            wrap.appendChild(el('div', { className: 'bowire-diag-hint', textContent: hint }));
        }

        // One-click "paste this into the bug report" — the whole table as
        // plain text, which is what an operator actually needs to hand to
        // whoever owns the server.
        wrap.appendChild(el('button', {
            className: 'bowire-diag-copy',
            'data-bowire-diag-key': key,
            onClick: function (e) {
                var k = e.currentTarget.getAttribute('data-bowire-diag-key');
                if (!k) return;
                if (!navigator.clipboard || typeof navigator.clipboard.writeText !== 'function') return;
                navigator.clipboard.writeText(serializeDiscoveryDiagnostics(k)).then(function () {
                    if (typeof toast === 'function') toast('Diagnostics copied', 'success');
                });
            }
        },
            el('span', { innerHTML: svgIcon('copy') }),
            el('span', { textContent: 'Copy diagnostics' })
        ));

        return wrap;
    }

    function _diagIsFailure(a) {
        return a && (a.outcome === 'error' || a.outcome === 'timeout');
    }

    // Deliberately NOT folded into _diagIsFailure: a partial probe did
    // contribute services, so counting it as failed next to a populated
    // tree reads as a bug in the workbench. It is its own term (#544).
    function _diagIsPartial(a) {
        return !!(a && a.outcome === 'partial');
    }

    // Failures first — the reason a discovery came back empty should be
    // the first row, not buried under nine "returned no services" lines.
    // `partial` sits above `empty` for the same reason: it is closer to
    // what the operator opened this list for.
    function _diagOutcomeRank(a) {
        var o = (a && a.outcome) || '';
        if (o === 'error') return 0;
        if (o === 'timeout') return 1;
        if (o === 'partial') return 2;
        if (o === 'empty') return 3;
        return 4;
    }

    // Stable element id per key so morphdom keys the subtree by id rather
    // than by sibling position.
    function _diagKeySlug(key) {
        return String(key).replace(/[^a-zA-Z0-9]+/g, '-');
    }

    function _renderDiagRow(a) {
        var outcome = (a && a.outcome) || 'error';
        var msg = String((a && a.message) || '');
        var shown = msg.length > 160 ? (msg.slice(0, 159) + '…') : msg;
        var row = el('div', { className: 'bowire-diag-row' },
            el('span', { className: 'bowire-diag-dot bowire-diag-' + outcome, title: outcome }),
            el('span', { className: 'bowire-diag-plugin', textContent: (a && a.plugin) || 'plugin' }),
            el('span', { className: 'bowire-diag-msg', textContent: shown, title: msg || outcome })
        );
        if (a && typeof a.durationMs === 'number') {
            row.appendChild(el('span', {
                className: 'bowire-diag-dur',
                textContent: a.durationMs + ' ms'
            }));
        }

        // Per-step breakdown (#544): one line per faulted MCP surface, one
        // per well-known path a REST sweep tried. Only present when the
        // plugin implements the diagnostics seam — everything else keeps
        // rendering exactly the single-line row it always did. Rendered
        // as a sibling block so the row's flex layout is untouched.
        var details = (a && Array.isArray(a.details)) ? a.details : null;
        if (!details || details.length === 0) return row;
        // A joined message already repeats a single detail line verbatim.
        if (details.length === 1 && msg.indexOf(String(details[0])) >= 0) return row;

        var group = el('div', { className: 'bowire-diag-row-group' }, row);
        var list = el('div', { className: 'bowire-diag-details' });
        for (var i = 0; i < details.length; i++) {
            list.appendChild(el('div', {
                className: 'bowire-diag-detail',
                textContent: String(details[i])
            }));
        }
        group.appendChild(list);
        return group;
    }

    // Plain-text rendering of one key's attempt table, for bug reports.
    function serializeDiscoveryDiagnostics(key) {
        if (typeof discoveryAttempts === 'undefined') return '';
        var attempts = discoveryAttempts[key];
        if (!Array.isArray(attempts) || attempts.length === 0) return '';
        var lines = ['Bowire discovery diagnostics for ' + key];
        var err = (typeof discoveryErrors !== 'undefined') ? discoveryErrors[key] : null;
        if (err) lines.push('Result: ' + err);
        lines.push('');
        var ordered = attempts.slice().sort(function (a, b) {
            return _diagOutcomeRank(a) - _diagOutcomeRank(b);
        });
        for (var i = 0; i < ordered.length; i++) {
            var a = ordered[i];
            lines.push('  ' + (a.plugin || 'plugin')
                + '  ' + (a.outcome || '?')
                + '  ' + (typeof a.durationMs === 'number' ? a.durationMs + ' ms' : '')
                + '  ' + (a.message || ''));
            // The breakdown belongs in the bug report too — it names the
            // exact surface / path, which the joined message may not.
            if (Array.isArray(a.details)) {
                for (var d = 0; d < a.details.length; d++) {
                    lines.push('      ' + String(a.details[d]));
                }
            }
        }
        var hint = (typeof discoveryHints !== 'undefined') ? discoveryHints[key] : null;
        if (hint) { lines.push(''); lines.push('Hint: ' + hint); }
        return lines.join('\n');
    }

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

        // Attach the per-plugin disclosure under the row when this URL has
        // one. Both call sites only appendChild the return value, and every
        // .bowire-landing-status-* CSS rule is a flat class selector with no
        // child combinator, so wrapping is safe.
        var diag = renderDiscoveryDiagnostics(url);
        if (!diag) return row;
        var wrap = el('div', { className: 'bowire-landing-status-entry' });
        wrap.appendChild(row);
        wrap.appendChild(diag);
        return wrap;
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

    // Guided-tour + Open-docs footer. Moved out of the per-state
    // Discover landing cards (they were appearing under every empty
    // state; once is enough) and into the Home portal — Home is the
    // welcome surface, that's where onboarding affordances belong.
    // Kept exported so other callers can mount the same footer
    // without re-implementing the smart docs branch.
    function renderLandingHelpFooter(parent) {
        var footer = el('div', { className: 'bowire-landing-footer' });
        footer.appendChild(el('button', {
            className: 'bowire-landing-footer-btn',
            textContent: 'Take the guided tour →',
            onClick: function () {
                if (typeof startTour === 'function') startTour();
            }
        }));
        // "Open docs" prefers the local NuGet-installed help drawer
        // when present — that's what `helpAvailable` (boot capability
        // probe) reports. Falls back to bowire.io/docs when the
        // package isn't installed so the link still works.
        var localDocs = typeof helpAvailable !== 'undefined' && helpAvailable;
        if (localDocs && typeof helpOpenDrawer === 'function') {
            footer.appendChild(el('button', {
                className: 'bowire-landing-footer-btn',
                textContent: 'Open docs →',
                title: 'Open the in-app help drawer (F1)',
                onClick: function () { helpOpenDrawer(); }
            }));
        } else {
            footer.appendChild(el('a', {
                className: 'bowire-landing-footer-btn',
                href: 'https://bowire.io/docs/',
                target: '_blank',
                rel: 'noopener',
                textContent: 'Open docs →'
            }));
        }
        parent.appendChild(footer);
    }
    if (typeof window !== 'undefined') {
        window.bowireRenderLandingHelpFooter = renderLandingHelpFooter;
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
        // Count only services that actually carry methods — the
        // OpenAPI discovery sometimes emits a host-only "service"
        // (the API root) with no operations, which would inflate the
        // count past what the user sees in the tree.
        var serviceCount = 0;
        var methodCount = 0;
        var protoIds = new Set();
        for (var i = 0; i < services.length; i++) {
            var s = services[i];
            if (Array.isArray(s.methods) && s.methods.length > 0) {
                serviceCount++;
                methodCount += s.methods.length;
            }
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
