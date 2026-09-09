    // ---- #189 — Design-time Lint rail ----
    //
    // The browser twin of `bowire lint`: POST the currently discovered services
    // to /api/lint (server-side BowireSchemaLinter, honouring .bowire/rules.json)
    // and list the findings grouped by severity. No sidebar — the findings live
    // in the main pane. State is rebuilt from `_lintState` on every render() so
    // there is no captured, morphdom-stale container.

    var _lintState = { loading: false, error: null, findings: null, summary: null };

    function _lintSeverityRank(sev) {
        var order = { High: 0, Medium: 1, Low: 2, Info: 3 };
        return (sev in order) ? order[sev] : 4;
    }

    function runLint() {
        _lintState.loading = true;
        _lintState.error = null;
        if (typeof render === 'function') render();

        fetch(config.prefix + '/api/lint', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ services: (typeof services !== 'undefined' && services) ? services : [] })
        })
            .then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            })
            .then(function (data) {
                _lintState.loading = false;
                _lintState.findings = Array.isArray(data.findings) ? data.findings : [];
                _lintState.summary = data.summary || null;
                if (typeof render === 'function') render();
            })
            .catch(function (e) {
                _lintState.loading = false;
                _lintState.error = (e && e.message) || 'lint request failed';
                if (typeof render === 'function') render();
            });
    }

    function _renderLintRow(f) {
        var loc = f.service + (f.method ? '.' + f.method : '') + (f.field ? '.' + f.field : '');
        var sev = f.severity || 'Info';
        var row = el('div', { className: 'bowire-lint-row bowire-lint-' + sev.toLowerCase() });
        row.appendChild(el('span', { className: 'bowire-lint-sev', textContent: sev.toUpperCase() }));
        var body = el('div', { className: 'bowire-lint-body' });
        body.appendChild(el('div', { className: 'bowire-lint-loc' },
            el('code', { textContent: loc }),
            el('span', { className: 'bowire-lint-rule', textContent: f.ruleId })));
        body.appendChild(el('div', { className: 'bowire-lint-msg', textContent: f.message }));
        row.appendChild(body);
        return row;
    }

    function _renderLintResults() {
        if (_lintState.loading) {
            return el('p', { className: 'bowire-pane-empty', textContent: t('lint.running') });
        }
        if (_lintState.error) {
            return el('p', { className: 'bowire-pane-empty',
                textContent: t('lint.failed', { reason: _lintState.error }) });
        }
        if (!_lintState.findings) {
            return el('p', { className: 'bowire-pane-empty', textContent: t('lint.idle') });
        }

        var findings = _lintState.findings.slice().sort(function (a, b) {
            return _lintSeverityRank(a.severity) - _lintSeverityRank(b.severity);
        });
        if (findings.length === 0) {
            return el('p', { className: 'bowire-pane-empty', textContent: t('lint.clean') });
        }

        var wrap = el('div', {});
        var s = _lintState.summary || {};
        wrap.appendChild(el('p', {
            className: 'bowire-lint-summary',
            // #688 - two keys and a conditional until the layer can express a
            // plural. Not two messages: one message, two shapes.
            textContent: t(findings.length === 1 ? 'lint.summary.one' : 'lint.summary.many', {
                count: findings.length,
                high: s.high || 0, medium: s.medium || 0, low: s.low || 0
            })
        }));
        var listEl = el('div', { className: 'bowire-lint-list' });
        findings.forEach(function (f) { listEl.appendChild(_renderLintRow(f)); });
        wrap.appendChild(listEl);
        return wrap;
    }

    function renderLintMain() {
        var main = el('div', { id: 'bowire-main-lint', className: 'bowire-main bowire-main-lint' });
        var pad = el('div', { className: 'bowire-main-pad' });

        pad.appendChild(el('h1', { className: 'bowire-pane-title', textContent: t('lint.title') }));
        pad.appendChild(el('p', {
            className: 'bowire-lint-sub',
            textContent: t('lint.lede')
        }));

        var count = (typeof services !== 'undefined' && services) ? services.length : 0;
        var runBtn = el('button', { className: 'bowire-btn bowire-btn-primary', type: 'button', textContent: t('lint.run') });
        runBtn.addEventListener('click', runLint);
        pad.appendChild(el('div', { className: 'bowire-lint-controls' },
            runBtn,
            el('span', { className: 'bowire-lint-count', textContent: count + ' service' + (count === 1 ? '' : 's') + ' discovered' })));

        pad.appendChild(_renderLintResults());

        // Auto-run once when services are already present, so opening the rail
        // shows findings without an extra click. Deferred so it never re-enters
        // the render it was called from.
        if (count > 0 && !_lintState.findings && !_lintState.loading && !_lintState.error) {
            setTimeout(runLint, 0);
        }

        main.appendChild(pad);
        return main;
    }

    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.lintMain = renderLintMain;
    }
