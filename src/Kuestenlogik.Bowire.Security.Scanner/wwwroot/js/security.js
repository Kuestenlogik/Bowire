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

    function renderOwaspSuiteSection() {
        var wrap = el('div', { className: 'bowire-secsuite' });
        wrap.appendChild(el('h3', { className: 'bowire-secsuite-title', textContent: 'OWASP API Security Top 10 (2023)' }));
        wrap.appendChild(el('p', { className: 'bowire-secsuite-hint', textContent: 'Run the suite against a target to see which entries are exercised, clean, or vulnerable. Add a credential to unlock the auth-dependent checks (API1 BOLA also takes a second identity).' }));

        var targetInput = el('input', { type: 'text', className: 'bowire-form-input', placeholder: 'https://api.example.com' });
        var authInput = el('input', { type: 'text', className: 'bowire-form-input', placeholder: 'Authorization: Bearer … (optional)' });
        var statusEl = el('span', { className: 'bowire-secsuite-status' });
        var list = el('div', { className: 'bowire-secsuite-list' });

        function renderRows(entries) {
            list.textContent = '';
            (entries || []).forEach(function (e) { list.appendChild(renderOwaspRow(e)); });
        }

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
                            statusEl.textContent = res.covered + '/' + res.total + ' exercised · ' + res.vulnerable + ' with findings';
                        } else { statusEl.textContent = (res && res.detail) || 'Scan failed.'; }
                    })
                    .catch(function (e) { statusEl.textContent = 'Scan failed: ' + e; })
                    .finally(function () { runBtn.disabled = false; });
            }
        });

        wrap.appendChild(el('div', { className: 'bowire-secsuite-controls' }, targetInput, authInput, runBtn, statusEl));
        wrap.appendChild(list);

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
