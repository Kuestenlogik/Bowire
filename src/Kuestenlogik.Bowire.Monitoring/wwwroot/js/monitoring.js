    // ---- Monitoring (#102) ----
    // Read-only workbench surface over the probe outcome ledger. Probes are
    // authored as files and run by `bowire monitor run` (usually a separate
    // long-lived process); this rail only reads what that process appends to
    // ~/.bowire/monitoring/<probe>.jsonl via /api/monitoring/*. No mutation,
    // no workspace dependency — the ledger root is machine-global.
    //
    // State shape:
    //   monitoringProbes       — overview rows [{ name, last, history[] }]
    //   monitoringSelectedName — probe whose detail pane is open (null = overview)
    //   monitoringDetail       — { name, outcomes[] } for the selected probe
    // Data flows one way: the fetch helpers below write state and call
    // render(); the renderers only read (no state mutators in the render
    // path). A slow poll keeps the surface live against the appending
    // monitor process — probe cadences are seconds-to-minutes, so 10s is
    // plenty and cheap.

    var monitoringProbes = [];
    var monitoringSelectedName = null;
    var monitoringDetail = null;
    var monitoringLoaded = false;
    var _monitoringFetching = false;
    var _monitoringTimer = null;

    function refreshMonitoring() {
        if (_monitoringFetching) return;
        _monitoringFetching = true;
        var overview = fetch(config.prefix + '/api/monitoring/probes')
            .then(function (r) { return r.ok ? r.json() : { probes: [] }; })
            .then(function (data) {
                monitoringProbes = Array.isArray(data.probes) ? data.probes : [];
                if (monitoringSelectedName
                    && !monitoringProbes.some(function (p) { return p.name === monitoringSelectedName; })) {
                    monitoringSelectedName = null;
                    monitoringDetail = null;
                }
            });
        var detail = monitoringSelectedName
            ? fetch(config.prefix + '/api/monitoring/probes/'
                    + encodeURIComponent(monitoringSelectedName) + '/outcomes?limit=200')
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    // Only accept the row set if the selection didn't move
                    // while the request was in flight.
                    if (data && data.name === monitoringSelectedName) monitoringDetail = data;
                })
            : Promise.resolve();
        Promise.all([overview, detail])
            .catch(function () { /* endpoint unreachable — keep the last snapshot */ })
            .then(function () {
                _monitoringFetching = false;
                monitoringLoaded = true;
                if (railMode === 'monitoring') render();
            });
    }

    function _ensureMonitoringPoll() {
        if (_monitoringTimer !== null) return;
        _monitoringTimer = setInterval(function () {
            if (railMode !== 'monitoring') return;
            if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
            refreshMonitoring();
        }, 10000);
    }

    function selectMonitoringProbe(name) {
        monitoringSelectedName = name;
        monitoringDetail = null;
        refreshMonitoring();
        render();
    }

    // Result → reserved status slot. Never color-alone: every use pairs the
    // color with the result word (chip label, table cell, tooltip).
    //   pass  → success   fail → error   error (probe couldn't run) → warning
    function _monitoringResultClass(result) {
        if (result === 'pass') return 'is-pass';
        if (result === 'fail') return 'is-fail';
        return 'is-error';
    }

    function _monitoringTimeOf(outcome) {
        try {
            return new Date(outcome.t).toLocaleTimeString();
        } catch (_) {
            return '';
        }
    }

    function _monitoringLatencyLabel(outcome) {
        if (!outcome) return '';
        if (outcome.result === 'error') return 'error';
        return Math.round(outcome.latencyMs) + 'ms';
    }

    // Latency sparkline over the outcome tail, one thin bar per run anchored
    // to the baseline; bar color carries the run's status. Native tooltip per
    // bar is the hover layer (time · result · latency). Errors have no
    // latency measurement — they render as full-height warning bars so a
    // "couldn't run" phase stays visible instead of vanishing at 0px.
    function renderMonitoringSparkline(history, opts) {
        opts = opts || {};
        var w = opts.width || 300;
        var h = opts.height || 32;
        var svgNs = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(svgNs, 'svg');
        svg.setAttribute('class', 'bowire-mon-spark');
        svg.setAttribute('width', String(w));
        svg.setAttribute('height', String(h));
        svg.setAttribute('role', 'img');
        svg.setAttribute('aria-label', 'Latency per run, newest right');
        var rows = Array.isArray(history) ? history : [];
        if (rows.length === 0) return svg;
        var barW = 3, gap = 2;
        var maxBars = Math.floor((w + gap) / (barW + gap));
        if (rows.length > maxBars) rows = rows.slice(rows.length - maxBars);
        var maxLatency = 0;
        rows.forEach(function (o) {
            if (o.result !== 'error' && o.latencyMs > maxLatency) maxLatency = o.latencyMs;
        });
        rows.forEach(function (o, i) {
            var isErr = o.result === 'error';
            var barH = isErr
                ? h
                : Math.max(3, maxLatency > 0 ? Math.round((o.latencyMs / maxLatency) * (h - 2)) : 3);
            var rect = document.createElementNS(svgNs, 'rect');
            rect.setAttribute('x', String(i * (barW + gap)));
            rect.setAttribute('y', String(h - barH));
            rect.setAttribute('width', String(barW));
            rect.setAttribute('height', String(barH));
            rect.setAttribute('rx', '1');
            rect.setAttribute('class', 'bowire-mon-spark-bar ' + _monitoringResultClass(o.result));
            var tip = document.createElementNS(svgNs, 'title');
            tip.textContent = _monitoringTimeOf(o) + ' · ' + o.result
                + (isErr ? (o.error ? ' · ' + o.error : '') : ' · ' + Math.round(o.latencyMs) + 'ms');
            rect.appendChild(tip);
            svg.appendChild(rect);
        });
        return svg;
    }

    function _renderMonitoringStatusChip(outcome) {
        var result = outcome ? outcome.result : 'never ran';
        return el('span', {
            className: 'bowire-mon-chip ' + (outcome ? _monitoringResultClass(result) : 'is-none'),
            textContent: result
        });
    }

    function renderMonitoringSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        _ensureMonitoringPoll();
        if (!monitoringLoaded && !_monitoringFetching) refreshMonitoring();

        sidebar.appendChild(renderSidebarToolbar({
            title: 'Monitoring',
            onTitleClick: monitoringSelectedName !== null ? function () {
                monitoringSelectedName = null;
                monitoringDetail = null;
                render();
            } : undefined,
            titleClickTitle: 'Back to the probe overview',
            actions: [
                { icon: 'repeat', title: 'Refresh now', onClick: function () { refreshMonitoring(); } }
            ]
        }));

        if (monitoringProbes.length === 0) {
            sidebar.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: monitoringLoaded ? 'No probe outcomes yet.' : 'Loading probes…'
            }));
            return sidebar;
        }

        var list = el('div', { id: 'bowire-monitoring-list', className: 'bowire-env-list' });
        monitoringProbes.forEach(function (p) {
            var last = p.last;
            var accent = !last ? 'var(--bowire-text-tertiary)'
                : last.result === 'pass' ? 'var(--bowire-success)'
                : last.result === 'fail' ? 'var(--bowire-error)'
                : 'var(--bowire-warning)';
            list.appendChild(renderSidebarListItem({
                id: 'bowire-mon-row-' + p.name,
                accent: accent,
                name: p.name,
                meta: _monitoringLatencyLabel(last),
                selected: p.name === monitoringSelectedName,
                onClick: function () { selectMonitoringProbe(p.name); }
            }));
        });
        sidebar.appendChild(list);
        return sidebar;
    }

    function _renderMonitoringEmpty() {
        // View-keyed id — morphdom replaces the subtree wholesale when the
        // view changes (loading → empty → overview → detail) instead of
        // position-morphing div-into-div, which would strip the freshly
        // attached click listeners off the incoming nodes.
        var pad = el('div', {
            id: 'bowire-mon-view-' + (monitoringLoaded ? 'empty' : 'loading'),
            className: 'bowire-main-pad'
        });
        pad.appendChild(renderEmptyCard({
            icon: 'pulse',
            headline: monitoringLoaded ? 'No probe outcomes yet' : 'Loading probe ledger…',
            body: 'Monitoring renders the outcome ledger that `bowire monitor run <probes>` writes. '
                + 'Start a monitor process against your probe file — every run lands in '
                + '~/.bowire/monitoring and shows up here live: status, latency sparkline, and '
                + 'the full outcome history per probe.',
            actions: [
                { label: 'Refresh', primary: true, onClick: function () { refreshMonitoring(); } }
            ]
        }));
        return pad;
    }

    function _renderMonitoringOverview() {
        var wrap = el('div', { id: 'bowire-mon-view-overview', className: 'bowire-main-pad' });
        var grid = el('div', { className: 'bowire-mon-grid' });
        monitoringProbes.forEach(function (p) {
            // Stable per-probe id so morphdom keys cards by identity — a
            // probe appearing/disappearing must not shift another card
            // onto a node whose closure captured a different name.
            var card = el('div', {
                id: 'bowire-mon-card-' + p.name,
                className: 'bowire-mon-card',
                role: 'button',
                tabIndex: 0,
                onClick: function () { selectMonitoringProbe(p.name); }
            });
            var head = el('div', { className: 'bowire-mon-card-head' });
            head.appendChild(el('span', { className: 'bowire-mon-card-name', textContent: p.name }));
            head.appendChild(_renderMonitoringStatusChip(p.last));
            card.appendChild(head);
            card.appendChild(renderMonitoringSparkline(p.history, { width: 280, height: 32 }));
            if (p.last) {
                card.appendChild(el('div', {
                    className: 'bowire-mon-card-meta',
                    textContent: 'Last run ' + _monitoringTimeOf(p.last) + ' · ' + _monitoringLatencyLabel(p.last)
                }));
            }
            grid.appendChild(card);
        });
        wrap.appendChild(grid);
        return wrap;
    }

    function _renderMonitoringDetail() {
        var wrap = el('div', {
            id: 'bowire-mon-view-detail-' + monitoringSelectedName,
            className: 'bowire-main-pad'
        });
        var outcomes = (monitoringDetail && Array.isArray(monitoringDetail.outcomes))
            ? monitoringDetail.outcomes : [];
        var last = outcomes.length > 0 ? outcomes[outcomes.length - 1] : null;

        var head = el('div', { className: 'bowire-mon-detail-head' });
        head.appendChild(el('h2', { className: 'bowire-mon-detail-name', textContent: monitoringSelectedName }));
        head.appendChild(_renderMonitoringStatusChip(last));
        if (last) {
            head.appendChild(el('span', {
                className: 'bowire-mon-card-meta',
                textContent: 'Last run ' + _monitoringTimeOf(last) + ' · ' + _monitoringLatencyLabel(last)
            }));
        }
        wrap.appendChild(head);
        wrap.appendChild(renderMonitoringSparkline(outcomes, { width: 640, height: 48 }));

        // Outcome table, newest first. Detail column carries the error text
        // or the failed-assertion descriptions so a red row explains itself.
        var table = el('table', { className: 'bowire-mon-table' });
        var thead = el('thead', {});
        var headRow = el('tr', {});
        ['Time', 'Result', 'Latency', 'Detail'].forEach(function (label) {
            headRow.appendChild(el('th', { textContent: label }));
        });
        thead.appendChild(headRow);
        table.appendChild(thead);
        var tbody = el('tbody', {});
        outcomes.slice().reverse().forEach(function (o) {
            var tr = el('tr', {});
            tr.appendChild(el('td', { textContent: _monitoringTimeOf(o) }));
            tr.appendChild(el('td', {}, el('span', {
                className: 'bowire-mon-chip ' + _monitoringResultClass(o.result),
                textContent: o.result
            })));
            tr.appendChild(el('td', { textContent: o.result === 'error' ? '—' : Math.round(o.latencyMs) + 'ms' }));
            var detailText = o.error
                || (Array.isArray(o.assertions)
                    ? o.assertions.filter(function (a) { return a && a.passed === false; })
                        .map(function (a) { return a.description; }).join('; ')
                    : '');
            tr.appendChild(el('td', { className: 'bowire-mon-table-detail', textContent: detailText }));
            tbody.appendChild(tr);
        });
        table.appendChild(tbody);
        wrap.appendChild(table);
        return wrap;
    }

    function renderMonitoringMain() {
        var main = el('div', { id: 'bowire-main-monitoring', className: 'bowire-main bowire-main-monitoring' });
        _ensureMonitoringPoll();
        if (!monitoringLoaded && !_monitoringFetching) refreshMonitoring();

        if (monitoringProbes.length === 0) {
            main.appendChild(_renderMonitoringEmpty());
        } else if (monitoringSelectedName !== null) {
            main.appendChild(_renderMonitoringDetail());
        } else {
            main.appendChild(_renderMonitoringOverview());
        }
        return main;
    }

    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.monitoringSidebar = renderMonitoringSidebar;
        window.__bowireRailRenderers.monitoringMain = renderMonitoringMain;
    }
