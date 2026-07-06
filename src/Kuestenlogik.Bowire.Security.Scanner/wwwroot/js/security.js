    // Copyright 2026 Küstenlogik · Apache-2.0
    // ------------------------------------------------------------------
    // #306 / #314 — Security rail JS fragment.
    //
    // Moved out of core (render-sidebar.js / render-main.js) into the
    // Kuestenlogik.Bowire.Security.Scanner package. The sidebar is a title
    // + hint; the main pane hosts the OWASP API Top 10 suite (always,
    // served by this package's /api/security/owasp-* endpoints) plus the
    // AI security panel when Kuestenlogik.Bowire.Ai is in the process.
    // Registered on the renderer-key seam so core resolves them from the
    // descriptor's Sidebar/MainPaneRendererKey.
    // ------------------------------------------------------------------

    // #173 — OWASP API Security Top 10 suite surface. Lists the ten
    // entries (fetched from /api/security/owasp-catalog) and runs the
    // suite against a target via /api/security/owasp-scan, painting each
    // row covered / clean / vulnerable from the roll-up.
    function owaspBadge(status) {
        var map = {
            Vulnerable: ['VULN', '#c0392b'],
            Safe: ['OK', '#1e8449'],
            Error: ['ERR', '#b9770e'],
            NotCovered: ['—', '#7f8c8d']
        };
        var m = map[status] || map.NotCovered;
        return el('span', {
            textContent: m[0],
            style: 'display:inline-block;min-width:44px;text-align:center;font-size:11px;font-weight:600;'
                + 'padding:1px 6px;border-radius:4px;color:#fff;background:' + m[1]
        });
    }

    function renderOwaspRow(entry) {
        var note = entry.status === 'Vulnerable' ? ((entry.vulnCount || 0) + ' finding(s)')
            : entry.status === 'Safe' ? 'clean'
            : entry.status === 'Error' ? 'probe error'
            : 'not exercised';
        return el('div', { style: 'display:flex;align-items:center;gap:10px;padding:5px 0;border-bottom:1px solid rgba(127,127,127,.15)' },
            owaspBadge(entry.status || 'NotCovered'),
            el('a', { href: entry.reference, target: '_blank', rel: 'noopener noreferrer',
                textContent: entry.id, style: 'font-family:monospace;font-size:12px;min-width:72px;text-decoration:none' }),
            el('span', { textContent: entry.title, style: 'flex:1' }),
            el('span', { textContent: note, style: 'font-size:12px;opacity:.7' }));
    }

    function renderOwaspSuiteSection() {
        var wrap = el('div', { style: 'margin-bottom:20px' });
        wrap.appendChild(el('h3', { textContent: 'OWASP API Security Top 10 (2023)', style: 'margin:0 0 4px' }));
        wrap.appendChild(el('p', { textContent: 'Run the suite against a target to see which entries are exercised, clean, or vulnerable. Add a credential to unlock the auth-dependent checks (API1 BOLA also takes a second identity).',
            style: 'margin:0 0 8px;font-size:12px;opacity:.75' }));

        var targetInput = el('input', { type: 'text', placeholder: 'https://api.example.com', style: 'flex:1;min-width:200px;padding:5px 8px' });
        var authInput = el('input', { type: 'text', placeholder: 'Authorization: Bearer … (optional)', style: 'flex:1;min-width:200px;padding:5px 8px' });
        var statusEl = el('span', { style: 'font-size:12px;opacity:.8' });
        var list = el('div', {});

        function renderRows(entries) {
            list.textContent = '';
            (entries || []).forEach(function (e) { list.appendChild(renderOwaspRow(e)); });
        }

        var runBtn = el('button', {
            textContent: 'Run OWASP suite',
            className: 'bowire-btn bowire-btn-primary',
            style: 'padding:5px 12px',
            onclick: function () {
                var target = targetInput.value.trim();
                if (!target) { statusEl.textContent = 'Enter a target URL first.'; return; }
                statusEl.textContent = 'Scanning…';
                runBtn.disabled = true;
                var auth = authInput.value.trim();
                fetch(config.prefix + '/api/security/owasp-scan', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ target: target, authHeaders: auth ? [auth] : [] })
                })
                    .then(function (r) { return r.json(); })
                    .then(function (res) {
                        if (res && res.entries) {
                            renderRows(res.entries);
                            statusEl.textContent = res.covered + '/' + res.total + ' exercised · ' + res.vulnerable + ' with findings';
                        } else {
                            statusEl.textContent = (res && res.detail) || 'Scan failed.';
                        }
                    })
                    .catch(function (e) { statusEl.textContent = 'Scan failed: ' + e; })
                    .finally(function () { runBtn.disabled = false; });
            }
        });

        wrap.appendChild(el('div', { style: 'display:flex;gap:6px;flex-wrap:wrap;align-items:center;margin-bottom:8px' },
            targetInput, authInput, runBtn, statusEl));
        wrap.appendChild(list);

        // Paint the ten rows straight away (pre-scan, all "not exercised").
        fetch(config.prefix + '/api/security/owasp-catalog')
            .then(function (r) { return r.json(); })
            .then(function (entries) { renderRows(entries); })
            .catch(function () { statusEl.textContent = 'Could not load the OWASP catalog.'; });

        return wrap;
    }

    function renderSecuritySidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        sidebar.appendChild(renderSidebarToolbar({ title: 'Security' }));
        sidebar.appendChild(el('div', {
            className: 'bowire-pane-empty',
            style: 'padding:12px 14px',
            textContent: 'OWASP API Top 10, threat model, fuzz, and Nuclei templates sit in the main pane. Discovered endpoints are pulled automatically from the active workspace.'
        }));
        return sidebar;
    }

    function renderSecurityMain() {
        var secMain = el('div', { id: 'bowire-main-security', className: 'bowire-main bowire-main-security' });
        var pad = el('div', { className: 'bowire-main-pad' });

        // OWASP suite is always available — it's served by this package.
        pad.appendChild(renderOwaspSuiteSection());

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
