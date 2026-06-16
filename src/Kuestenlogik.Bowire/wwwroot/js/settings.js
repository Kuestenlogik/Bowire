    // ---- Settings Dialog ----
    // Postman-style modal with category sidebar + settings panel.
    // Bowire-owned categories first, then one section per protocol
    // plugin (Phase 2: IBowireProtocol.Settings).

    var settingsOpen = false;
    var settingsTab = 'general';

    // #192 — Build the settings nav tree. Top level: General /
    // Shortcuts / Data / Assistant / Plugins. Plugins expands to one
    // child per enabled-plugin-with-settings, plus a leading "All
    // plugins" entry that lands on the legacy overview page (enable
    // toggles + install hints).
    function _buildSettingsTreeNodes() {
        function leaf(id, label, icon) {
            return {
                id: 'settings:' + id,
                label: label,
                icon: icon,
                selected: settingsTab === id,
                onClick: function () {
                    if (settingsTab === 'plugins' && id !== 'plugins') {
                        pluginsTabFetchedThisOpen = false;
                    }
                    settingsTab = id;
                    renderSettingsDialog();
                }
            };
        }
        // #193 Phase 2 item 4 — non-clickable group header. Used to
        // visually segment the tree into "My preferences" (this
        // dialog) vs "This project" (the Workspace Settings detail
        // panel, reachable via the workspace-pointer leaf below). The
        // helpers.js renderTree honours .header by rendering the row
        // without click bindings.
        function header(label) {
            return { id: 'settings-group:' + label, label: label, header: true };
        }

        var nodes = [
            header('My preferences'),
            leaf('general', 'General', 'settings'),
            leaf('shortcuts', 'Shortcuts', 'list'),
            leaf('data', 'Data', 'trash'),
            leaf('ai', 'Assistant', 'spark')
        ];

        // Per-plugin children. The Plugins group node itself routes to
        // the overview page on click — no separate "All plugins" entry
        // (cleaner tree shape, matches MudBlazor NavMenu where the
        // parent is the destination + chevron handles expansion).
        var pluginChildren = [];
        for (var pi = 0; pi < protocols.length; pi++) {
            var p = protocols[pi];
            if (!isProtocolEnabled(p.id)) continue;
            if (!p.settings || p.settings.length === 0) continue;
            (function (pp) {
                var tabId = 'plugin-' + pp.id;
                pluginChildren.push({
                    id: 'settings:' + tabId,
                    label: pp.name,
                    iconHtml: pp.icon || null,
                    selected: settingsTab === tabId,
                    onClick: function () {
                        if (settingsTab === 'plugins') {
                            pluginsTabFetchedThisOpen = false;
                        }
                        settingsTab = tabId;
                        renderSettingsDialog();
                    }
                });
            })(p);
        }

        // Plugins group — click on the label routes to the overview
        // (enable toggles, install hints); the chevron toggles
        // expansion independently. Default-open when the operator is
        // already on a plugin tab so the active row is visible
        // without an extra click.
        var pluginsKey = 'plugins';
        var pluginsActive = settingsTab === 'plugins' || settingsTab.indexOf('plugin-') === 0;
        var pluginsExpanded = isSettingsTreeNodeExpanded(pluginsKey, pluginsActive);
        nodes.push({
            id: 'settings:plugins',
            label: 'Plugins',
            icon: 'plug',
            badge: pluginChildren.length > 0 ? pluginChildren.length : null,
            expandable: pluginChildren.length > 0,
            expanded: pluginsExpanded,
            selected: settingsTab === 'plugins',
            onClick: function () {
                settingsTab = 'plugins';
                renderSettingsDialog();
            },
            onToggle: function () {
                toggleSettingsTreeNode(pluginsKey, pluginsActive);
                renderSettingsDialog();
            },
            children: pluginChildren
        });

        // #193 Phase 2 item 4 — second group for workspace-scope
        // settings. The Settings dialog itself doesn't own them (they
        // live in the Workspace tree → Settings node so they round-
        // trip through .bww with the workspace), but a pointer here
        // makes the split discoverable: an operator who opens
        // Settings looking for "URL list" or "plugin pins" sees the
        // category exists + a one-click jump to the right surface.
        nodes.push(header('This project'));
        nodes.push(leaf('workspace', 'Workspace…', 'layers'));

        return nodes;
    }

    function openSettings(targetTab) {
        settingsOpen = true;
        // Allow callers (e.g. the chat-pane invocation-status pill) to
        // deep-link into a specific tab. Falls back to 'general' when
        // no argument is given, preserving the legacy entry point.
        settingsTab = targetTab && typeof targetTab === 'string' ? targetTab : 'general';
        renderSettingsDialog();
    }

    function closeSettings() {
        settingsOpen = false;
        // Reset the per-open one-shot guard so reopening the dialog
        // re-fetches plugin data. See pluginsTabFetchedThisOpen in
        // renderSettingsPlugins() for the loop-prevention story.
        pluginsTabFetchedThisOpen = false;
        renderSettingsDialog();
    }

    // #192 — Settings nav as a tree (MudBlazor NavMenu style). Plugins
    // is now an expandable group with each enabled+settings-providing
    // plugin as a child row, so MQTT / WebSocket / gRPC live under the
    // umbrella instead of cluttering the top level. Persistence per
    // group key so the operator's collapse choice survives a reload.
    var settingsTreeExpanded = {};
    try {
        var rawSTE = localStorage.getItem('bowire_settings_tree_expanded');
        if (rawSTE) {
            var parsedSTE = JSON.parse(rawSTE);
            if (parsedSTE && typeof parsedSTE === 'object') settingsTreeExpanded = parsedSTE;
        }
    } catch { /* ignore */ }
    function persistSettingsTreeExpanded() {
        try { localStorage.setItem('bowire_settings_tree_expanded', JSON.stringify(settingsTreeExpanded)); }
        catch { /* ignore */ }
    }
    function isSettingsTreeNodeExpanded(key, defaultOpen) {
        if (settingsTreeExpanded[key] === undefined) return !!defaultOpen;
        return !!settingsTreeExpanded[key];
    }
    function toggleSettingsTreeNode(key, defaultOpen) {
        settingsTreeExpanded[key] = !isSettingsTreeNodeExpanded(key, defaultOpen);
        persistSettingsTreeExpanded();
    }

    function renderSettingsDialog() {
        var existing = document.querySelector('.bowire-settings-overlay');
        if (existing) existing.remove();
        if (!settingsOpen) return;

        // ---- Left sidebar: navigation tree ----
        var leftPanel = el('div', { className: 'bowire-settings-left' });
        leftPanel.appendChild(renderTree(_buildSettingsTreeNodes(), { ariaLabel: 'Settings' }));

        // ---- Right panel: settings for selected category ----
        // ID includes the active tab so morphdom fully replaces the
        // panel when switching categories instead of reusing stale DOM.
        var rightPanel = el('div', { id: 'bowire-settings-right-' + settingsTab, className: 'bowire-settings-right' });

        // #193 Phase 2 item 4 — scope banner at the top of every
        // section. The Settings dialog itself only houses user-scope
        // preferences (theme, AI, shortcuts, data-cleanup), so a
        // single sticky banner spelling that out is enough to make
        // the split discoverable for an operator opening a workspace
        // for the first time. The workspace pointer page suppresses
        // the banner because it has its own scope copy.
        if (settingsTab !== 'workspace') {
            rightPanel.appendChild(el('div', {
                className: 'bowire-settings-scope-banner',
                role: 'note',
                textContent: 'These settings stay on this machine — they don\'t travel with the workspace file (.bww).'
            }));
        }

        if (settingsTab === 'general') {
            rightPanel.appendChild(renderSettingsGeneral());
        } else if (settingsTab === 'shortcuts') {
            rightPanel.appendChild(renderSettingsShortcuts());
        } else if (settingsTab === 'data') {
            rightPanel.appendChild(renderSettingsData());
        } else if (settingsTab === 'ai') {
            rightPanel.appendChild(renderSettingsAi());
        } else if (settingsTab === 'plugins') {
            rightPanel.appendChild(renderSettingsPlugins());
        } else if (settingsTab === 'workspace') {
            rightPanel.appendChild(renderSettingsWorkspacePointer());
        } else if (settingsTab.indexOf('plugin-') === 0) {
            var pluginId = settingsTab.substring(7);
            var plugin = protocols.find(function (p) { return p.id === pluginId; });
            if (plugin) rightPanel.appendChild(renderPluginSettings(plugin));
        }

        // ---- Modal frame ----
        var modal = el('div', { className: 'bowire-settings-modal' },
            el('div', { className: 'bowire-settings-header' },
                el('span', { className: 'bowire-settings-title', textContent: 'Settings' }),
                el('button', {
                    id: 'bowire-settings-close-btn',
                    className: 'bowire-settings-close',
                    innerHTML: svgIcon('close'),
                    title: 'Close (Esc)',
                    'aria-label': 'Close settings',
                    onClick: closeSettings
                })
            ),
            el('div', { className: 'bowire-settings-body' }, leftPanel, rightPanel)
        );

        var overlay = el('div', {
            className: 'bowire-settings-overlay',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'Settings',
            onClick: function (e) { if (e.target === overlay) closeSettings(); }
        }, modal);

        document.body.appendChild(overlay);
    }

    // ---- General Settings ----
    function renderSettingsGeneral() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', { className: 'bowire-settings-section-title', textContent: 'General' }));

        // Theme
        section.appendChild(renderSettingsRow(
            'Theme',
            'Color scheme for the UI',
            function () {
                var select = el('select', {
                    id: 'bowire-settings-theme-select',
                    className: 'bowire-settings-select',
                    onChange: function (e) {
                        setThemePreference(e.target.value);
                        render();
                        renderSettingsDialog();
                    }
                });
                var opts = [
                    { value: 'auto', label: 'Auto (follow OS)' },
                    { value: 'dark', label: 'Dark' },
                    { value: 'light', label: 'Light' }
                ];
                for (var i = 0; i < opts.length; i++) {
                    var opt = el('option', { value: opts[i].value, textContent: opts[i].label });
                    if (opts[i].value === themePreference) opt.selected = true;
                    select.appendChild(opt);
                }
                return select;
            }
        ));

        // Auto-interpret JSON in payloads
        var autoInterpret = localStorage.getItem('bowire_auto_interpret') !== 'false';
        section.appendChild(renderSettingsToggle(
            'Auto-interpret JSON',
            'Parse JSON payloads in WebSocket, MQTT, and SSE responses for pretty display',
            autoInterpret,
            function (val) {
                localStorage.setItem('bowire_auto_interpret', val ? 'true' : 'false');
            }
        ));

        // Auto-create initial workspace. Per-browser opt-in; the host
        // can also force this on via appsettings (Bowire:
        // AutoCreateInitialWorkspace) or --auto-create-initial-workspace.
        var autoCreateInitial = localStorage.getItem('bowire_auto_create_initial_workspace') === 'true';
        var hostForced = !!(config && config.autoCreateInitialWorkspace === true);
        section.appendChild(renderSettingsToggle(
            'Auto-create initial workspace',
            hostForced
                ? 'Forced on by the host (appsettings / CLI). New installs boot straight into a "Personal" workspace.'
                : 'Seed a default "Personal" workspace on first run instead of showing the empty Home + Create-Workspace CTA. Takes effect after a reload of a fresh install.',
            hostForced || autoCreateInitial,
            function (val) {
                if (hostForced) return; // host forces value, browser can't override
                localStorage.setItem('bowire_auto_create_initial_workspace', val ? 'true' : 'false');
            }
        ));

        // Workspace identity cue — opt-in peripheral indicator of the
        // active workspace's colour. Split into a bool (on/off) and
        // a variant (which presentation) so the operator can flip
        // between presentations without dropping back to 'off'.
        //
        // localStorage keys:
        //   bowire_workspace_identity_enabled  ('true' / 'false')
        //   bowire_workspace_identity_variant  ('top-strip' / 'rail-tint')
        // Variant default = 'top-strip' (the original presentation).
        //
        // Back-compat: the older `bowire_workspace_identity_mode` key
        // packed both into one value. Migrate on first read so existing
        // opt-ins don't regress to off.
        (function _migrateIdentityKeys() {
            try {
                var legacy = localStorage.getItem('bowire_workspace_identity_mode');
                if (legacy != null) {
                    if (localStorage.getItem('bowire_workspace_identity_enabled') == null) {
                        localStorage.setItem('bowire_workspace_identity_enabled', legacy === 'none' ? 'false' : 'true');
                    }
                    if (localStorage.getItem('bowire_workspace_identity_variant') == null
                        && (legacy === 'top-strip' || legacy === 'rail-tint')) {
                        localStorage.setItem('bowire_workspace_identity_variant', legacy);
                    }
                    localStorage.removeItem('bowire_workspace_identity_mode');
                }
                // Even older boolean from the very first iteration.
                var older = localStorage.getItem('bowire_show_workspace_identity_band');
                if (older != null) {
                    if (localStorage.getItem('bowire_workspace_identity_enabled') == null) {
                        localStorage.setItem('bowire_workspace_identity_enabled', older === 'true' ? 'true' : 'false');
                    }
                    localStorage.removeItem('bowire_show_workspace_identity_band');
                }
            } catch { /* ignore */ }
        })();
        var identityEnabled = localStorage.getItem('bowire_workspace_identity_enabled') === 'true';
        var identityVariant = localStorage.getItem('bowire_workspace_identity_variant') || 'top-strip';
        section.appendChild(renderSettingsToggle(
            'Workspace identity cue',
            'Show a peripheral indicator in the active workspace’s colour.',
            identityEnabled,
            function (val) {
                localStorage.setItem('bowire_workspace_identity_enabled', val ? 'true' : 'false');
                render();
            }
        ));
        section.appendChild(renderSettingsRow(
            'Identity cue style',
            'Presentation used when the identity cue is on.',
            function () {
                var select = el('select', {
                    className: 'bowire-settings-select',
                    onChange: function (e) {
                        localStorage.setItem('bowire_workspace_identity_variant', e.target.value);
                        render();
                    }
                });
                var opts = [
                    { value: 'top-strip', label: 'Top strip' },
                    { value: 'rail-tint', label: 'Rail tint' }
                ];
                for (var ii = 0; ii < opts.length; ii++) {
                    var opt = el('option', { value: opts[ii].value, textContent: opts[ii].label });
                    if (opts[ii].value === identityVariant) opt.selected = true;
                    select.appendChild(opt);
                }
                return select;
            }
        ));

        // Schema Watch interval
        section.appendChild(renderSettingsRow(
            'Schema Watch interval',
            'How often to re-discover services when Schema Watch is active',
            function () {
                var input = el('input', {
                    id: 'bowire-settings-watch-interval-input',
                    type: 'number',
                    className: 'bowire-settings-input',
                    value: '15',
                    min: '5',
                    max: '300',
                    style: 'width:80px',
                    onChange: function (e) {
                        localStorage.setItem('bowire_watch_interval', e.target.value);
                    }
                });
                var stored = localStorage.getItem('bowire_watch_interval');
                if (stored) input.value = stored;
                return el('div', { style: 'display:flex;align-items:center;gap:6px' },
                    input, el('span', { textContent: 'seconds', style: 'font-size:12px;color:var(--bowire-text-tertiary)' }));
            }
        ));

        // MCP adapter status — read-only mirror of the server's
        // --enable-mcp-adapter / Bowire:EnableMcpAdapter configuration.
        // The adapter is wired at ASP.NET startup (MapBowireMcpAdapter
        // adds routes); we can't toggle it from a running session.
        // What we *can* do is tell the user clearly whether it's on
        // right now and give them a copy-pasteable command to flip
        // it on the next start. The status comes from
        // GET {prefix}/api/mcp-adapter — a 200 means the adapter is
        // mounted, a 404 means the route doesn't exist (adapter off).
        section.appendChild(renderSettingsRow(
            'MCP adapter',
            'Expose Bowire’s discover / invoke / record primitives as MCP tools so AI agents (Claude, Cursor, Copilot) can drive the workbench.',
            function () {
                var statusBox = el('div', {
                    id: 'bowire-settings-mcp-adapter-status',
                    style: 'display:flex;flex-direction:column;align-items:flex-end;gap:4px;min-width:220px'
                });

                // Initial state — "checking…" until fetch resolves.
                statusBox.appendChild(el('span', {
                    style: 'font-size:12px;color:var(--bowire-text-tertiary)',
                    textContent: 'Checking…'
                }));

                fetch(`${config.prefix}/api/mcp-adapter`)
                    .then(function (resp) {
                        statusBox.innerHTML = '';
                        if (resp.ok) {
                            return resp.json().then(function (data) {
                                statusBox.appendChild(el('span', {
                                    style: 'font-size:13px;color:var(--bowire-text);font-weight:500',
                                    innerHTML: '<span style="color:var(--bowire-success,#10b981);margin-right:4px">✓</span>Active'
                                }));
                                statusBox.appendChild(el('code', {
                                    style: 'font-size:11px;color:var(--bowire-text-secondary);background:var(--bowire-bg-elevated);padding:2px 6px;border-radius:3px',
                                    textContent: data.path || '/mcp'
                                }));
                            });
                        }
                        renderMcpAdapterDisabled(statusBox);
                    })
                    .catch(function () { renderMcpAdapterDisabled(statusBox); });

                return statusBox;
            }
        ));

        // #169 — Hints and warnings. Lists every banner / empty-card
        // hint the operator has dismissed so they can bring any of
        // them back. The state is read live from the central
        // dismissed-hint store (prologue.js).
        section.appendChild(renderHintsAndWarnings());

        return section;
    }

    // #169 — Settings row for restoring dismissed hints. Renders a
    // light list of dismissed hint labels with a "Show again" link
    // per row, plus a single "Reset all" link in the row header.
    function renderHintsAndWarnings() {
        var row = el('div', { className: 'bowire-settings-row bowire-settings-hints-row' });
        row.appendChild(el('div', { className: 'bowire-settings-row-info' },
            el('div', { className: 'bowire-settings-row-label', textContent: 'Hints and warnings' }),
            el('div', {
                className: 'bowire-settings-row-desc',
                textContent: 'First-time hints and banners you’ve dismissed. Bring any of them back.'
            })
        ));

        var listCol = el('div', { className: 'bowire-settings-row-control bowire-settings-hints-list' });
        var hints = (typeof listDismissedHints === 'function') ? listDismissedHints() : [];
        if (hints.length === 0) {
            listCol.appendChild(el('div', {
                className: 'bowire-settings-hints-empty',
                textContent: 'No hints dismissed.'
            }));
        } else {
            hints.forEach(function (h) {
                var item = el('div', { className: 'bowire-settings-hints-item' });
                item.appendChild(el('span', {
                    className: 'bowire-settings-hints-label',
                    textContent: h.label,
                    title: h.id
                }));
                item.appendChild(el('span', {
                    className: 'bowire-settings-hints-scope',
                    textContent: h.scope === 'permanent' ? 'permanent' : 'this session'
                }));
                item.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-settings-hints-restore',
                    textContent: 'Show again',
                    onClick: function () {
                        if (typeof undismissHint === 'function') undismissHint(h.id);
                        render();
                        renderSettingsDialog();
                    }
                }));
                listCol.appendChild(item);
            });
            listCol.appendChild(el('button', {
                type: 'button',
                className: 'bowire-settings-hints-reset',
                textContent: 'Reset all hints',
                onClick: function () {
                    if (typeof resetAllDismissedHints === 'function') resetAllDismissedHints();
                    render();
                    renderSettingsDialog();
                }
            }));
        }
        row.appendChild(listCol);
        return row;
    }

    function renderMcpAdapterDisabled(statusBox) {
        statusBox.appendChild(el('span', {
            style: 'font-size:13px;color:var(--bowire-text);font-weight:500',
            innerHTML: '<span style="color:var(--bowire-text-tertiary);margin-right:4px">✕</span>Disabled'
        }));
        var cmdRow = el('div', { style: 'display:flex;align-items:center;gap:4px' });
        var cmdCode = el('code', {
            style: 'font-size:11px;color:var(--bowire-text-secondary);background:var(--bowire-bg-elevated);padding:2px 6px;border-radius:3px',
            textContent: 'bowire --enable-mcp-adapter'
        });
        var copyBtn = el('button', {
            type: 'button',
            className: 'bowire-settings-copy-mini',
            style: 'background:none;border:none;cursor:pointer;color:var(--bowire-text-tertiary);padding:2px;font-size:11px',
            title: 'Copy command',
            textContent: '⧉',
            onClick: function () {
                navigator.clipboard.writeText('bowire --enable-mcp-adapter').then(function () {
                    copyBtn.textContent = '✓';
                    setTimeout(function () { copyBtn.textContent = '⧉'; }, 1200);
                });
            }
        });
        cmdRow.appendChild(cmdCode);
        cmdRow.appendChild(copyBtn);
        statusBox.appendChild(cmdRow);
        statusBox.appendChild(el('span', {
            style: 'font-size:11px;color:var(--bowire-text-tertiary)',
            textContent: 'Restart Bowire with this flag, or set Bowire:EnableMcpAdapter=true in appsettings.'
        }));
    }

    // ---- Shortcuts ----
    function renderSettingsShortcuts() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', { className: 'bowire-settings-section-title', textContent: 'Keyboard Shortcuts' }));

        var shortcuts = [
            { key: 'Ctrl+Enter', desc: 'Execute request / Send message' },
            { key: '?', desc: 'Show/hide shortcuts overlay' },
            { key: 'Esc', desc: 'Close dialog / Stop streaming / Disconnect' },
            { key: '/', desc: 'Focus command palette' },
            { key: 't', desc: 'Toggle theme (Auto → Dark → Light)' },
            { key: 'f', desc: 'Toggle Form/JSON mode' },
            { key: 'r', desc: 'Repeat last call' },
            { key: 'j', desc: 'Next method (sidebar)' },
            { key: 'k', desc: 'Previous method (sidebar)' }
        ];

        var table = el('div', { className: 'bowire-settings-shortcuts' });
        for (var i = 0; i < shortcuts.length; i++) {
            table.appendChild(el('div', { className: 'bowire-settings-shortcut-row' },
                el('kbd', { className: 'bowire-settings-kbd', textContent: shortcuts[i].key }),
                el('span', { textContent: shortcuts[i].desc })
            ));
        }
        section.appendChild(table);
        return section;
    }

    // ---- AI (#63) ----
    // Per-user AI provider / endpoint / model. Reads /api/ai/status +
    // /api/ai/probe-local on first paint, writes via POST /api/ai/config.
    // Persisted via IBowireUserStore so the choice survives restart;
    // the runtime hot-swaps the active IChatClient so changes apply
    // without restarting the host. When the optional
    // Kuestenlogik.Bowire.Ai package isn't installed, /api/ai/status
    // returns 404 and the section renders an install hint instead.
    var aiSettingsState = {
        loaded: false,
        loading: false,
        status: null,         // last /api/ai/status body, or null when 404
        probe: null,          // last /api/ai/probe-local body
        draft: null,          // { providerId, endpoint, model, autoDetectLocal }
        saving: false,
        result: null          // { kind: 'ok'|'err', text }
    };

    function aiSettingsPrefix() {
        return (typeof config !== 'undefined' && config && config.prefix) ? config.prefix : '';
    }

    function loadAiSettings(force) {
        if (aiSettingsState.loading) return;
        if (aiSettingsState.loaded && !force) return;
        aiSettingsState.loading = true;
        var p = aiSettingsPrefix();
        // #116 Phase 3 — pass workspaceId so the backend resolves
        // override > global > defaults + reports whether this
        // workspace has its own override file.
        var wsParam = '';
        try { if (typeof activeWorkspaceId === 'string' && activeWorkspaceId) wsParam = '?workspaceId=' + encodeURIComponent(activeWorkspaceId); }
        catch { /* prologue not loaded yet — skip */ }
        Promise.all([
            fetch(p + '/api/ai/status' + wsParam).then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; }),
            fetch(p + '/api/ai/probe-local').then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; })
        ]).then(function (results) {
            aiSettingsState.status = results[0];
            aiSettingsState.probe = results[1];
            aiSettingsState.loaded = true;
            aiSettingsState.loading = false;
            // Seed the draft from current server state so a Save without
            // edits is a true no-op rather than rewriting fields to JS
            // defaults.
            if (aiSettingsState.status) {
                aiSettingsState.draft = {
                    providerId: aiSettingsState.status.providerId || 'ollama',
                    endpoint: aiSettingsState.status.endpoint || 'http://localhost:11434',
                    model: aiSettingsState.status.model || '',
                    autoDetectLocal: !!aiSettingsState.status.autoDetectLocal
                };
            }
            if (settingsOpen && settingsTab === 'ai') renderSettingsDialog();
        });
    }

    // #116 Phase 3 — drop the per-workspace override so this
    // workspace inherits the global default again. Issues a DELETE
    // to /api/ai/config?workspaceId=<id> which removes the override
    // file + hot-swaps the runtime back to global (unless
    // host-managed).
    function removeAiWorkspaceOverride() {
        if (aiSettingsState.saving) return;
        var wsId = '';
        try { if (typeof activeWorkspaceId === 'string') wsId = activeWorkspaceId; } catch { /* ignore */ }
        if (!wsId) return;
        aiSettingsState.saving = true;
        aiSettingsState.result = null;
        renderSettingsDialog();
        var p = aiSettingsPrefix();
        fetch(p + '/api/ai/config?workspaceId=' + encodeURIComponent(wsId), {
            method: 'DELETE'
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .then(function (resp) {
                aiSettingsState.saving = false;
                if (resp.ok) {
                    aiSettingsState.result = { kind: 'ok', text: 'Override removed. This workspace now inherits the global default.' };
                    loadAiSettings(true);
                    if (window.__bowireAi && window.__bowireAi.refreshStatus) window.__bowireAi.refreshStatus();
                } else {
                    aiSettingsState.result = { kind: 'err', text: problemTitle(resp.body, 'Remove failed (HTTP ' + resp.status + ').') };
                }
                renderSettingsDialog();
            })
            .catch(function (err) {
                aiSettingsState.saving = false;
                aiSettingsState.result = { kind: 'err', text: err && err.message ? err.message : 'Remove failed.' };
                renderSettingsDialog();
            });
    }

    // #116 Phase 3 — saveScope: 'global' writes ai-config.json (every
    // workspace inherits it). 'workspace' writes the per-workspace
    // override file. The Settings UI exposes both with explicit
    // buttons so the operator picks scope at save time.
    function saveAiSettings(saveScope) {
        if (aiSettingsState.saving || !aiSettingsState.draft) return;
        aiSettingsState.saving = true;
        aiSettingsState.result = null;
        renderSettingsDialog();
        var p = aiSettingsPrefix();
        var wsParam = '';
        if (saveScope === 'workspace') {
            try { if (typeof activeWorkspaceId === 'string' && activeWorkspaceId) wsParam = '?workspaceId=' + encodeURIComponent(activeWorkspaceId); }
            catch { /* prologue not loaded yet — fall back to global */ }
        }
        fetch(p + '/api/ai/config' + wsParam, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(aiSettingsState.draft)
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .then(function (resp) {
                aiSettingsState.saving = false;
                if (resp.ok) {
                    // Refresh status from the canonical post-save shape
                    // so the form mirrors what the server actually
                    // accepted (trimmed strings, applied defaults, &c.).
                    aiSettingsState.status = {
                        hasClient: !resp.body.hostManaged ? true : (aiSettingsState.status && aiSettingsState.status.hasClient),
                        hostManaged: !!resp.body.hostManaged,
                        providerId: resp.body.providerId,
                        endpoint: resp.body.endpoint,
                        model: resp.body.model,
                        autoDetectLocal: !!resp.body.autoDetectLocal
                    };
                    aiSettingsState.draft = {
                        providerId: resp.body.providerId,
                        endpoint: resp.body.endpoint,
                        model: resp.body.model,
                        autoDetectLocal: !!resp.body.autoDetectLocal
                    };
                    aiSettingsState.result = {
                        kind: 'ok',
                        text: resp.body.hostManaged
                            ? 'Saved to disk. Host-managed runtime — applies on next host start.'
                            : 'Saved. Next AI request uses the new provider / model.'
                    };
                    // Nudge the AI side-panel so its footer + composer
                    // reflect the new binding without waiting for the
                    // user to re-open the panel.
                    if (window.__bowireAi && window.__bowireAi.refreshStatus) {
                        window.__bowireAi.refreshStatus();
                    }
                } else {
                    aiSettingsState.result = {
                        kind: 'err',
                        text: problemTitle(resp.body, 'Save failed (HTTP ' + resp.status + ').')
                    };
                }
                renderSettingsDialog();
            })
            .catch(function (err) {
                aiSettingsState.saving = false;
                aiSettingsState.result = { kind: 'err', text: 'Network error: ' + (err && err.message ? err.message : err) };
                renderSettingsDialog();
            });
    }

    function renderSettingsAi() {
        var section = el('div', { className: 'bowire-settings-section' });
        // Section title flips to Assistant. The sub-section labels
        // below ('AI provider', 'AI model', 'AI endpoint') keep
        // 'AI' because that's the technology vocabulary the
        // operator configures — naming it Assistant there would
        // hide what the field actually selects.
        section.appendChild(el('h3', { className: 'bowire-settings-section-title', textContent: 'Assistant' }));

        if (!aiSettingsState.loaded) {
            section.appendChild(el('p', {
                className: 'bowire-settings-help',
                textContent: 'Loading AI configuration…'
            }));
            loadAiSettings(false);
            return section;
        }

        // Package missing — render install hint, no form. The Phase-1
        // hint engine still works without the package; we point users
        // at the optional NuGet so they understand what they're opting
        // into rather than seeing a broken-looking config screen.
        if (!aiSettingsState.status) {
            section.appendChild(el('p', {
                className: 'bowire-settings-help',
                textContent: 'The optional Kuestenlogik.Bowire.Ai package isn’t installed. The Phase‑1 hint engine in the AI side‑panel keeps working without it; install the package to add Ollama / LM Studio / cloud chat to the workbench.'
            }));
            section.appendChild(el('div', {
                className: 'bowire-settings-help',
                style: 'font-family: var(--bowire-font-mono, monospace); margin-top: 8px;',
                textContent: 'dotnet add package Kuestenlogik.Bowire.Ai'
            }));
            section.appendChild(el('p', {
                className: 'bowire-settings-help',
                style: 'margin-top: 8px;',
                textContent: 'Standalone bowire installs already ship the package — restart the workbench if you upgraded and the tab still says this.'
            }));
            return section;
        }

        var st = aiSettingsState.status;
        var draft = aiSettingsState.draft;
        var hostManaged = !!st.hostManaged;
        var probe = aiSettingsState.probe || {};

        // #116 Phase 3 — scope header. Tells the operator whether
        // the editor is showing the global config or a workspace
        // override, and exposes the path to switch between them.
        var wsName = null;
        try {
            if (typeof activeWorkspace === 'function') {
                var aw = activeWorkspace();
                if (aw) wsName = aw.name;
            }
        } catch { /* prologue not loaded — skip */ }
        if (wsName) {
            var scopeBar = el('div', { className: 'bowire-ai-scope-bar' });
            if (st.hasOverride) {
                scopeBar.appendChild(el('span', {
                    className: 'bowire-ai-scope-tag bowire-ai-scope-tag-override',
                    textContent: 'Workspace override'
                }));
                scopeBar.appendChild(el('span', {
                    className: 'bowire-ai-scope-desc',
                    textContent: 'These values apply only inside "' + wsName + '". Remove the override to inherit the global config.'
                }));
                scopeBar.appendChild(el('button', {
                    className: 'bowire-settings-action-btn bowire-ai-scope-remove',
                    textContent: 'Use global instead',
                    onClick: function () { removeAiWorkspaceOverride(); }
                }));
            } else {
                scopeBar.appendChild(el('span', {
                    className: 'bowire-ai-scope-tag bowire-ai-scope-tag-global',
                    textContent: 'Global'
                }));
                scopeBar.appendChild(el('span', {
                    className: 'bowire-ai-scope-desc',
                    textContent: 'No workspace-specific override. The global default applies inside "' + wsName + '" (and every other workspace).'
                }));
            }
            section.appendChild(scopeBar);
        }

        // Live status header — green dot when connected, yellow when
        // package installed but no client (e.g. unsupported provider id),
        // gray when host-managed.
        var statusClass = hostManaged
            ? 'bowire-ai-settings-status host-managed'
            : (st.hasClient ? 'bowire-ai-settings-status live' : 'bowire-ai-settings-status idle');
        var statusText = hostManaged
            ? 'Host-managed: the embedding host registered its own IChatClient. Saved values apply on next host start.'
            : (st.hasClient
                ? 'Connected: ' + (st.providerId || '(unknown)') + ' · ' + (st.model || '(default model)')
                : 'No live client. The next save reconfigures the runtime.');
        section.appendChild(el('div', { className: statusClass, textContent: statusText }));

        // Provider dropdown (Phase 2 set; cloud providers join the list
        // once #25 Phase 3 ships).
        section.appendChild(renderSettingsRow('Provider',
            'Backend that serves chat completions. Local providers (Ollama / LM Studio) require nothing leaves the machine. Cloud providers land with #25 Phase 3.',
            function () {
                var sel = el('select', { className: 'bowire-settings-select' });
                var options = [
                    { v: 'ollama', l: 'Ollama (local)' },
                    { v: 'lmstudio', l: 'LM Studio (local, Ollama-compatible)' }
                ];
                options.forEach(function (o) {
                    sel.appendChild(el('option', { value: o.v, textContent: o.l, selected: draft.providerId === o.v }));
                });
                sel.onchange = function () { draft.providerId = sel.value; };
                if (hostManaged) sel.setAttribute('disabled', 'disabled');
                return sel;
            }));

        // Endpoint text input. Defaults populated from current state so
        // editing only what you need is the path of least resistance.
        section.appendChild(renderSettingsRow('Endpoint',
            'Base URL of the provider. Ollama default: http://localhost:11434 · LM Studio default: http://localhost:1234. Use a remote host for shared GPU servers.',
            function () {
                var input = el('input', {
                    type: 'text',
                    className: 'bowire-settings-input',
                    value: draft.endpoint || '',
                    placeholder: 'http://localhost:11434'
                });
                input.oninput = function () { draft.endpoint = input.value; };
                if (hostManaged) input.setAttribute('disabled', 'disabled');
                return input;
            }));

        // Model: dropdown sourced from probe results when available,
        // free-text otherwise. Probe response shapes for the two
        // providers expose their loaded-model lists.
        section.appendChild(renderSettingsRow('Model',
            'Model id served by the provider. Use \'ollama pull <name>\' first; LM Studio uses the currently-loaded model. Empty falls back to the provider’s own default.',
            function () {
                var key = draft.providerId === 'lmstudio' ? 'lmstudio' : 'ollama';
                var hit = probe[key];
                var models = (hit && Array.isArray(hit.models)) ? hit.models.filter(function (m) { return !!m; }) : [];
                if (models.length === 0) {
                    var input = el('input', {
                        type: 'text',
                        className: 'bowire-settings-input',
                        value: draft.model || '',
                        placeholder: 'llama3.2:3b'
                    });
                    input.oninput = function () { draft.model = input.value; };
                    if (hostManaged) input.setAttribute('disabled', 'disabled');
                    return input;
                }
                var wrap = el('div', { className: 'bowire-settings-model-wrap' });
                var sel = el('select', { className: 'bowire-settings-select' });
                // Always include the current draft value as the first
                // option even when it isn't in the detected list — the
                // user may have a model the probe missed (different
                // host, not yet pulled, &c.).
                var seen = {};
                if (draft.model && models.indexOf(draft.model) === -1) {
                    sel.appendChild(el('option', { value: draft.model, textContent: draft.model + ' (current)', selected: true }));
                    seen[draft.model] = true;
                }
                models.forEach(function (m) {
                    if (seen[m]) return;
                    sel.appendChild(el('option', { value: m, textContent: m, selected: draft.model === m }));
                    seen[m] = true;
                });
                sel.onchange = function () { draft.model = sel.value; };
                if (hostManaged) sel.setAttribute('disabled', 'disabled');
                wrap.appendChild(sel);
                wrap.appendChild(el('span', {
                    className: 'bowire-settings-help',
                    style: 'margin-left: 8px;',
                    textContent: models.length + ' detected on ' + (hit.endpoint || 'local provider')
                }));
                return wrap;
            }));

        section.appendChild(renderSettingsToggle('Auto-detect local providers',
            'Probe 127.0.0.1:11434 (Ollama) and 127.0.0.1:1234 (LM Studio) on AI panel paint. Each probe times out at 300 ms. Off = fully offline, no local network calls.',
            draft.autoDetectLocal,
            function (v) { draft.autoDetectLocal = v; }));

        // #116 Phase 3 — two save buttons, explicit scope. The
        // operator picks whether the edit applies to every
        // workspace (global) or just this one (override).
        var saveRow = el('div', { className: 'bowire-settings-row bowire-settings-ai-save-row' });
        var saveGlobalBtn = el('button', {
            className: 'bowire-settings-action-btn',
            textContent: aiSettingsState.saving ? 'Saving…' : 'Save as global default',
            title: 'Persists to ai-config.json. Workspaces without their own override inherit these values.',
            onClick: function () { saveAiSettings('global'); }
        });
        var saveWsBtn = wsName ? el('button', {
            className: 'bowire-settings-action-btn bowire-ai-save-ws-btn',
            textContent: aiSettingsState.saving ? '…' : 'Save for "' + wsName + '" only',
            title: 'Persists to ai-config.<workspaceId>.json. Other workspaces keep the global config.',
            onClick: function () { saveAiSettings('workspace'); }
        }) : null;
        if (aiSettingsState.saving) {
            saveGlobalBtn.setAttribute('disabled', 'disabled');
            if (saveWsBtn) saveWsBtn.setAttribute('disabled', 'disabled');
        }
        if (hostManaged) {
            saveGlobalBtn.textContent = aiSettingsState.saving ? 'Saving…' : 'Save as global default (host-managed)';
        }
        var resultBox = el('span', { className: 'bowire-settings-ai-result' });
        if (aiSettingsState.result) {
            resultBox.classList.add(aiSettingsState.result.kind === 'ok' ? 'ok' : 'err');
            resultBox.textContent = aiSettingsState.result.text;
        }
        saveRow.appendChild(saveGlobalBtn);
        if (saveWsBtn) saveRow.appendChild(saveWsBtn);
        saveRow.appendChild(resultBox);
        section.appendChild(saveRow);

        // Allow-AI-to-invoke toggle. Session-only by design (#109): if
        // it persisted, an operator who turned it on for one focused
        // session could end up with a future cold-start that fires
        // real calls before they realise. So Settings is the canonical
        // UI for the switch, but localStorage stays out of the loop —
        // every reload starts back at off.
        section.appendChild(renderSettingsToggle(
            'Allow AI to invoke methods',
            'When on, the assistant can dispatch real calls via the bowire_invoke tool — every invocation is audited to ~/.bowire/.ai-actions.jsonl. Session-only: every workbench restart goes back to off.',
            typeof aiAllowInvoke !== 'undefined' && !!aiAllowInvoke,
            function (newVal) {
                if (typeof aiAllowInvoke !== 'undefined') aiAllowInvoke = newVal;
                // Re-render the chat panel if it's open so its
                // session-only banner reflects the new state.
                render();
            }
        ));

        // Refresh button — re-runs status + probe so a user who just
        // started Ollama can see the detected models without closing
        // and reopening the dialog.
        section.appendChild(renderSettingsAction(
            'Refresh detection',
            'Re-probe local providers and pull the current server-side configuration. Use this after starting Ollama / LM Studio.',
            'Refresh',
            function () { loadAiSettings(true); }
        ));

        return section;
    }

    // ---- Workspace pointer (#193 Phase 2 item 4) ----
    // Project-scope settings (URLs, environments, collections,
    // recordings, flows, plugin pins, storage mode, storage root)
    // live in the Workspace tree → Settings node so they round-trip
    // through .bww with the workspace. This pointer page makes the
    // split discoverable from inside the Settings dialog without
    // duplicating the workspace-settings UI here — a single jump
    // closes the modal and lands the operator on the workspace
    // detail panel.
    function renderSettingsWorkspacePointer() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', { className: 'bowire-settings-section-title',
            textContent: 'Workspace settings' }));

        var ws = (typeof activeWorkspace === 'function') ? activeWorkspace() : null;

        section.appendChild(el('p', { className: 'bowire-settings-row-hint',
            style: 'margin-bottom:14px;',
            textContent: 'These travel with the workspace file (.bww). A team member opening a checked-in workspace inherits exactly the set you save here:' }));

        var list = el('ul', { className: 'bowire-settings-scope-list' },
            el('li', { textContent: 'URLs + per-URL headers (Sources)' }),
            el('li', { textContent: 'Environments + globals + secrets layout' }),
            el('li', { textContent: 'Collections + recordings + flows' }),
            el('li', { textContent: 'Plugin pins — required protocols + version constraints' }),
            el('li', { textContent: 'Storage mode + storage root (where the workspace lives on disk)' })
        );
        section.appendChild(list);

        section.appendChild(el('p', { className: 'bowire-settings-row-hint',
            style: 'margin-top:14px;',
            textContent: ws
                ? 'Active workspace: ' + ws.name + '.'
                : 'No workspace selected. Pick one in the activity rail before opening the workspace settings.' }));

        var actions = el('div', { style: 'margin-top:14px;display:flex;gap:8px;' });
        var openBtn = el('button', {
            className: 'bowire-presets-btn',
            textContent: 'Open Workspace Settings',
            disabled: !ws,
            onClick: function () {
                closeSettings();
                // Land on the workspaces rail with the active
                // workspace selected. _renderWorkspaceSettingsDetail
                // takes over the main pane from there.
                if (typeof railMode !== 'undefined') {
                    railMode = 'workspaces';
                    try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                }
                if (ws && typeof workspacesSelectedId !== 'undefined') {
                    workspacesSelectedId = ws.id;
                }
                if (typeof render === 'function') render();
            }
        });
        actions.appendChild(openBtn);
        section.appendChild(actions);

        return section;
    }

    // ---- Data ----
    function renderSettingsData() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', { className: 'bowire-settings-section-title', textContent: 'Data Management' }));

        section.appendChild(renderSettingsAction(
            'Clear call history',
            'Remove all request history entries',
            'Clear History',
            function () {
                bowireConfirm('Clear all call history?', function () {
                    var backup = getHistory();
                    clearHistory();
                    toast('History cleared', 'success', {
                        undo: function () { restoreHistory(backup); },
                        logAction: { kind: 'history-clear',
                            title: 'Cleared call history (' + (Array.isArray(backup) ? backup.length : 0) + ' entries)' }
                    });
                }, { title: 'Clear History', danger: true, confirmText: 'Clear' });
            }
        ));

        section.appendChild(renderSettingsAction(
            'Clear favorites',
            'Remove all starred methods',
            'Clear Favorites',
            function () {
                bowireConfirm('Clear all favorites?', function () {
                    var backup = getFavorites();
                    localStorage.removeItem(wsKey(FAVORITES_KEY));
                    render();
                    toast('Favorites cleared', 'success', {
                        undo: function () { try { localStorage.setItem(wsKey(FAVORITES_KEY), JSON.stringify(backup)); } catch {} render(); },
                        logAction: { kind: 'favorites-clear',
                            title: 'Cleared favorites (' + (Array.isArray(backup) ? backup.length : 0) + ' entries)' }
                    });
                }, { title: 'Clear Favorites', danger: true, confirmText: 'Clear' });
            }
        ));

        section.appendChild(renderSettingsAction(
            'Reset all settings',
            'Clear localStorage and reload — returns everything to initial state',
            'Reset All',
            function () {
                bowireConfirm('Reset ALL Bowire data (history, favorites, environments, settings)?\n\nThis cannot be undone.', function () {
                    localStorage.clear();
                    toast('All data cleared \u2014 reloading...', 'success');
                    setTimeout(function () { location.reload(); }, 500);
                }, { title: 'Reset All Settings', danger: true, confirmText: 'Reset All' });
            },
            true // danger
        ));

        return section;
    }

    // ---- About dialog ----
    // Stand-alone modal opened from the topbar ?-button. Lives outside
    // the Settings overlay so the user can pop the credits / version /
    // license info without wading through the configuration sidebar.

    var aboutOpen = false;

    function openAbout() {
        aboutOpen = true;
        renderAboutDialog();
    }

    function closeAbout() {
        aboutOpen = false;
        renderAboutDialog();
    }

    function renderAboutDialog() {
        var existing = document.querySelector('.bowire-about-overlay');
        if (existing) existing.remove();
        if (!aboutOpen) return;

        var modal = el('div', { className: 'bowire-about-modal' },
            el('div', { className: 'bowire-about-modal-header' },
                el('span', { className: 'bowire-about-modal-title', textContent: 'About' }),
                el('button', {
                    className: 'bowire-settings-close',
                    innerHTML: svgIcon('close'),
                    title: 'Close (Esc)',
                    'aria-label': 'Close about dialog',
                    onClick: closeAbout
                })
            ),
            el('div', { className: 'bowire-about-modal-body' }, renderAboutContent())
        );

        var overlay = el('div', {
            className: 'bowire-about-overlay',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'About Bowire',
            onClick: function (e) { if (e.target === overlay) closeAbout(); }
        }, modal);

        document.body.appendChild(overlay);
    }

    // Curated list of the most prominent direct dependencies. Grouped so
    // the panel doesn't spam the user with all 18 individual NuGet refs.
    // For the full transitive set, point at GitHub / nuget.org.
    var BOWIRE_OSS_NOTICES = [
        { name: 'gRPC for .NET',                license: 'Apache-2.0', url: 'https://github.com/grpc/grpc-dotnet',                            note: 'Grpc.AspNetCore, Grpc.Net.Client, Grpc.Reflection, Google.Protobuf, Google.Api.CommonProtos' },
        { name: 'MQTTnet',                      license: 'MIT',        url: 'https://github.com/dotnet/MQTTnet',                              note: 'MQTTnet, MQTTnet.Server' },
        { name: 'GraphQL-Parser',               license: 'MIT',        url: 'https://github.com/graphql-dotnet/parser',                       note: '' },
        { name: 'Microsoft.OpenApi',            license: 'MIT',        url: 'https://github.com/microsoft/OpenAPI.NET',                       note: 'Microsoft.OpenApi, Microsoft.OpenApi.YamlReader' },
        { name: 'Microsoft.OData.Edm',          license: 'MIT',        url: 'https://github.com/OData/odata.net',                             note: '' },
        { name: 'Microsoft.AspNetCore.SignalR', license: 'MIT',        url: 'https://github.com/dotnet/aspnetcore',                           note: 'Microsoft.AspNetCore.SignalR.Client' },
        { name: 'Model Context Protocol SDK',   license: 'MIT',        url: 'https://github.com/modelcontextprotocol/csharp-sdk',             note: 'ModelContextProtocol, ModelContextProtocol.AspNetCore' },
        { name: 'SocketIOClient',               license: 'MIT',        url: 'https://github.com/doghappy/socket.io-client-csharp',            note: '' },
        { name: 'NuGet.Protocol',               license: 'Apache-2.0', url: 'https://github.com/NuGet/NuGet.Client',                          note: '' },
        { name: 'System.CommandLine',           license: 'MIT',        url: 'https://github.com/dotnet/command-line-api',                     note: '' },
        { name: '.NET runtime + ASP.NET Core',  license: 'MIT',        url: 'https://github.com/dotnet/runtime',                              note: 'Foundation libraries Bowire runs on' }
    ];

    function renderAboutContent() {
        var section = el('div', { className: 'bowire-settings-section' });

        // Branding header: the prominent Bowire wordmark logo sits at
        // the top of the About box. This is now the only place the
        // big horizontal logo shows in the running workbench — the
        // first-run Home dropped it per the portal redesign.
        section.appendChild(el('div', { className: 'bowire-settings-about-brand' },
            el('div', {
                className: 'bowire-settings-about-brand-logo',
                innerHTML: svgIcon('bowireLogo')
            }),
            el('div', { className: 'bowire-settings-about-brand-text' },
                el('div', { className: 'bowire-settings-about-brand-name', textContent: 'Bowire' }),
                el('div', { className: 'bowire-settings-about-brand-version', textContent: 'Version ' + (config.version || 'unknown') }),
                el('div', { className: 'bowire-settings-about-brand-tagline', textContent: 'The multi-protocol API workbench' })
            )
        ));

        // ---- Runtime stats ----
        section.appendChild(el('h4', { className: 'bowire-settings-about-subhead', textContent: 'Runtime' }));

        section.appendChild(el('div', { className: 'bowire-settings-about-row' },
            el('span', { className: 'bowire-settings-about-label', textContent: 'Mode' }),
            el('span', { textContent: uiMode })
        ));

        section.appendChild(el('div', { className: 'bowire-settings-about-row' },
            el('span', { className: 'bowire-settings-about-label', textContent: 'Protocols' }),
            el('span', { textContent: protocols.map(function (p) { return p.name; }).join(', ') || 'Loading...' })
        ));

        section.appendChild(el('div', { className: 'bowire-settings-about-row' },
            el('span', { className: 'bowire-settings-about-label', textContent: 'Services' }),
            el('span', { textContent: String(services.length) })
        ));

        var methods = services.reduce(function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
        section.appendChild(el('div', { className: 'bowire-settings-about-row' },
            el('span', { className: 'bowire-settings-about-label', textContent: 'Methods' }),
            el('span', { textContent: String(methods) })
        ));

        // ---- Project links ----
        section.appendChild(el('h4', { className: 'bowire-settings-about-subhead', textContent: 'Project' }));

        var linksRow = el('div', { className: 'bowire-settings-about-links' });
        linksRow.appendChild(aboutLink('GitHub Repository',  'https://github.com/Kuestenlogik/Bowire'));
        linksRow.appendChild(aboutLink('Documentation',      'https://bowire.io/docs/'));
        linksRow.appendChild(aboutLink('License (Apache-2.0)','https://github.com/Kuestenlogik/Bowire/blob/main/LICENSE'));
        linksRow.appendChild(aboutLink('Issues',             'https://github.com/Kuestenlogik/Bowire/issues'));
        section.appendChild(linksRow);

        // ---- Open-source notices (collapsible, default-collapsed) ----
        // Inline rather than a stacked sub-modal so the dialog stays a
        // single overlay and the user can copy text out without juggling
        // focus traps.
        var noticesOpen = false;
        var noticesWrap = el('div', { className: 'bowire-settings-about-notices' });
        var noticesToggle = el('button', {
            className: 'bowire-settings-about-notices-toggle',
            type: 'button',
            onClick: function () {
                noticesOpen = !noticesOpen;
                noticesBody.style.display = noticesOpen ? '' : 'none';
                noticesToggle.textContent = (noticesOpen ? '▾' : '▸') + ' Open-source notices';
            }
        });
        noticesToggle.textContent = '▸ Open-source notices';
        var noticesBody = el('div', { className: 'bowire-settings-about-notices-body', style: 'display:none' });
        noticesBody.appendChild(el('p', {
            className: 'bowire-settings-about-notices-lede',
            textContent: 'Bowire ships with the following third-party components. Each retains its original license; full transitive list available via dotnet list package --include-transitive.'
        }));
        for (var i = 0; i < BOWIRE_OSS_NOTICES.length; i++) {
            var n = BOWIRE_OSS_NOTICES[i];
            var entry = el('div', { className: 'bowire-settings-about-notices-entry' },
                el('a', {
                    className: 'bowire-settings-about-notices-name',
                    href: n.url, target: '_blank', rel: 'noopener',
                    textContent: n.name
                }),
                el('span', { className: 'bowire-settings-about-notices-license', textContent: n.license })
            );
            if (n.note) {
                entry.appendChild(el('div', { className: 'bowire-settings-about-notices-note', textContent: n.note }));
            }
            noticesBody.appendChild(entry);
        }
        noticesWrap.appendChild(noticesToggle);
        noticesWrap.appendChild(noticesBody);
        section.appendChild(noticesWrap);

        // ---- Made by Küstenlogik ----
        var lockupSrc = config.theme === 'dark' ? config.kuestenlogikLockupMono : config.kuestenlogikLockup;
        section.appendChild(el('div', { className: 'bowire-settings-about-madeby' },
            el('div', { className: 'bowire-settings-about-madeby-label', textContent: 'Made by' }),
            el('a', {
                href: 'https://github.com/Kuestenlogik',
                target: '_blank',
                rel: 'noopener',
                'aria-label': 'Küstenlogik'
            },
                el('img', { src: lockupSrc || '', alt: 'Küstenlogik', className: 'bowire-settings-about-madeby-lockup' })
            )
        ));

        return section;
    }

    function aboutLink(label, href) {
        return el('a', {
            className: 'bowire-settings-link bowire-settings-about-link',
            href: href,
            target: '_blank',
            rel: 'noopener',
            textContent: label
        });
    }

    // ---- Settings UI helpers ----
    function renderSettingsRow(label, description, controlFn) {
        var row = el('div', { className: 'bowire-settings-row' });
        row.appendChild(el('div', { className: 'bowire-settings-row-info' },
            el('div', { className: 'bowire-settings-row-label', textContent: label }),
            el('div', { className: 'bowire-settings-row-desc', textContent: description })
        ));
        row.appendChild(el('div', { className: 'bowire-settings-row-control' }, controlFn()));
        return row;
    }

    function renderSettingsToggle(label, description, value, onChange) {
        return renderSettingsRow(label, description, function () {
            var toggle = el('button', {
                className: 'bowire-settings-toggle' + (value ? ' on' : ''),
                onClick: function () {
                    value = !value;
                    onChange(value);
                    toggle.classList.toggle('on', value);
                    var dot = toggle.querySelector('.bowire-settings-toggle-dot');
                    if (dot) dot.style.transform = value ? 'translateX(16px)' : 'translateX(0)';
                }
            },
                el('span', {
                    className: 'bowire-settings-toggle-dot',
                    style: value ? 'transform:translateX(16px)' : ''
                })
            );
            return toggle;
        });
    }

    function renderSettingsAction(label, description, buttonText, onClick, danger) {
        var row = el('div', { className: 'bowire-settings-row' });
        row.appendChild(el('div', { className: 'bowire-settings-row-info' },
            el('div', { className: 'bowire-settings-row-label', textContent: label }),
            el('div', { className: 'bowire-settings-row-desc', textContent: description })
        ));
        row.appendChild(el('div', { className: 'bowire-settings-row-control' },
            el('button', {
                className: 'bowire-settings-action-btn' + (danger ? ' danger' : ''),
                textContent: buttonText,
                onClick: onClick
            })
        ));
        return row;
    }

    // ---- Plugin Settings ----
    // "Plugins" settings page — list every loaded plugin with an
    // enable/disable toggle. Disabling a plugin hides its protocol
    // chips, its landing-page switcher button, and drops its
    // discovered services from the sidebar; the plugin stays loaded
    // server-side so a re-enable is instant (no round trip).
    // Cache of installed plugins from /api/plugins and the latest
    // version each one resolves to on the configured feed. Both refresh
    // when the Plugins tab opens; renderManagePluginsSection() reads
    // from the cached snapshot so re-renders are instant.
    var installedPlugins = [];
    var latestVersions = {};
    var pluginPrereleaseToggle = false;
    var pluginActionInFlight = null;
    var pluginActionResult = null;
    // Tracks whether the Plugins tab has already kicked off its data
    // fetches in the current "session" of being open. Reset to false
    // when the dialog closes or the user switches tabs and comes back,
    // so the tab refreshes on re-entry without firing once per render.
    // Without this guard fetch{PluginHealth,InstalledPlugins}() re-trigger
    // renderSettingsDialog() on response → re-fires both fetches →
    // infinite render loop (#66).
    var pluginsTabFetchedThisOpen = false;

    function renderSettingsPlugins() {
        var section = el('div', { className: 'bowire-settings-section' });

        // Plugin health banner — surfaces every plugin in the
        // configured plugin-dir whose load didn't reach the Loaded /
        // AlreadyLoaded state. Fetch is fire-and-forget; the banner
        // appears once the response lands and a re-render runs. Empty
        // when every install is healthy so the section stays quiet
        // for the common case.
        section.appendChild(renderPluginHealthBanner());

        // "Manage installed plugins" — list every sibling-installed
        // plugin under ~/.bowire/plugins/ with its version and per-row
        // Update / Uninstall buttons. The protocol-toggle list below
        // covers the bundled + sibling protocols indiscriminately and
        // stays focused on enable/disable; this section is the
        // lifecycle counterpart.
        section.appendChild(renderManagePluginsSection());

        // Fire the refresh fetches exactly once per "tab opened" event,
        // not on every render. Each fetch's success handler calls
        // renderSettingsDialog() again to surface the new data — if we
        // fetched on every render, we'd be in a loop.
        if (!pluginsTabFetchedThisOpen) {
            pluginsTabFetchedThisOpen = true;
            fetchPluginHealth();
            fetchInstalledPlugins();
        }

        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: 'Installed protocol plugins'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-desc',
            textContent: 'Disable a plugin to hide its protocol chips, landing card, and discovered services. Plugins stay loaded so toggling is instant.'
        }));

        if (!protocols || protocols.length === 0) {
            section.appendChild(el('p', {
                className: 'bowire-settings-empty',
                textContent: 'No protocol plugins loaded.'
            }));
            return section;
        }

        var list = el('div', { className: 'bowire-settings-plugin-list' });
        for (var pi = 0; pi < protocols.length; pi++) {
            (function (p) {
                var row = el('label', { className: 'bowire-settings-plugin-row' });

                if (p.icon) {
                    row.appendChild(el('span', {
                        className: 'bowire-settings-plugin-icon',
                        innerHTML: p.icon
                    }));
                }
                var textBox = el('div', { className: 'bowire-settings-plugin-text' },
                    el('div', { className: 'bowire-settings-plugin-name', textContent: p.name }),
                    el('div', { className: 'bowire-settings-plugin-id', textContent: p.id })
                );
                row.appendChild(textBox);

                var cb = el('input', {
                    type: 'checkbox',
                    className: 'bowire-settings-plugin-toggle',
                    checked: isProtocolEnabled(p.id),
                    onChange: function (e) {
                        setProtocolEnabled(p.id, e.target.checked);
                        // Drop the plugin from the active protocol filter
                        // so a disabled plugin doesn't keep services hidden
                        // when it's re-enabled later.
                        if (!e.target.checked && typeof protocolFilter !== 'undefined') {
                            protocolFilter.delete(p.id);
                            if (typeof persistProtocolFilter === 'function') persistProtocolFilter();
                        }
                        render();
                    }
                });
                row.appendChild(cb);
                list.appendChild(row);
            })(protocols[pi]);
        }
        section.appendChild(list);
        return section;
    }

    /**
     * Fire-and-forget refresh of the plugin-health snapshot. Updates
     * the module-level `pluginHealth` array; re-renders the settings
     * dialog when fresh data arrives so the banner inside the Plugins
     * tab reflects the current loader state. Silent on transport
     * failures — surfacing a banner about the banner being broken
     * doesn't help the operator.
     */
    function fetchPluginHealth() {
        try {
            fetch(`${config.prefix}/api/plugins/health`)
                .then(function (resp) { return resp.ok ? resp.json() : null; })
                .then(function (body) {
                    if (!body || !Array.isArray(body.plugins)) return;
                    pluginHealth = body.plugins;
                    // Re-render only when the dialog is still open and
                    // sitting on the Plugins tab — anything else means
                    // the user has navigated away and a re-render would
                    // be wasted work.
                    if (settingsOpen && settingsTab === 'plugins') {
                        renderSettingsDialog();
                    }
                })
                .catch(function () { /* network failure — leave the cache as is */ });
        } catch { /* fetch threw synchronously — same handling */ }
    }

    /**
     * Pull the per-plugin failure rows out of the cached health
     * snapshot and render a banner above the protocol list. Returns
     * an empty fragment when every plugin is healthy so the section
     * stays quiet on the common case. Each row carries the package
     * id, the loader's status enum, and the human-readable error
     * message — same shape /api/plugins/health surfaces.
     */
    function renderPluginHealthBanner() {
        var unhealthy = pluginHealth.filter(function (r) {
            return r.status !== 'Loaded' && r.status !== 'AlreadyLoaded';
        });
        if (unhealthy.length === 0) {
            return el('div'); // empty placeholder, takes no space
        }
        var banner = el('div', { className: 'bowire-settings-plugin-health' });
        banner.appendChild(el('div', {
            className: 'bowire-settings-plugin-health-title',
            textContent: unhealthy.length === 1
                ? '1 plugin failed to load'
                : unhealthy.length + ' plugins failed to load'
        }));
        for (var i = 0; i < unhealthy.length; i++) {
            (function (r) {
                var row = el('div', { className: 'bowire-settings-plugin-health-row' });
                row.appendChild(el('span', {
                    className: 'bowire-settings-plugin-health-status status-' + r.status,
                    textContent: r.status
                }));
                row.appendChild(el('div', { className: 'bowire-settings-plugin-health-text' },
                    el('div', {
                        className: 'bowire-settings-plugin-health-pkg',
                        textContent: r.packageId
                    }),
                    el('div', {
                        className: 'bowire-settings-plugin-health-msg',
                        textContent: r.errorMessage || ''
                    })
                ));
                banner.appendChild(row);
            })(unhealthy[i]);
        }
        return banner;
    }

    /**
     * Manage-installed-plugins section. Lists every entry returned by
     * /api/plugins with installed version + 'update available' hint
     * (compared to the latest from /api/plugins/{id}/latest) + per-row
     * Update / Uninstall buttons. Pre-release toggle at the top
     * controls whether the latest-version lookup considers RC builds.
     */
    function renderManagePluginsSection() {
        var section = el('div', { className: 'bowire-settings-plugin-manage' });
        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: 'Installed (sibling) plugins'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-desc',
            textContent: 'Sibling plugins under ~/.bowire/plugins/ get Update / Uninstall buttons; bundled plugins (gRPC, REST, MQTT, …) ship inside the Bowire tool and update with dotnet tool update.'
        }));

        // Update-check status banner — shows whether the daily
        // background check is opted-in + when it last ran. The
        // "Check now" button forces an immediate /api/plugins/check-updates
        // round-trip without flipping the opt-in.
        section.appendChild(renderUpdateCheckBanner());

        // Pre-release toggle row
        var prereleaseRow = el('label', { className: 'bowire-settings-plugin-prerelease' });
        var cb = el('input', {
            type: 'checkbox',
            checked: pluginPrereleaseToggle,
            onChange: function (e) {
                pluginPrereleaseToggle = e.target.checked;
                latestVersions = {}; // force re-fetch with the new filter
                renderSettingsDialog();
                fetchLatestVersions();
            }
        });
        prereleaseRow.appendChild(cb);
        prereleaseRow.appendChild(el('span', {
            textContent: 'Include pre-release versions when checking for updates'
        }));
        section.appendChild(prereleaseRow);

        if (pluginActionResult) {
            section.appendChild(renderPluginActionBanner(pluginActionResult));
        }

        if (!installedPlugins.length) {
            section.appendChild(el('p', {
                className: 'bowire-settings-empty',
                textContent: 'No sibling plugins installed.'
            }));
            return section;
        }

        var list = el('div', { className: 'bowire-settings-plugin-manage-list' });
        for (var i = 0; i < installedPlugins.length; i++) {
            list.appendChild(renderManagedPluginRow(installedPlugins[i]));
        }
        section.appendChild(list);
        return section;
    }

    // Banner above the installed-plugins list: shows daily-check
    // opt-in status, last run timestamp, count of pending updates.
    // "Check now" runs an on-demand sweep regardless of opt-in (a
    // user click is always a direct action — only the *background*
    // call is gated).
    function renderUpdateCheckBanner() {
        var box = el('div', { className: 'bowire-settings-update-check' });
        var s = (typeof pluginUpdateCheckStatus !== 'undefined')
            ? pluginUpdateCheckStatus : { enabled: false, cached: null };

        var lines = [];
        if (s.enabled) {
            lines.push('Daily plugin-update check is enabled (every '
                + (s.intervalHours || 24) + ' h).');
        } else {
            lines.push('Daily plugin-update check is OFF — opt in via --update-check or Bowire:PluginUpdateCheck:Enabled=true. Manual checks via the button below always work.');
        }
        if (s.cached && s.cached.CheckedAt) {
            var n = pluginUpdateBadgeCount();
            lines.push('Last run: ' + new Date(s.cached.CheckedAt).toLocaleString()
                + ' — ' + (n === 0 ? 'all plugins up to date.'
                    : n + ' update(s) available.'));
        }
        box.appendChild(el('div', {
            className: 'bowire-settings-update-check-text',
            textContent: lines.join(' '),
        }));

        var checkBtn = el('button', {
            className: 'bowire-settings-update-check-btn',
            textContent: 'Check now',
            onClick: function () {
                checkBtn.disabled = true;
                checkBtn.textContent = 'Checking…';
                var qs = pluginPrereleaseToggle ? '?prerelease=true' : '';
                fetch(config.prefix + '/api/plugins/check-updates' + qs)
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (snapshot) {
                        if (snapshot) {
                            pluginUpdateCheckStatus = Object.assign(
                                {}, pluginUpdateCheckStatus, { cached: snapshot });
                        }
                        renderSettingsDialog();
                        if (typeof render === 'function') render();
                    })
                    .catch(function () { /* offline / NuGet down */ })
                    .finally(function () {
                        checkBtn.disabled = false;
                        checkBtn.textContent = 'Check now';
                    });
            },
        });
        box.appendChild(checkBtn);
        return box;
    }

    function renderManagedPluginRow(p) {
        var bundled = p.source === 'bundled';
        var row = el('div', {
            className: 'bowire-settings-plugin-manage-row'
                + (bundled ? ' is-bundled' : '')
        });

        var pkgId = p.packageId || p.PackageId || '';
        var version = p.version || p.Version || 'unknown';
        var latest = latestVersions[pkgId];
        // #167 — DisplayName is the user-facing identity (the protocol the
        // plugin contributes, e.g. "GraphQL"); packageId is the assembly /
        // NuGet name and now reads as small mono secondary text. Empty
        // DisplayName falls back to the assembly name so third-party
        // plugins without a manifest still render meaningfully.
        var displayName = p.displayName || p.DisplayName || '';
        var description = p.description || p.Description || '';

        var textBox = el('div', { className: 'bowire-settings-plugin-manage-text' });
        var idLine = el('div', { className: 'bowire-settings-plugin-manage-id-line' });
        idLine.appendChild(el('span', {
            className: 'bowire-settings-plugin-manage-id',
            textContent: displayName || pkgId
        }));
        if (bundled) {
            idLine.appendChild(el('span', {
                className: 'bowire-settings-plugin-manage-badge',
                textContent: 'bundled',
                title: 'Ships with the bowire tool — updated via `dotnet tool update -g Kuestenlogik.Bowire.Tool`'
            }));
        }
        textBox.appendChild(idLine);

        if (description) {
            textBox.appendChild(el('div', {
                className: 'bowire-settings-plugin-manage-desc',
                textContent: description
            }));
        }

        var versionLine = el('div', { className: 'bowire-settings-plugin-manage-version' });
        // Show the assembly id as small mono secondary text whenever we
        // have a real DisplayName to lead with — keeps the assembly name
        // discoverable (for `bowire plugin uninstall <id>`, NuGet lookup,
        // &c) without making it the heading.
        if (displayName && pkgId && displayName !== pkgId) {
            versionLine.appendChild(el('span', {
                className: 'bowire-settings-plugin-manage-asm',
                textContent: pkgId
            }));
        }
        versionLine.appendChild(el('span', { textContent: version }));
        if (!bundled && latest && latest !== version) {
            versionLine.appendChild(el('span', {
                className: 'bowire-settings-plugin-manage-update',
                textContent: '→ ' + latest + ' available'
            }));
        }
        textBox.appendChild(versionLine);
        row.appendChild(textBox);

        var actions = el('div', { className: 'bowire-settings-plugin-manage-actions' });
        var hasUpdate = !bundled && latest && latest !== version;
        var busy = pluginActionInFlight === pkgId;

        var updateBtn = el('button', {
            type: 'button',
            className: 'bowire-settings-plugin-btn'
                + (hasUpdate ? ' bowire-settings-plugin-btn-accent' : ''),
            disabled: bundled || busy,
            title: bundled
                ? 'Bundled plugin — run `dotnet tool update -g Kuestenlogik.Bowire.Tool` to update'
                : '',
            textContent: busy ? 'Working…' : 'Update',
            onClick: function () { if (!bundled) runPluginAction(pkgId, 'update'); }
        });
        actions.appendChild(updateBtn);

        var uninstallBtn = el('button', {
            type: 'button',
            className: 'bowire-settings-plugin-btn bowire-settings-plugin-btn-danger',
            disabled: bundled || busy,
            title: bundled ? 'Bundled plugins cannot be uninstalled separately' : '',
            textContent: 'Uninstall',
            onClick: function () {
                if (bundled) return;
                bowireConfirm(
                    'Uninstall ' + pkgId + '? The package is removed from disk and the workbench restarts to unload it.',
                    function () { runPluginAction(pkgId, 'uninstall'); },
                    { title: 'Uninstall plugin', confirmText: 'Uninstall', danger: true }
                );
            }
        });
        actions.appendChild(uninstallBtn);

        // Inspect — pulls /api/plugins/protocols on demand to cross-
        // reference which protocols this plugin contributes, then
        // pops a modal with the consolidated metadata. UI analogue
        // of `bowire plugin inspect <packageId>`.
        var inspectBtn = el('button', {
            type: 'button',
            className: 'bowire-settings-plugin-btn',
            disabled: busy,
            title: 'Show package metadata + protocol contributions',
            textContent: 'Inspect',
            onClick: function () { openPluginInspectModal(p); }
        });
        actions.appendChild(inspectBtn);

        row.appendChild(actions);
        return row;
    }

    function openPluginInspectModal(plugin) {
        var pkgId = plugin.packageId || plugin.PackageId || '';
        var overlay = el('div', {
            id: 'bowire-plugin-inspect-overlay',
            className: 'bowire-modal-overlay',
            onClick: function (e) { if (e.target === overlay) overlay.remove(); }
        });
        var panel = el('div', { className: 'bowire-modal-panel bowire-plugin-inspect-panel' });

        var header = el('div', { className: 'bowire-modal-header' });
        header.appendChild(el('h2', { textContent: pkgId }));
        header.appendChild(el('button', {
            className: 'bowire-modal-close',
            innerHTML: svgIcon('x'),
            title: 'Close',
            onClick: function () { overlay.remove(); }
        }));
        panel.appendChild(header);

        var body = el('div', { className: 'bowire-modal-body' });

        // Static fields the list endpoint already gave us.
        var dl = el('dl', { className: 'bowire-plugin-inspect-meta' });
        function row(label, value) {
            if (value === null || value === undefined || value === '') return;
            dl.appendChild(el('dt', { textContent: label }));
            dl.appendChild(el('dd', { textContent: String(value) }));
        }
        row('Version', plugin.version || plugin.Version);
        row('Source', plugin.source === 'bundled'
            ? 'bundled (ships with the bowire tool)'
            : 'sibling (~/.bowire/plugins/' + pkgId + ')');
        if (plugin.installedAt || plugin.InstalledAt) {
            row('Installed', plugin.installedAt || plugin.InstalledAt);
        }
        if (plugin.sources || plugin.Sources) {
            var s = plugin.sources || plugin.Sources;
            row('Feed sources', Array.isArray(s) ? s.join(', ') : String(s));
        }
        body.appendChild(dl);

        // Protocol contributions — fetch /api/plugins/protocols and
        // filter the catalog to entries whose packageId matches.
        var contribTitle = el('h3', {
            className: 'bowire-plugin-inspect-section',
            textContent: 'Protocol contributions'
        });
        body.appendChild(contribTitle);

        var contribBox = el('div', { className: 'bowire-plugin-inspect-contrib' });
        contribBox.appendChild(el('span', { textContent: 'Loading…' }));
        body.appendChild(contribBox);

        fetch(config.prefix + '/api/plugins/protocols')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                contribBox.innerHTML = '';
                if (!data || !data.catalog) {
                    contribBox.appendChild(el('span', { textContent: 'Catalog unavailable.' }));
                    return;
                }
                var hits = Object.keys(data.catalog).filter(function (proto) {
                    return data.catalog[proto] && data.catalog[proto].toLowerCase() === pkgId.toLowerCase();
                });
                if (hits.length === 0) {
                    contribBox.appendChild(el('span', {
                        className: 'bowire-plugin-inspect-empty',
                        textContent: 'No protocol contributions resolved from this package. (Either a non-protocol plugin, or the package id does not match the catalog mapping — try the CLI: `bowire plugin inspect ' + pkgId + '`.)'
                    }));
                } else {
                    var ul = el('ul', { className: 'bowire-plugin-inspect-contrib-list' });
                    hits.forEach(function (proto) {
                        var loaded = (data.loaded || []).indexOf(proto) >= 0;
                        ul.appendChild(el('li', {},
                            el('code', { textContent: proto }),
                            el('span', {
                                className: 'bowire-plugin-inspect-loaded'
                                    + (loaded ? ' is-loaded' : ' is-unloaded'),
                                textContent: loaded ? 'loaded' : 'declared (not loaded)'
                            })
                        ));
                    });
                    contribBox.appendChild(ul);
                }
            })
            .catch(function () {
                contribBox.innerHTML = '';
                contribBox.appendChild(el('span', { textContent: 'Catalog fetch failed.' }));
            });

        // Footer hint for the deeper CLI surface
        body.appendChild(el('p', {
            className: 'bowire-plugin-inspect-cli-hint',
            textContent: 'For deeper introspection (load context, assemblies, IBowireMockEmitter contributions), run: bowire plugin inspect ' + pkgId
        }));

        panel.appendChild(body);
        overlay.appendChild(panel);
        document.body.appendChild(overlay);
    }

    function renderPluginActionBanner(result) {
        var ok = result.ok;
        var banner = el('div', {
            className: 'bowire-settings-plugin-action-banner '
                + (ok ? 'is-ok' : 'is-error'),
        });
        banner.appendChild(el('div', {
            className: 'bowire-settings-plugin-action-summary',
            textContent: (ok ? '✓ ' : '✗ ') + result.summary
        }));
        if (result.detail) {
            banner.appendChild(el('pre', {
                className: 'bowire-settings-plugin-action-detail',
                textContent: result.detail
            }));
        }
        return banner;
    }

    function fetchInstalledPlugins() {
        try {
            fetch(config.prefix + '/api/plugins')
                .then(function (resp) { return resp.ok ? resp.json() : null; })
                .then(function (body) {
                    if (!body || !Array.isArray(body.plugins)) return;
                    installedPlugins = body.plugins;
                    if (settingsOpen && settingsTab === 'plugins') {
                        renderSettingsDialog();
                    }
                    fetchLatestVersions();
                })
                .catch(function () { /* leave cache as-is */ });
        } catch { /* fetch threw synchronously */ }
    }

    function fetchLatestVersions() {
        // One fetch per *sibling* plugin. Bundled ones don't need a
        // latest-lookup — they're updated en bloc via `dotnet tool
        // update` and the manage panel shows them as a read-only
        // inventory. nuget.org's v3-flatcontainer is a CDN endpoint
        // so 5–10 parallel sibling lookups finish in <1 s.
        for (var i = 0; i < installedPlugins.length; i++) {
            (function (p) {
                if (p.source === 'bundled') return;
                var id = p.packageId || p.PackageId;
                if (!id) return;
                var qs = pluginPrereleaseToggle ? '?prerelease=true' : '';
                fetch(config.prefix + '/api/plugins/' + encodeURIComponent(id) + '/latest' + qs)
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (body) {
                        if (!body || !body.latest) return;
                        latestVersions[id] = body.latest;
                        if (settingsOpen && settingsTab === 'plugins') {
                            renderSettingsDialog();
                        }
                    })
                    .catch(function () { /* offline / NuGet down */ });
            })(installedPlugins[i]);
        }
    }

    function runPluginAction(packageId, verb) {
        pluginActionInFlight = packageId;
        pluginActionResult = null;
        renderSettingsDialog();

        var url, method, body;
        if (verb === 'update') {
            url = config.prefix + '/api/plugins/' + encodeURIComponent(packageId) + '/update';
            method = 'POST';
            body = JSON.stringify({ prerelease: pluginPrereleaseToggle });
        } else if (verb === 'uninstall') {
            url = config.prefix + '/api/plugins/' + encodeURIComponent(packageId);
            method = 'DELETE';
            body = null;
        } else {
            return;
        }

        fetch(url, {
            method: method,
            headers: { 'Content-Type': 'application/json' },
            body: body,
        })
            .then(function (resp) {
                return resp.json().then(function (data) {
                    return { ok: resp.ok, data: data };
                });
            })
            .then(function (result) {
                pluginActionInFlight = null;
                pluginActionResult = result.ok
                    ? {
                        ok: true,
                        summary: verb === 'update'
                            ? 'Updated ' + packageId
                            : 'Uninstalled ' + packageId,
                        detail: (result.data && result.data.output) || ''
                    }
                    : {
                        ok: false,
                        summary: 'Failed to ' + verb + ' ' + packageId,
                        detail: (result.data && (result.data.detail || result.data.stdout || result.data.output)) || ''
                    };
                fetchInstalledPlugins();
            })
            .catch(function (err) {
                pluginActionInFlight = null;
                pluginActionResult = {
                    ok: false,
                    summary: 'Failed to ' + verb + ' ' + packageId,
                    detail: String(err)
                };
                renderSettingsDialog();
            });
    }

    function renderPluginSettings(plugin) {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', { className: 'bowire-settings-section-title',
            textContent: plugin.name + ' Settings' }));

        // Per-plugin enable/disable toggle. Mirrors the row in the
        // Plugins category but lives on the plugin's own page so users
        // who navigated here directly can flip the switch without
        // hopping back. When the user disables, jump to the Plugins
        // tab so they don't end up looking at a sidebar entry that's
        // about to disappear.
        section.appendChild(renderSettingsToggle(
            'Enabled',
            'When disabled, this protocol’s services are hidden from the sidebar, chip suggestions, and the protocol switcher. The plugin stays loaded server-side so flipping it back on is instant.',
            isProtocolEnabled(plugin.id),
            function (v) {
                setProtocolEnabled(plugin.id, v);
                if (!v && typeof protocolFilter !== 'undefined') {
                    protocolFilter.delete(plugin.id);
                    if (typeof persistProtocolFilter === 'function') persistProtocolFilter();
                }
                if (!v) {
                    settingsTab = 'plugins';
                    renderSettingsDialog();
                }
                render();
            }
        ));

        if (!plugin.settings || plugin.settings.length === 0) {
            section.appendChild(el('div', { style: 'color:var(--bowire-text-tertiary);font-size:13px;margin-top:8px',
                textContent: 'This plugin has no configurable settings.' }));
            return section;
        }

        for (var si = 0; si < plugin.settings.length; si++) {
            (function (setting) {
                var storageKey = 'bowire_plugin_' + plugin.id + '_' + setting.key;
                if (setting.type === 'bool') {
                    var current = localStorage.getItem(storageKey);
                    var val = current !== null ? current === 'true' : !!setting.defaultValue;
                    section.appendChild(renderSettingsToggle(
                        setting.label, setting.description || '', val,
                        function (v) { localStorage.setItem(storageKey, v ? 'true' : 'false'); }
                    ));
                } else if (setting.type === 'number') {
                    section.appendChild(renderSettingsRow(setting.label, setting.description || '', function () {
                        var stored = localStorage.getItem(storageKey);
                        return el('input', {
                            id: 'bowire-plugin-setting-' + plugin.id + '-' + setting.key,
                            type: 'number', className: 'bowire-settings-input',
                            value: stored !== null ? stored : String(setting.defaultValue || ''),
                            style: 'width:80px',
                            onChange: function (e) { localStorage.setItem(storageKey, e.target.value); }
                        });
                    }));
                } else if (setting.type === 'select' && setting.options) {
                    section.appendChild(renderSettingsRow(setting.label, setting.description || '', function () {
                        var stored = localStorage.getItem(storageKey);
                        var cur = stored !== null ? stored : String(setting.defaultValue || '');
                        var select = el('select', {
                            id: 'bowire-plugin-setting-' + plugin.id + '-' + setting.key,
                            className: 'bowire-settings-select',
                            onChange: function (e) { localStorage.setItem(storageKey, e.target.value); }
                        });
                        for (var oi = 0; oi < setting.options.length; oi++) {
                            var opt = el('option', { value: setting.options[oi].value, textContent: setting.options[oi].label });
                            if (setting.options[oi].value === cur) opt.selected = true;
                            select.appendChild(opt);
                        }
                        return select;
                    }));
                } else {
                    section.appendChild(renderSettingsRow(setting.label, setting.description || '', function () {
                        var stored = localStorage.getItem(storageKey);
                        return el('input', {
                            id: 'bowire-plugin-setting-' + plugin.id + '-' + setting.key,
                            type: 'text', className: 'bowire-settings-input',
                            value: stored !== null ? stored : String(setting.defaultValue || ''),
                            onChange: function (e) { localStorage.setItem(storageKey, e.target.value); }
                        });
                    }));
                }
            })(plugin.settings[si]);
        }
        return section;
    }

    function getPluginSetting(pluginId, key, defaultValue) {
        var stored = localStorage.getItem('bowire_plugin_' + pluginId + '_' + key);
        if (stored === null) return defaultValue;
        if (defaultValue === true || defaultValue === false) return stored === 'true';
        if (typeof defaultValue === 'number') return Number(stored);
        return stored;
    }
