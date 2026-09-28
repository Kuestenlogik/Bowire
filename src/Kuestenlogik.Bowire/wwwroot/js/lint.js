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

    // #583 — `background` is the run after a discovery: nobody is looking at
    // the rail, so no "running…" paint first, only the result.
    function runLint(background) {
        _lintState.loading = true;
        _lintState.error = null;
        if (!background && typeof render === 'function') render();

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

    // ---- #583 — findings where the method is ----
    //
    // The rail lists findings; a person reading a method should not have to
    // go there to learn that its response carries a password. So the linter
    // also runs after every discovery, quietly, and the result feeds three
    // places: a pill on the method's sidebar row, a strip under the method's
    // header, and the rail. One run, one result — the three never disagree.

    var _lintRunSignature = null;
    var _lintTimer = null;
    var _lintHintsOpen = new Set();

    /// A cheap fingerprint of the discovered surface, so an unchanged
    /// rediscovery (a reload, a second URL that adds nothing) does not lint
    /// the same thing again.
    function _servicesSignature(list) {
        var text = JSON.stringify(list || []);
        var h = 5381;
        for (var i = 0; i < text.length; i++) h = ((h << 5) + h + text.charCodeAt(i)) | 0;
        return text.length + ':' + h;
    }

    /// Called at the end of every discovery pass. Debounced: several URLs
    /// finishing close together are one surface, and one lint.
    function scheduleLint() {
        if (typeof services === 'undefined' || !services || services.length === 0) return;
        if (_lintTimer) clearTimeout(_lintTimer);
        _lintTimer = setTimeout(function () {
            _lintTimer = null;
            var sig = _servicesSignature(services);
            if (sig === _lintRunSignature && _lintState.findings) return;
            _lintRunSignature = sig;
            runLint(true);
        }, 400);
    }

    /// The findings on one method, worst first. Service-level findings (no
    /// method) belong to the rail — pinned to every method they would be
    /// noise on all of them.
    function lintFindingsFor(svcName, methodName) {
        var all = _lintState.findings;
        if (!all || !svcName || !methodName) return [];
        return all.filter(function (f) { return f.service === svcName && f.method === methodName; })
            .sort(function (a, b) { return _lintSeverityRank(a.severity) - _lintSeverityRank(b.severity); });
    }

    /// Per method: how many findings reach Low or worse, and the worst. The
    /// sidebar pill reads this. Info stays out of it on purpose — a naming
    /// nit on half the rows would teach people to ignore the pill.
    function lintIndex() {
        var idx = new Map();
        (_lintState.findings || []).forEach(function (f) {
            if (!f.method || _lintSeverityRank(f.severity) > _lintSeverityRank('Low')) return;  // i18n-exempt: the severity token the server sends, not a label
            var key = f.service + '|' + f.method;
            var e = idx.get(key) || { count: 0, worst: 'Low' };
            e.count++;
            if (_lintSeverityRank(f.severity) < _lintSeverityRank(e.worst)) e.worst = f.severity;
            idx.set(key, e);
        });
        return idx;
    }

    /// Open the method a finding is about — from the rail, or from the pill.
    function openLintedMethod(svcName, methodName) {
        if (typeof services === 'undefined' || !services) return;
        var svc = services.find(function (s) { return s.name === svcName; });
        var m = svc && (svc.methods || []).find(function (x) { return x.name === methodName; });
        if (!svc || !m) return;
        railMode = 'discover';
        try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
        _lintHintsOpen.add(svcName + '|' + methodName);
        openTab(svc, m);
        if (typeof render === 'function') render();
    }

    /// The strip under a method's header: collapsed to one line naming how
    /// many findings and how bad, open to the list. Null when there are none,
    /// so a clean method looks exactly as it did.
    function renderLintHints(svcName, methodName) {
        var list = lintFindingsFor(svcName, methodName);
        if (list.length === 0) return null;
        var key = svcName + '|' + methodName;
        var open = _lintHintsOpen.has(key);
        var worst = (list[0].severity || 'Info').toLowerCase();
        var strip = el('div', { className: 'bowire-lint-hints bowire-lint-hints-' + worst + (open ? ' open' : '') });
        strip.appendChild(el('button', {
            type: 'button',
            className: 'bowire-lint-hints-toggle',
            'aria-expanded': open ? 'true' : 'false',
            onClick: function () {
                if (_lintHintsOpen.has(key)) _lintHintsOpen.delete(key); else _lintHintsOpen.add(key);
                if (typeof render === 'function') render();
            }
        },
            el('span', { className: 'bowire-lint-sev', textContent: (list[0].severity || 'Info').toUpperCase() }),
            el('span', {
                className: 'bowire-lint-hints-title',
                textContent: t(list.length === 1 ? 'lint.hints.one' : 'lint.hints.many', { count: list.length })
            })));
        if (open) {
            var body = el('div', { className: 'bowire-lint-hints-body' });
            list.forEach(function (f) {
                body.appendChild(el('div', { className: 'bowire-lint-hint bowire-lint-' + (f.severity || 'Info').toLowerCase() },
                    el('span', { className: 'bowire-lint-sev', textContent: (f.severity || 'Info').toUpperCase() }),
                    f.field ? el('code', { className: 'bowire-lint-hint-field', textContent: f.field }) : null,
                    el('span', { className: 'bowire-lint-msg', textContent: f.message }),
                    el('span', { className: 'bowire-lint-rule', textContent: f.ruleId })));
            });
            body.appendChild(el('button', {
                type: 'button',
                className: 'bowire-lint-hints-rail',
                textContent: t('lint.hints.openRail'),
                onClick: function () {
                    railMode = 'lint';
                    try { localStorage.setItem('bowire_rail_mode', 'lint'); } catch { /* ignore */ }
                    if (typeof render === 'function') render();
                }
            }));
            strip.appendChild(body);
        }
        return strip;
    }

    function _renderLintRow(f) {
        var loc = f.service + (f.method ? '.' + f.method : '') + (f.field ? '.' + f.field : '');
        var sev = f.severity || 'Info';
        // #583 — the docs always said a finding is "clickable through to the
        // method it fired on"; the row had no handler. A method-level finding
        // now is. A service-level one has nowhere more specific to go.
        var row = el('div', {
            className: 'bowire-lint-row bowire-lint-' + sev.toLowerCase() + (f.method ? ' clickable' : ''),
            role: f.method ? 'button' : null,
            tabindex: f.method ? '0' : null,
            onClick: f.method ? function () { openLintedMethod(f.service, f.method); } : null,
            onKeydown: f.method ? function (e) {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openLintedMethod(f.service, f.method); }
            } : null
        });
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
        runBtn.addEventListener('click', function () { runLint(false); });
        pad.appendChild(el('div', { className: 'bowire-lint-controls' },
            runBtn,
            // #688 - one message, two shapes.
el('span', { className: 'bowire-lint-count',
    textContent: t(count === 1 ? 'lint.discoveredOne' : 'lint.discoveredMany',
        { count: count }) })));

        pad.appendChild(_renderLintResults());

        // Auto-run once when services are already present, so opening the rail
        // shows findings without an extra click. Deferred so it never re-enters
        // the render it was called from.
        if (count > 0 && !_lintState.findings && !_lintState.loading && !_lintState.error) {
            setTimeout(function () { runLint(false); }, 0);
        }

        main.appendChild(pad);
        return main;
    }

    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.lintMain = renderLintMain;
    }
