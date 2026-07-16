    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // #306 / #314 — Security rail JS fragment.
    //
    // The sidebar is a title + hint; the main pane hosts the OWASP API
    // Top 10 suite (#173) and the endpoint spider (#176), both served by
    // this package's /api/security/* endpoints, plus the AI security
    // panel when Kuestenlogik.Bowire.Ai is in the process. Styling uses
    // the shared `bowire-secsuite-*` / `bowire-btn` / `bowire-form-input`
    // classes (bowire.css) so the rail matches the rest of the workbench.
    // ------------------------------------------------------------------

    // ---- OWASP API Security Top 10 suite (#173) ----

    function owaspBadge(status) {
        var map = { Vulnerable: ['VULN', 'is-vuln'], Safe: ['OK', 'is-ok'], Error: ['ERR', 'is-err'], NotCovered: ['—', 'is-none'] };
        var m = map[status] || map.NotCovered;
        return el('span', { className: 'bowire-secsuite-badge ' + m[1], textContent: m[0] });
    }

    function renderOwaspRow(entry) {
        var note = entry.status === 'Vulnerable' ? ((entry.vulnCount || 0) + ' finding(s)')
            : entry.status === 'Safe' ? 'clean'
            : entry.status === 'Error' ? 'probe error'
            : 'not exercised';
        return el('div', { className: 'bowire-secsuite-row' },
            owaspBadge(entry.status || 'NotCovered'),
            el('a', { className: 'bowire-secsuite-row-id', href: entry.reference, target: '_blank', rel: 'noopener noreferrer', textContent: entry.id }),
            el('span', { className: 'bowire-secsuite-row-title', textContent: entry.title }),
            el('span', { className: 'bowire-secsuite-row-note', textContent: note }));
    }

    // ---- compliance overview (#381 part 4): per-scan OWASP / CWE / CVSS view ----

    var SEVERITIES = ['critical', 'high', 'medium', 'low'];

    function cweUrl(id) {
        var m = /CWE-(\d+)/i.exec(id || '');
        return m ? 'https://cwe.mitre.org/data/definitions/' + m[1] + '.html' : null;
    }

    function severityChip(sev, count) {
        var cls = { critical: 'is-critical', high: 'is-high', medium: 'is-medium', low: 'is-low' }[sev] || 'is-low';
        return el('span', { className: 'bowire-compliance-sev ' + cls, textContent: sev + ' · ' + count });
    }

    function postureStrip(entries) {
        var strip = el('div', { className: 'bowire-compliance-strip' });
        var cellCls = { Vulnerable: 'is-vuln', Safe: 'is-ok', Error: 'is-err', NotCovered: 'is-none' };
        (entries || []).forEach(function (e) {
            var s = e.status || 'NotCovered';
            strip.appendChild(el('span', {
                className: 'bowire-compliance-cell ' + (cellCls[s] || 'is-none'),
                title: e.id + ' — ' + e.title + ' (' + s + ')'
            }));
        });
        return strip;
    }

    function renderComplianceView(container, result) {
        container.textContent = '';
        if (!result) {
            container.appendChild(el('p', { className: 'bowire-secsuite-hint', textContent: 'Run the suite to see the OWASP / CWE / CVSS compliance overview.' }));
            return;
        }
        var c = result.compliance || { severity: {}, cwe: [], findingCount: 0, maxCvss: 0 };

        // OWASP posture strip.
        container.appendChild(el('div', { className: 'bowire-compliance-block' },
            el('div', { className: 'bowire-compliance-head' },
                el('span', { className: 'bowire-compliance-label', textContent: 'OWASP API Top 10 posture' }),
                el('span', { className: 'bowire-compliance-meta', textContent: result.covered + '/' + result.total + ' exercised · ' + result.vulnerable + ' vulnerable' })),
            postureStrip(result.entries)));

        // Severity histogram + peak CVSS.
        var sevRow = el('div', { className: 'bowire-compliance-sevrow' });
        SEVERITIES.forEach(function (s) {
            var n = (c.severity && c.severity[s]) || 0;
            if (n > 0) sevRow.appendChild(severityChip(s, n));
        });
        if (!sevRow.childNodes.length) sevRow.appendChild(el('span', { className: 'bowire-secsuite-row-note', textContent: 'No vulnerability findings.' }));
        if (c.maxCvss > 0) sevRow.appendChild(el('span', { className: 'bowire-compliance-cvss', textContent: 'peak CVSS ' + c.maxCvss.toFixed(1) }));
        container.appendChild(el('div', { className: 'bowire-compliance-block' },
            el('span', { className: 'bowire-compliance-label', textContent: 'Severity' }),
            sevRow));

        // CWE breakdown.
        if (c.cwe && c.cwe.length) {
            var table = el('div', { className: 'bowire-secsuite-list' });
            c.cwe.forEach(function (w) {
                var url = cweUrl(w.id);
                var idNode = url
                    ? el('a', { className: 'bowire-secsuite-row-id', href: url, target: '_blank', rel: 'noopener noreferrer', textContent: w.id })
                    : el('span', { className: 'bowire-secsuite-row-id', textContent: w.id });
                table.appendChild(el('div', { className: 'bowire-secsuite-row' },
                    idNode,
                    severityChip(w.maxSeverity, w.count),
                    el('span', { className: 'bowire-secsuite-row-title', textContent: w.count + ' finding(s)' }),
                    el('span', { className: 'bowire-secsuite-row-note', textContent: w.maxCvss > 0 ? 'CVSS ' + w.maxCvss.toFixed(1) : '' })));
            });
            container.appendChild(el('div', { className: 'bowire-compliance-block' },
                el('span', { className: 'bowire-compliance-label', textContent: 'CWE breakdown' }),
                table));
        }
    }

    function renderOwaspSuiteSection() {
        var wrap = el('div', { className: 'bowire-secsuite' });
        wrap.appendChild(el('h3', { className: 'bowire-secsuite-title', textContent: 'OWASP API Security Top 10 (2023)' }));
        wrap.appendChild(el('p', { className: 'bowire-secsuite-hint', textContent: 'Run the suite against a target to see which entries are exercised, clean, or vulnerable. Add a credential to unlock the auth-dependent checks (API1 BOLA also takes a second identity).' }));

        var targetInput = el('input', { type: 'text', className: 'bowire-form-input', placeholder: 'https://api.example.com' });
        var authInput = el('input', { type: 'text', className: 'bowire-form-input', placeholder: 'Authorization: Bearer … (optional)' });
        var statusEl = el('span', { className: 'bowire-secsuite-status' });
        var coverageList = el('div', { className: 'bowire-secsuite-list' });
        var complianceView = el('div', { className: 'bowire-compliance' });

        function renderRows(entries) {
            coverageList.textContent = '';
            (entries || []).forEach(function (e) { coverageList.appendChild(renderOwaspRow(e)); });
        }

        // Coverage / Compliance tab toggle over the same scan result.
        var coverageBtn, complianceBtn;
        function setTab(name) {
            var isCov = name === 'coverage';
            coverageBtn.classList.toggle('is-active', isCov);
            complianceBtn.classList.toggle('is-active', !isCov);
            coverageList.style.display = isCov ? '' : 'none';
            complianceView.style.display = isCov ? 'none' : '';
        }
        coverageBtn = el('button', { className: 'bowire-secsuite-tab', textContent: 'Coverage', onclick: function () { setTab('coverage'); } });
        complianceBtn = el('button', { className: 'bowire-secsuite-tab', textContent: 'Compliance', onclick: function () { setTab('compliance'); } });
        var tabs = el('div', { className: 'bowire-secsuite-tabs' }, coverageBtn, complianceBtn);

        var runBtn = el('button', {
            className: 'bowire-btn', textContent: 'Run OWASP suite',
            onclick: function () {
                var target = targetInput.value.trim();
                if (!target) { statusEl.textContent = 'Enter a target URL first.'; return; }
                statusEl.textContent = 'Scanning…';
                runBtn.disabled = true;
                var auth = authInput.value.trim();
                fetch(config.prefix + '/api/security/owasp-scan', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ target: target, authHeaders: auth ? [auth] : [] })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (res) {
                        if (res && res.entries) {
                            renderRows(res.entries);
                            renderComplianceView(complianceView, res);
                            statusEl.textContent = res.covered + '/' + res.total + ' exercised · ' + res.vulnerable + ' with findings';
                        } else {
                            renderComplianceView(complianceView, null);
                            statusEl.textContent = (res && res.detail) || 'Scan failed.';
                        }
                    })
                    .catch(function (e) { statusEl.textContent = 'Scan failed: ' + e; })
                    .finally(function () { runBtn.disabled = false; });
            }
        });

        wrap.appendChild(el('div', { className: 'bowire-secsuite-controls' }, targetInput, authInput, runBtn, statusEl));
        wrap.appendChild(tabs);
        wrap.appendChild(coverageList);
        wrap.appendChild(complianceView);

        renderComplianceView(complianceView, null);
        setTab('coverage');

        fetch(config.prefix + '/api/security/owasp-catalog')
            .then(function (r) { return r.json(); })
            .then(function (entries) { renderRows(entries); })
            .catch(function () { statusEl.textContent = 'Could not load the OWASP catalog.'; });

        return wrap;
    }

    // ---- endpoint discovery (spider) with confirm / ignore triage (#176) ----

    function renderSpiderSection() {
        var wrap = el('div', { className: 'bowire-secsuite' });
        wrap.appendChild(el('h3', { className: 'bowire-secsuite-title', textContent: 'Endpoint discovery (spider)' }));
        wrap.appendChild(el('p', { className: 'bowire-secsuite-hint', textContent: 'Discover candidate endpoints (OpenAPI / GraphQL / robots / sitemap / common paths / page links) from a base URL, then confirm the real ones and ignore the rest.' }));

        var urlInput = el('input', { type: 'text', className: 'bowire-form-input', placeholder: 'https://api.example.com' });
        var authInput = el('input', { type: 'text', className: 'bowire-form-input', placeholder: 'Authorization: Bearer … (optional)' });
        var statusEl = el('span', { className: 'bowire-secsuite-status' });
        var summary = el('div', { className: 'bowire-secsuite-summary' });
        var list = el('div', { className: 'bowire-secsuite-list' });
        var triage = {};
        var rendered = [];

        function refreshSummary() {
            var confirmed = 0, ignored = 0;
            rendered.forEach(function (x) { if (triage[x.id] === 'confirmed') confirmed++; else if (triage[x.id] === 'ignored') ignored++; });
            var pending = rendered.length - confirmed - ignored;
            summary.textContent = rendered.length
                ? (rendered.length + ' candidate(s) · ' + confirmed + ' confirmed · ' + ignored + ' ignored · ' + pending + ' pending')
                : '';
        }

        function row(c) {
            var id = c.method + ' ' + c.url + ' ' + c.source;
            rendered.push({ id: id, url: c.url });
            var r = el('div', { className: 'bowire-secsuite-row' });
            function paint() {
                var s = triage[id];
                r.classList.toggle('is-confirmed', s === 'confirmed');
                r.classList.toggle('is-ignored', s === 'ignored');
            }
            r.appendChild(el('span', { className: 'bowire-secsuite-row-method', textContent: c.method }));
            r.appendChild(el('span', { className: 'bowire-secsuite-row-url', textContent: c.url }));
            r.appendChild(el('span', { className: 'bowire-secsuite-row-src', textContent: c.source }));
            r.appendChild(el('button', { className: 'bowire-btn bowire-secsuite-triage', textContent: '✓', title: 'Confirm — part of my API',
                onclick: function () { triage[id] = triage[id] === 'confirmed' ? undefined : 'confirmed'; paint(); refreshSummary(); } }));
            r.appendChild(el('button', { className: 'bowire-btn bowire-btn--danger bowire-secsuite-triage', textContent: '✕', title: 'Ignore',
                onclick: function () { triage[id] = triage[id] === 'ignored' ? undefined : 'ignored'; paint(); refreshSummary(); } }));
            return r;
        }

        var discoverBtn = el('button', {
            className: 'bowire-btn', textContent: 'Discover endpoints',
            onclick: function () {
                var url = urlInput.value.trim();
                if (!url) { statusEl.textContent = 'Enter a base URL first.'; return; }
                statusEl.textContent = 'Spidering…'; discoverBtn.disabled = true;
                triage = {}; rendered = []; list.textContent = ''; summary.textContent = '';
                var auth = authInput.value.trim();
                fetch(config.prefix + '/api/security/spider', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ url: url, authHeaders: auth ? [auth] : [] })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (res) {
                        if (res && res.candidates) {
                            res.candidates.forEach(function (c) { list.appendChild(row(c)); });
                            statusEl.textContent = res.count + ' candidate(s) discovered.';
                            refreshSummary();
                        } else { statusEl.textContent = (res && res.detail) || 'Spider failed.'; }
                    })
                    .catch(function (e) { statusEl.textContent = 'Spider failed: ' + e; })
                    .finally(function () { discoverBtn.disabled = false; });
            }
        });

        var copyBtn = el('button', {
            className: 'bowire-btn', textContent: 'Copy confirmed URLs',
            onclick: function () {
                var urls = rendered.filter(function (x) { return triage[x.id] === 'confirmed'; }).map(function (x) { return x.url; });
                if (!urls.length) { statusEl.textContent = 'No confirmed candidates yet.'; return; }
                if (navigator.clipboard) navigator.clipboard.writeText(urls.join('\n')).then(function () { statusEl.textContent = 'Copied ' + urls.length + ' URL(s).'; });
                else statusEl.textContent = urls.join('  ');
            }
        });

        wrap.appendChild(el('div', { className: 'bowire-secsuite-controls' }, urlInput, authInput, discoverBtn, copyBtn, statusEl));
        wrap.appendChild(summary);
        wrap.appendChild(list);
        return wrap;
    }

    // ---- manual OAST / out-of-band pen-test surface (#486) ----
    //
    // The interactive counterpart to the scanner's --oast-server path: generate
    // a callback payload by hand, paste it into whatever you're testing, and
    // watch the feed for the target reaching back. The host owns the interactsh
    // session (crypto + key), so the browser only allocates + polls.

    // Module-scoped so the payload list + feed survive the rail's re-renders
    // (renderSecurityMain rebuilds the section on every render).
    var oastGenerated = [];
    var oastFeed = [];
    var _oastPollTimer = null;
    var _oastFeedEl = null;
    // A poll that keeps failing must NOT read as an empty (clean) feed — that
    // would hide a lost session / unreachable server on a real target.
    var _oastError = null;

    function oastCallbackRow(c) {
        var when = '';
        try { when = new Date(c.timestampUnixMs).toLocaleTimeString(); } catch (_) { /* ignore */ }
        var proto = (c.protocol || '?').toLowerCase();
        var row = el('div', { className: 'bowire-secsuite-row bowire-oast-hit is-' + (proto === 'dns' ? 'dns' : 'http') });
        row.appendChild(el('span', { className: 'bowire-oast-proto', textContent: proto + (c.queryType ? ' ' + c.queryType : '') }));
        row.appendChild(el('span', { className: 'bowire-secsuite-row-url', textContent: c.id || '(callback)' }));
        row.appendChild(el('span', { className: 'bowire-secsuite-row-src', textContent: c.remoteAddress || '' }));
        row.appendChild(el('span', { className: 'bowire-secsuite-row-note', textContent: when }));
        return row;
    }

    function renderOastFeed() {
        if (!_oastFeedEl) return;
        _oastFeedEl.textContent = '';
        // Surface a persistent poll failure loudly — an unreachable / restarted
        // server or a lost session is NOT the same as "target didn't call back".
        if (_oastError) {
            _oastFeedEl.appendChild(el('div', { className: 'bowire-oast-error', textContent: 'Callback polling failed — results below may be stale. ' + _oastError }));
        }
        if (!oastFeed.length) {
            if (!_oastError) {
                _oastFeedEl.appendChild(el('p', { className: 'bowire-secsuite-hint', textContent: 'No callbacks yet. Plant a payload where a target might resolve or fetch it — a DNS lookup alone proves it reached the host.' }));
            }
            return;
        }
        // Newest first — the callback that just landed is what the operator is watching for.
        oastFeed.slice().reverse().forEach(function (c) { _oastFeedEl.appendChild(oastCallbackRow(c)); });
    }

    function refreshOastFeed() {
        fetch(config.prefix + '/api/security/oast/poll')
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
            .then(function (res) {
                if (res.ok && res.body && Array.isArray(res.body.interactions)) {
                    oastFeed = res.body.interactions; _oastError = null;
                } else {
                    // The endpoint returns { error } on a 502 (server unreachable
                    // / unreadable) — show it rather than silently keeping a
                    // stale-looking empty feed.
                    _oastError = (res.body && res.body.error) || 'poll returned an unexpected response';
                }
                renderOastFeed();
            })
            .catch(function (e) { _oastError = String(e); renderOastFeed(); });
    }

    // One guarded timer for the whole rail: polls only while the Security rail
    // is showing and a session has been started, and never stacks intervals.
    function ensureOastPoll() {
        if (_oastPollTimer !== null) return;
        _oastPollTimer = setInterval(function () {
            if (typeof railMode !== 'undefined' && railMode !== 'security') return;
            if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
            if (!oastGenerated.length) return;
            refreshOastFeed();
        }, 4000);
    }

    function renderOastPayloadRow(host) {
        var row = el('div', { className: 'bowire-secsuite-row bowire-oast-payload' });
        row.appendChild(el('code', { className: 'bowire-oast-host', textContent: host }));
        row.appendChild(el('button', {
            className: 'bowire-btn bowire-secsuite-triage', textContent: 'Copy', title: 'Copy the callback host',
            onclick: function () {
                var full = host;
                if (navigator.clipboard) navigator.clipboard.writeText(full).catch(function () { /* ignore */ });
            }
        }));
        return row;
    }

    function renderOastSection() {
        var wrap = el('div', { className: 'bowire-secsuite' });
        wrap.appendChild(el('h3', { className: 'bowire-secsuite-title', textContent: 'Out-of-band (OAST) — manual' }));

        var body = el('div', { className: 'bowire-oast-body' });
        wrap.appendChild(body);

        // The panel's shape depends on whether a server is configured; decide
        // that from /status rather than guessing.
        fetch(config.prefix + '/api/security/oast/status')
            .then(function (r) { return r.json(); })
            .then(function (st) {
                body.textContent = '';
                if (!st || !st.configured) {
                    var msg = st && st.error
                        ? 'OAST server rejected: ' + st.error
                        : 'No interaction server is configured. Start one — `bowire oast serve --domain oast.example.com --public-ip <ip>` — then launch the workbench with `--oast-server http://oast.example.com` (or set Bowire__Oast__Server). The same server the scanner’s --oast-server uses.';
                    body.appendChild(el('p', { className: 'bowire-pane-empty', textContent: msg }));
                    return;
                }

                body.appendChild(el('p', { className: 'bowire-secsuite-hint', textContent: 'Callbacks collected via ' + st.server + '. Generate a payload host, plant it by hand (a URL, a header, an XML entity, an SSRF sink), and watch below for the target reaching back.' }));

                var statusEl = el('span', { className: 'bowire-secsuite-status' });
                var payloadList = el('div', { className: 'bowire-secsuite-list' });
                _oastFeedEl = el('div', { className: 'bowire-secsuite-list bowire-oast-feed' });

                var genBtn = el('button', {
                    className: 'bowire-btn', textContent: 'Generate payload',
                    onclick: function () {
                        genBtn.disabled = true; statusEl.textContent = 'Allocating…';
                        fetch(config.prefix + '/api/security/oast/allocate', { method: 'POST' })
                            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
                            .then(function (res) {
                                if (res.ok && res.body && res.body.host) {
                                    oastGenerated.push(res.body.host);
                                    payloadList.insertBefore(renderOastPayloadRow(res.body.host), payloadList.firstChild);
                                    // Auto-copy the freshest payload — the operator is about to paste it.
                                    if (navigator.clipboard) navigator.clipboard.writeText(res.body.host).catch(function () { /* ignore */ });
                                    statusEl.textContent = 'Payload ready + copied. Waiting for callbacks…';
                                    ensureOastPoll();
                                    refreshOastFeed();
                                } else {
                                    statusEl.textContent = (res.body && res.body.error) || 'Could not allocate a payload.';
                                }
                            })
                            .catch(function (e) { statusEl.textContent = 'Allocate failed: ' + e; })
                            .finally(function () { genBtn.disabled = false; });
                    }
                });

                body.appendChild(el('div', { className: 'bowire-secsuite-controls' }, genBtn, statusEl));
                if (oastGenerated.length) {
                    oastGenerated.slice().reverse().forEach(function (h) { payloadList.appendChild(renderOastPayloadRow(h)); });
                }
                body.appendChild(el('div', { className: 'bowire-oast-cols' },
                    el('div', {}, el('div', { className: 'bowire-compliance-label', textContent: 'Payloads' }), payloadList),
                    el('div', {}, el('div', { className: 'bowire-compliance-label', textContent: 'Live callbacks' }), _oastFeedEl)));

                renderOastFeed();
                // Pick up any callbacks already caught in a prior render of this session.
                if (oastGenerated.length) { ensureOastPoll(); refreshOastFeed(); }
            })
            .catch(function () {
                body.textContent = '';
                body.appendChild(el('p', { className: 'bowire-pane-empty', textContent: 'Could not reach the OAST status endpoint.' }));
            });

        return wrap;
    }

    // ---- rail renderers ----

    function renderSecuritySidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        sidebar.appendChild(renderSidebarToolbar({ title: 'Security' }));
        sidebar.appendChild(el('div', {
            className: 'bowire-pane-empty',
            style: 'padding:12px 14px',
            textContent: 'OWASP API Top 10, endpoint discovery, threat model, fuzz, and Nuclei templates sit in the main pane. Discovered endpoints are pulled automatically from the active workspace.'
        }));
        return sidebar;
    }

    function renderSecurityMain() {
        var secMain = el('div', { id: 'bowire-main-security', className: 'bowire-main bowire-main-security' });
        var pad = el('div', { className: 'bowire-main-pad' });

        // OWASP suite + endpoint spider are always available — served by this package.
        pad.appendChild(renderOwaspSuiteSection());
        pad.appendChild(renderSpiderSection());
        pad.appendChild(renderOastSection());

        // AI-assisted tools (threat model, fuzz values) are contributed by
        // the AI package when present; otherwise a thin install hint.
        if (typeof window !== 'undefined' && window.__bowireAi
                && typeof window.__bowireAi.renderSecurityPanel === 'function') {
            pad.appendChild(window.__bowireAi.renderSecurityPanel());
        } else {
            pad.appendChild(el('p', {
                className: 'bowire-pane-empty',
                textContent: 'AI-assisted security tools (threat-model ranking, fuzz-value suggestions) need Kuestenlogik.Bowire.Ai installed in the workbench process. Install the package + restart to enable them.'
            }));
        }
        secMain.appendChild(pad);
        return secMain;
    }

    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.securitySidebar = renderSecuritySidebar;
        window.__bowireRailRenderers.securityMain = renderSecurityMain;
    }
