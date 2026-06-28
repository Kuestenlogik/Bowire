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
            leaf('rails', 'Rail modes', 'layers'),
            leaf('modules', 'Modules', 'plug'),
            leaf('shortcuts', 'Shortcuts', 'list'),
            leaf('data', 'Data', 'trash'),
            leaf('ai', 'Assistant', 'spark'),
            // #309 — Catalogue provider picker. Sits next to Assistant
            // because both are "what backend talks to Bowire": Assistant
            // is the AI backend, Discovery is the URL catalogue backend.
            leaf('discovery', 'Discovery', 'search'),
            // #153 UI phase — Tools surface lists running reverse-proxy
            // hosts started via the topbar Tools menu. Stays empty for
            // installs that never start one (no rows + a hint), so the
            // entry doesn't lie about activity that isn't there.
            leaf('tools', 'Tools', 'plug')
        ];

        // Per-plugin children. The Plugins group node itself routes to
        // the overview page on click — no separate "All plugins" entry
        // (cleaner tree shape, matches MudBlazor NavMenu where the
        // parent is the destination + chevron handles expansion).
        var pluginChildren = [];
        for (var pi = 0; pi < protocols.length; pi++) {
            var p = protocols[pi];
            if (!isProtocolEnabled(p.id)) continue;
            // The previous filter dropped plugins without a
            // p.settings array, which hid REST / gRPC / MCP / GraphQL
            // / etc. from the tree entirely — even though those
            // plugins still benefit from a dedicated detail page
            // (enable/disable toggle, "no configurable settings"
            // notice, plugin description). renderPluginSettings
            // already handles the empty case, so the filter just
            // suppressed a useful navigation row.
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

        // Bug 1 — installed UI extensions also belong under the Plugins
        // group so the operator can SEE that, e.g., the MapLibre viewer
        // is loaded without scrolling the right-side overview pane.
        // Operator feedback: "ich sehe nur tacticalapi" — they expanded
        // the Plugins tree expecting Map to appear next to the protocol
        // chips, didn't find it, and concluded the map plugin wasn't
        // installed. Routing each extension child to the Plugins
        // overview parks the operator at the same page that lists the
        // Installed UI extensions section, where the extension already
        // shows with kind / capability chips + an Installed pill.
        for (var exi = 0; exi < installedExtensions.length; exi++) {
            (function (ext) {
                var extId = ext.id || ext.Id || '';
                if (!extId) return;
                var extTabId = 'extension-' + extId;
                pluginChildren.push({
                    id: 'settings:' + extTabId,
                    label: extensionDisplayName(extId),
                    icon: 'layers',
                    selected: settingsTab === extTabId,
                    onClick: function () {
                        // Operator: 'klick auf maplibre settings switched
                        // zurück auf plugins root im settings tree'.
                        // Routing to a dedicated `extension-<id>` tab keeps
                        // the row selected and shows the extension's own
                        // detail page instead of bouncing back to the
                        // protocols overview.
                        settingsTab = extTabId;
                        renderSettingsDialog();
                    }
                });
            })(installedExtensions[exi]);
        }

        // Plugins group — click on the label routes to the overview
        // (enable toggles, install hints); the chevron toggles
        // expansion independently. Default-open when the operator is
        // already on a plugin tab so the active row is visible
        // without an extra click.
        var pluginsKey = 'plugins';
        var pluginsActive = settingsTab === 'plugins'
            || settingsTab.indexOf('plugin-') === 0
            || settingsTab.indexOf('extension-') === 0;
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
        // Bug 1 — kick off the /api/plugins fetch on dialog open
        // regardless of which tab the operator lands on. The tree
        // surfaces installed UI extensions as child rows under the
        // Plugins group, so a user opening Settings → General also
        // needs the extension list cached for the tree to render
        // them. Previously the fetch was deferred until the Plugins
        // tab itself rendered, hiding the tree rows on first paint.
        if (!pluginsTabFetchedThisOpen) {
            pluginsTabFetchedThisOpen = true;
            fetchPluginHealth();
            fetchInstalledPlugins();
        }
        renderSettingsDialog();
    }

    function closeSettings() {
        settingsOpen = false;
        // Reset the per-open one-shot guard so reopening the dialog
        // re-fetches plugin data. See pluginsTabFetchedThisOpen in
        // renderSettingsPlugins() for the loop-prevention story.
        pluginsTabFetchedThisOpen = false;
        // #309 — drop the per-open transient result strips so the
        // operator doesn't see a stale "Saved" / "Test failed" line
        // next time they open the Discovery tab. Server-side override
        // state will re-fetch on next open.
        if (typeof discoveryState === 'object' && discoveryState) {
            discoveryState.loaded = false;
            discoveryState.saveResult = null;
            discoveryState.testResult = null;
            discoveryState.refreshResult = null;
        }
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
        } else if (settingsTab === 'rails') {
            rightPanel.appendChild(renderSettingsRails());
        } else if (settingsTab === 'modules') {
            rightPanel.appendChild(renderSettingsModules());
        } else if (settingsTab === 'shortcuts') {
            rightPanel.appendChild(renderSettingsShortcuts());
        } else if (settingsTab === 'data') {
            rightPanel.appendChild(renderSettingsData());
        } else if (settingsTab === 'ai') {
            rightPanel.appendChild(renderSettingsAi());
        } else if (settingsTab === 'discovery') {
            rightPanel.appendChild(renderSettingsDiscovery());
        } else if (settingsTab === 'tools') {
            rightPanel.appendChild(renderSettingsTools());
        } else if (settingsTab === 'plugins') {
            rightPanel.appendChild(renderSettingsPlugins());
        } else if (settingsTab === 'workspace') {
            rightPanel.appendChild(renderSettingsWorkspacePointer());
        } else if (settingsTab.indexOf('plugin-') === 0) {
            var pluginId = settingsTab.substring(7);
            var plugin = protocols.find(function (p) { return p.id === pluginId; });
            if (plugin) rightPanel.appendChild(renderPluginSettings(plugin));
        } else if (settingsTab.indexOf('extension-') === 0) {
            var extId = settingsTab.substring(10);
            var ext = (installedExtensions || []).find(function (e) { return (e.id || e.Id) === extId; });
            if (ext) rightPanel.appendChild(renderExtensionSettings(ext));
            else rightPanel.appendChild(renderSettingsPlugins());
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

    // ---- #248 Phase 1 — Rail modes editor ----
    //
    // Renders the Settings → Rail modes panel. Lists every entry in
    // _railModes (excluding hideFromRail = internal-only modes) with
    // a checkbox; always-on modes show as a non-interactive row so
    // the operator sees them but can't turn them off (greyed checkmark
    // + "Built-in" badge). Toggleable modes carry a real checkbox
    // that writes to bowire_enabled_rails on click and re-renders so
    // the rail strip updates in place.
    function renderSettingsRails() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: 'Rail modes'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-hint',
            textContent: 'Show or hide rail icons in the left strip. Always-on modes are required for Bowire to work and can’t be turned off. Disabled modes stay reachable via the command palette and deep links — only the rail icon disappears.'
        }));
        if (typeof _railModes === 'undefined' || !Array.isArray(_railModes)) {
            section.appendChild(el('div', {
                className: 'bowire-settings-section-empty',
                textContent: 'Rail catalogue not loaded.'
            }));
            return section;
        }
        // #294 — ALWAYS_ON_RAIL_MODES now comes from descriptors
        // (rail.alwaysOn=true in IBowireRailContribution). Default
        // fallback list dropped — settings should never lie about
        // what's locked; the contributor catalogue is the source of
        // truth. Empty list is acceptable: it just means every
        // discovered rail renders in the "Toggleable" section.
        var alwaysOn = (typeof ALWAYS_ON_RAIL_MODES !== 'undefined' && Array.isArray(ALWAYS_ON_RAIL_MODES))
            ? ALWAYS_ON_RAIL_MODES : [];

        function _renderModeRow(mode, locked) {
            var enabled = locked ? true
                : (typeof isRailEnabled === 'function' ? isRailEnabled(mode.id) : true);
            var row = el('label', {
                className: 'bowire-settings-rail-row' + (locked ? ' is-locked' : ''),
                title: locked
                    ? 'Always-on — cannot be disabled'
                    : (enabled ? 'Currently visible on the rail' : 'Currently hidden — toggle to show')
            });
            var input = el('input', {
                type: 'checkbox',
                className: 'bowire-settings-rail-checkbox',
                checked: enabled ? true : undefined,
                disabled: locked ? true : undefined,
                onChange: function (e) {
                    if (locked) return;
                    if (typeof setRailEnabled === 'function') {
                        setRailEnabled(mode.id, !!e.target.checked);
                    }
                    if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
                    if (typeof render === 'function') render();
                }
            });
            row.appendChild(input);
            row.appendChild(el('span', {
                className: 'bowire-settings-rail-icon',
                innerHTML: svgIcon(mode.icon || 'square')
            }));
            row.appendChild(el('span', {
                className: 'bowire-settings-rail-label',
                textContent: mode.label
            }));
            if (locked) {
                row.appendChild(el('span', {
                    className: 'bowire-settings-rail-badge',
                    textContent: 'Built-in'
                }));
            }
            return row;
        }

        // Group 1 — always on
        var lockedSub = el('div', {
            className: 'bowire-settings-rail-subhead',
            textContent: 'Always available'
        });
        section.appendChild(lockedSub);
        var lockedList = el('div', { className: 'bowire-settings-rail-list' });
        _railModes.forEach(function (m) {
            if (m.hideFromRail) return;
            if (alwaysOn.indexOf(m.id) < 0) return;
            lockedList.appendChild(_renderModeRow(m, true));
        });
        section.appendChild(lockedList);

        // Group 2 — toggleable
        var toggleSub = el('div', {
            className: 'bowire-settings-rail-subhead',
            style: 'margin-top:12px;',
            textContent: 'Toggleable'
        });
        section.appendChild(toggleSub);
        var toggleList = el('div', { className: 'bowire-settings-rail-list' });
        _railModes.forEach(function (m) {
            if (m.hideFromRail) return;
            if (alwaysOn.indexOf(m.id) >= 0) return;
            toggleList.appendChild(_renderModeRow(m, false));
        });
        section.appendChild(toggleList);
        return section;
    }

    // ---- #310 — Modules editor ----
    //
    // Settings → Modules. Lists every entry in __BOWIRE_CONFIG__.modules
    // (descriptors contributed by IBowireModuleContribution) with a
    // workspace-scoped Enabled toggle per row. Mirrors the Rail-modes
    // panel pattern from #248, with two differences:
    //   - persistence is per-workspace via wsKey() (not cross-workspace);
    //     different projects may want different module sets
    //   - there's no "always-on" sub-group: every module is opt-out, and
    //     hosts that don't ship a module's package don't see its row at
    //     all (descriptor never reaches __BOWIRE_CONFIG__.modules — same
    //     mechanic as rails for a missing package).
    function renderSettingsModules() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: 'Modules'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-hint',
            textContent: 'Enable or disable installed Bowire modules (AI Assistant, future variable-resolver, …). When disabled, the module’s UI hides and its hooks no-op until you turn it back on. The choice is per-workspace.'
        }));

        var cfg = (typeof config !== 'undefined' && config) ? config : {};
        var modules = Array.isArray(cfg.modules) ? cfg.modules : [];
        if (modules.length === 0) {
            section.appendChild(el('div', {
                className: 'bowire-settings-section-empty',
                textContent: 'No modules installed. Reference a Bowire module package (e.g. Kuestenlogik.Bowire.Ai) to see entries here.'
            }));
            return section;
        }

        var list = el('div', { className: 'bowire-settings-module-list' });
        modules.forEach(function (mod) {
            if (!mod || !mod.id) return;
            var enabled = (typeof isModuleEnabled === 'function')
                ? isModuleEnabled(mod.id)
                : (mod.defaultEnabled !== false);
            var row = el('label', {
                className: 'bowire-settings-module-row',
                title: enabled
                    ? 'Currently active in this workspace'
                    : 'Currently disabled in this workspace — toggle to enable'
            });
            var input = el('input', {
                type: 'checkbox',
                className: 'bowire-settings-module-checkbox',
                checked: enabled ? true : undefined,
                onChange: function (e) {
                    if (typeof setModuleEnabled === 'function') {
                        setModuleEnabled(mod.id, !!e.target.checked);
                    }
                    if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
                    if (typeof render === 'function') render();
                }
            });
            row.appendChild(input);
            var info = el('div', { className: 'bowire-settings-module-info' });
            info.appendChild(el('div', {
                className: 'bowire-settings-module-label',
                textContent: mod.label || mod.id
            }));
            if (mod.description) {
                info.appendChild(el('div', {
                    className: 'bowire-settings-module-desc',
                    textContent: mod.description
                }));
            }
            row.appendChild(info);
            list.appendChild(row);
        });
        section.appendChild(list);
        return section;
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
    //
    // Provider matrix (#25 ADR):
    //   ollama / lmstudio — local (Kuestenlogik.Bowire.Ai)
    //   openai / openrouter — BYOK cloud (Kuestenlogik.Bowire.Ai.OpenAi)
    //   anthropic — BYOK cloud (Kuestenlogik.Bowire.Ai.Anthropic)
    //   mcp — MCP-client reversal (Kuestenlogik.Bowire.Ai.Mcp)
    function isAiCloudProvider(p) {
        return p === 'openai' || p === 'anthropic' || p === 'openrouter';
    }
    function isAiLocalProvider(p) {
        return p === 'ollama' || p === 'lmstudio';
    }
    function defaultEndpointFor(providerId) {
        switch (providerId) {
            case 'openai': return 'https://api.openai.com/v1';
            case 'openrouter': return 'https://openrouter.ai/api/v1';
            case 'anthropic': return ''; // SDK uses its built-in endpoint
            case 'mcp': return 'http://localhost:3845/mcp';
            case 'lmstudio': return 'http://localhost:1234';
            default: return 'http://localhost:11434';
        }
    }
    function endpointPlaceholderFor(providerId) {
        switch (providerId) {
            case 'openai': return 'https://api.openai.com/v1';
            case 'openrouter': return 'https://openrouter.ai/api/v1';
            case 'anthropic': return '(SDK default — leave blank)';
            case 'mcp': return 'http://localhost:3845/mcp or stdio:claude mcp serve';
            case 'lmstudio': return 'http://localhost:1234';
            default: return 'http://localhost:11434';
        }
    }
    function endpointHelpFor(providerId) {
        switch (providerId) {
            case 'openai': return 'OpenAI API base URL. Default api.openai.com/v1 covers chat-completions; an Azure / proxy deployment slots in by overriding the URL here.';
            case 'openrouter': return 'OpenRouter base URL — same OpenAI-compatible wire shape against a single key that fronts dozens of models.';
            case 'anthropic': return 'Leave blank to use the SDK\'s built-in endpoint. The Anthropic.SDK package handles the wire details.';
            case 'mcp': return 'Either an absolute http(s) URL of the MCP host (Streamable HTTP or SSE) or a "stdio:<command>" string that Bowire will spawn as a child process. Bowire picks the first tool whose name reads as a chat / completion / sampling gateway.';
            case 'lmstudio': return 'LM Studio listens on 127.0.0.1:1234 by default. Same Ollama-compatible wire shape.';
            default: return 'Ollama listens on 127.0.0.1:11434 by default. Use a remote host for shared GPU servers.';
        }
    }
    function modelPlaceholderFor(providerId) {
        switch (providerId) {
            case 'openai': return 'gpt-4o-mini';
            case 'openrouter': return 'anthropic/claude-3.5-sonnet';
            case 'anthropic': return 'claude-opus-4-7';
            case 'mcp': return '(host-defined)';
            default: return 'llama3.2:3b';
        }
    }
    function renderAiPrivacyBanner(providerId) {
        // The privacy stance is loud per the ADR — every provider
        // option spells out exactly where prompts go.
        var text;
        var kind = 'tip';
        if (isAiLocalProvider(providerId)) {
            text = 'Prompts stay on this machine. Nothing leaves loopback. Bowire never sees the prompt or response.';
            kind = 'ok';
        } else if (providerId === 'openai') {
            text = 'Prompts go to api.openai.com. Your API key stays on this machine; Bowire calls the provider directly. Küstenlogik never sees the key, prompts, or responses.';
        } else if (providerId === 'anthropic') {
            text = 'Prompts go to api.anthropic.com. Your API key stays on this machine; Bowire calls the provider directly. Küstenlogik never sees the key, prompts, or responses.';
        } else if (providerId === 'openrouter') {
            text = 'Prompts go to openrouter.ai. Your API key stays on this machine; Bowire calls the provider directly. Küstenlogik never sees the key, prompts, or responses.';
        } else if (providerId === 'mcp') {
            text = 'Prompts go to the MCP host you configured. Bowire connects as a client; the host is responsible for the model affinity, auth, and rate limiting. Küstenlogik never sees prompts or responses.';
            kind = 'ok';
        } else {
            return el('div');
        }
        return el('div', {
            className: 'bowire-settings-ai-privacy bowire-settings-ai-privacy-' + kind,
            textContent: text
        });
    }
    function renderAiApiKeyRow(draft, status, hostManaged) {
        var hasExistingKey = !!(status && status.hasApiKey);
        var notApplicable = !isAiCloudProvider(draft.providerId);
        return renderSettingsRow('API key',
            notApplicable
                ? 'Not applicable for this provider. Local providers need no key; the MCP path inherits auth from the configured host.'
                : 'BYOK — your provider key. Stays in ai-config.json on this machine; never proxied through Küstenlogik. Leave blank to keep the existing key (shown as "set" below).',
            function () {
                var wrap = el('div', { className: 'bowire-settings-apikey-wrap' });
                var input = el('input', {
                    type: 'password',
                    className: 'bowire-settings-input bowire-settings-apikey-input',
                    value: '',
                    placeholder: notApplicable
                        ? '(not used by this provider)'
                        : (hasExistingKey ? '••••••••••• (leave blank to keep)' : 'sk-...'),
                    autocomplete: 'off',
                    spellcheck: false
                });
                input.oninput = function () { draft.apiKey = input.value; };
                if (hostManaged || notApplicable) input.setAttribute('disabled', 'disabled');
                wrap.appendChild(input);
                if (hasExistingKey && !notApplicable) {
                    var clearBtn = el('button', {
                        type: 'button',
                        className: 'bowire-settings-apikey-clear',
                        textContent: 'Clear stored key',
                        title: 'Remove the API key from ai-config.json. Disables the cloud provider until you paste one again.'
                    });
                    clearBtn.onclick = function () {
                        // The sentinel lets the server distinguish
                        // "leave existing" (empty body field) from
                        // "explicitly clear" (this sentinel).
                        draft.apiKey = '__bowire_clear__';
                        renderSettingsDialog();
                    };
                    wrap.appendChild(clearBtn);
                }
                return wrap;
            });
    }

    var aiSettingsState = {
        loaded: false,
        loading: false,
        status: null,         // last /api/ai/status body, or null when 404
        probe: null,          // last /api/ai/probe-local body
        draft: null,          // { providerId, endpoint, model, apiKey, autoDetectLocal }
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
                    apiKey: aiSettingsState.status.apiKey || '',
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
                        hasApiKey: !!resp.body.hasApiKey,
                        autoDetectLocal: !!resp.body.autoDetectLocal
                    };
                    aiSettingsState.draft = {
                        providerId: resp.body.providerId,
                        endpoint: resp.body.endpoint,
                        model: resp.body.model,
                        apiKey: '', // reset the draft field after save so a leave-blank does the right thing next save
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

        // Provider dropdown (Phase 2 local + Phase 3 BYOK cloud +
        // Phase 4 MCP-client reversal). Each option's tooltip names
        // the package that contributes the factory — standalone bowire
        // bundles all of them, embedded hosts opt in granularly.
        section.appendChild(renderSettingsRow('Provider',
            'Backend that serves chat completions. Local (Ollama / LM Studio) keeps prompts on this machine; BYOK cloud (OpenAI / Anthropic / OpenRouter) calls the provider directly from this host; MCP routes through your existing MCP host.',
            function () {
                var sel = el('select', { className: 'bowire-settings-select' });
                var options = [
                    { v: 'ollama', l: 'Ollama (local)' },
                    { v: 'lmstudio', l: 'LM Studio (local, Ollama-compatible)' },
                    { v: 'openai', l: 'OpenAI (BYOK cloud)' },
                    { v: 'anthropic', l: 'Anthropic / Claude (BYOK cloud)' },
                    { v: 'openrouter', l: 'OpenRouter (BYOK cloud, multi-model)' },
                    { v: 'mcp', l: 'MCP host (reverse — your gateway)' }
                ];
                options.forEach(function (o) {
                    sel.appendChild(el('option', { value: o.v, textContent: o.l, selected: draft.providerId === o.v }));
                });
                sel.onchange = function () {
                    draft.providerId = sel.value;
                    // Swap the endpoint default when the user picks a
                    // different provider class so they don't have to
                    // manually retype it on first switch. We only swap
                    // when the current endpoint still looks like the
                    // ollama default — never overwrite something the
                    // user has actually typed.
                    if ((draft.endpoint || '') === 'http://localhost:11434' || !draft.endpoint) {
                        draft.endpoint = defaultEndpointFor(sel.value);
                    }
                    renderSettingsDialog();
                };
                if (hostManaged) sel.setAttribute('disabled', 'disabled');
                return sel;
            }));

        // Privacy banner — provider-specific so the user always sees
        // exactly where prompts go before they paste a key. Same
        // privacy stance the ADR pins in docs/architecture/ai-integration.md.
        section.appendChild(renderAiPrivacyBanner(draft.providerId));

        // Endpoint text input. Placeholder + helper text are
        // provider-specific so MCP users see the stdio: form and
        // cloud users see the canonical base URL.
        section.appendChild(renderSettingsRow('Endpoint',
            endpointHelpFor(draft.providerId),
            function () {
                var input = el('input', {
                    type: 'text',
                    className: 'bowire-settings-input',
                    value: draft.endpoint || '',
                    placeholder: endpointPlaceholderFor(draft.providerId)
                });
                input.oninput = function () { draft.endpoint = input.value; };
                if (hostManaged) input.setAttribute('disabled', 'disabled');
                return input;
            }));

        // API key — only relevant for the BYOK cloud providers. We
        // render the row anyway with a "(not applicable)" hint for
        // local / MCP providers so the surface doesn't reflow every
        // time the user toggles the dropdown.
        section.appendChild(renderAiApiKeyRow(draft, aiSettingsState.status, hostManaged));

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
                        placeholder: modelPlaceholderFor(draft.providerId)
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

    // ---- #309 Discovery → Catalogue providers ----
    //
    // Configure the URL catalogue provider that #136 shipped as a
    // server-side seam. Today every flip of the provider required an
    // appsettings edit + restart; this section drives the runtime
    // surface POST /api/catalogue/config exposes — pick a provider,
    // fill its config, save. Test connection hits GET /api/catalogue/info
    // (always 200, so it tells the operator "the wiring is up" rather
    // than "the upstream is alive"). Refresh now hits
    // POST /api/catalogue/refresh and surfaces the live entry count.
    //
    // Persistence layers:
    //   - Server keeps the canonical config in ~/.bowire/catalogue-config.json
    //     (BowireCatalogueOverrideStore). Survives restart, applies to
    //     every workspace because catalogue is process-wide by design
    //     (#136 ADR: one provider per process).
    //   - The Settings UI ALSO mirrors the form fields per-workspace in
    //     localStorage so an operator who switches workspaces and re-
    //     opens this tab sees the form pre-filled with that workspace's
    //     preferred config. Saving from the UI then pushes that config
    //     to the server. Falls back to appsettings (no override file)
    //     when the operator hasn't touched the UI.

    var discoveryState = {
        loaded: false,
        loading: false,
        info: null,             // last /api/catalogue/info body
        override: null,         // last /api/catalogue/config body
        draft: null,            // editable form values
        testing: false,
        testResult: null,       // { kind: 'ok'|'err', text }
        refreshing: false,
        refreshResult: null,    // { kind: 'ok'|'err', text, count }
        saving: false,
        saveResult: null,
        entryCount: null        // last known entries.length
    };

    function discoveryPrefix() {
        return (typeof config !== 'undefined' && config && config.prefix) ? config.prefix : '';
    }

    function _discoveryWsKey() {
        // wsKey() lives in prologue.js; guard for boot order.
        try {
            if (typeof wsKey === 'function') return wsKey('bowire_catalogue_draft');
        } catch { /* ignore */ }
        return 'bowire_catalogue_draft';
    }

    function loadDiscoverySettings(force) {
        if (discoveryState.loading) return;
        if (discoveryState.loaded && !force) return;
        discoveryState.loading = true;
        var p = discoveryPrefix();
        Promise.all([
            fetch(p + '/api/catalogue/info').then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; }),
            fetch(p + '/api/catalogue/config').then(function (r) { return r.ok ? r.json() : null; }).catch(function () { return null; })
        ]).then(function (results) {
            discoveryState.info = results[0];
            discoveryState.override = results[1];
            discoveryState.loaded = true;
            discoveryState.loading = false;
            discoveryState.draft = _seedDiscoveryDraft(discoveryState.override);
            // Try to read a workspace-scoped draft override so an
            // operator who saved a form for this workspace sees it on
            // re-open even if the server-side override has since
            // changed (e.g. another workspace overwrote it).
            try {
                var raw = localStorage.getItem(_discoveryWsKey());
                if (raw) {
                    var parsed = JSON.parse(raw);
                    if (parsed && typeof parsed === 'object') {
                        discoveryState.draft = Object.assign(_seedDiscoveryDraft(null), parsed);
                    }
                }
            } catch { /* ignore */ }
            if (settingsOpen && settingsTab === 'discovery') renderSettingsDialog();
        });
    }

    function _seedDiscoveryDraft(override) {
        var draft = {
            providerId: '',
            localPath: '',
            httpUrl: '',
            httpAuthorization: '',
            httpAuthorizationSet: false,
            consulAddress: '',
            consulToken: '',
            consulTokenSet: false,
            consulDatacenter: '',
            consulTag: '',
            consulScheme: 'http'
        };
        if (override && override.hasOverride) {
            draft.providerId = override.provider || '';
            if (override.local) draft.localPath = override.local.path || override.local.Path || '';
            if (override.http) {
                draft.httpUrl = override.http.url || override.http.Url || '';
                var auth = override.http.authorization || override.http.Authorization || '';
                draft.httpAuthorizationSet = auth === '__set__';
            }
            if (override.consul) {
                draft.consulAddress = override.consul.address || override.consul.Address || '';
                var tok = override.consul.token || override.consul.Token || '';
                draft.consulTokenSet = tok === '__set__';
                draft.consulDatacenter = override.consul.datacenter || override.consul.Datacenter || '';
                draft.consulTag = override.consul.tag || override.consul.Tag || '';
                draft.consulScheme = override.consul.scheme || override.consul.Scheme || 'http';
            }
        }
        return draft;
    }

    function _persistDiscoveryDraft() {
        try {
            // Don't store the secret cleartext in localStorage — only
            // remember which fields the operator filled in (the server
            // is the authority for the actual token / auth header).
            var sanitised = Object.assign({}, discoveryState.draft);
            sanitised.httpAuthorization = '';
            sanitised.consulToken = '';
            localStorage.setItem(_discoveryWsKey(), JSON.stringify(sanitised));
        } catch { /* ignore */ }
    }

    function renderSettingsDiscovery() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: 'Catalogue providers'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-hint',
            textContent: 'Pick where Bowire reads its URL catalogue from. Local file, a remote JSON document, or a Consul agent. Saving from this UI overrides whatever Bowire:Discovery:Catalogue in appsettings.json sets; clearing the override falls back to appsettings.'
        }));

        if (!discoveryState.loaded) {
            section.appendChild(el('p', {
                className: 'bowire-settings-help',
                textContent: 'Loading catalogue configuration…'
            }));
            loadDiscoverySettings(false);
            return section;
        }

        var info = discoveryState.info || {};
        var override = discoveryState.override || {};
        var draft = discoveryState.draft;

        // Status banner — green when a provider is wired (override
        // OR appsettings), grey when none, with a tag telling the
        // operator which layer is active.
        var statusClass = info.available
            ? 'bowire-settings-catalogue-status live'
            : 'bowire-settings-catalogue-status idle';
        var statusText = info.available
            ? 'Active: ' + (info.providerName || info.providerId || '(unknown)')
            : 'No catalogue provider active. Pick one below to wire it up.';
        var statusBar = el('div', { className: statusClass });
        statusBar.appendChild(el('span', { className: 'bowire-settings-catalogue-status-text', textContent: statusText }));
        if (info.available) {
            statusBar.appendChild(el('span', {
                className: 'bowire-settings-catalogue-status-source',
                textContent: override.hasOverride ? 'Source: UI override' : 'Source: appsettings.json'
            }));
        }
        if (typeof discoveryState.entryCount === 'number') {
            statusBar.appendChild(el('span', {
                className: 'bowire-settings-catalogue-status-count',
                textContent: discoveryState.entryCount + ' entries'
            }));
        }
        section.appendChild(statusBar);

        // Provider radio picker — None / Local / HTTP / Consul.
        // The "None" option clears the override (falls back to
        // appsettings); the named options drive the form below.
        var picker = el('div', { className: 'bowire-settings-catalogue-picker' });
        var providers = [
            { id: '', label: 'None', desc: 'No UI override — fall back to appsettings.json (or empty when neither is set).' },
            { id: 'local', label: 'Local file', desc: 'Read entries from a JSON file on disk. Defaults to ~/.bowire/catalogue.json when no path is set.' },
            { id: 'http', label: 'HTTP endpoint', desc: 'Fetch a catalogue document over HTTP(S). Optional Authorization header for token-gated endpoints.' },
            { id: 'consul', label: 'Consul', desc: 'Query a Consul agent for service entries. Optional ACL token, DC, and tag filter.' }
        ];
        providers.forEach(function (p) {
            var row = el('label', {
                className: 'bowire-settings-catalogue-provider' + (draft.providerId === p.id ? ' is-selected' : '')
            });
            var radio = el('input', {
                type: 'radio',
                name: 'bowire-catalogue-provider',
                value: p.id,
                checked: draft.providerId === p.id,
                onChange: function () {
                    draft.providerId = p.id;
                    _persistDiscoveryDraft();
                    renderSettingsDialog();
                }
            });
            row.appendChild(radio);
            row.appendChild(el('div', { className: 'bowire-settings-catalogue-provider-text' },
                el('div', { className: 'bowire-settings-catalogue-provider-label', textContent: p.label }),
                el('div', { className: 'bowire-settings-catalogue-provider-desc', textContent: p.desc })
            ));
            picker.appendChild(row);
        });
        section.appendChild(picker);

        // Provider-specific config fields.
        if (draft.providerId === 'local') {
            section.appendChild(renderSettingsRow('Catalogue path',
                'Absolute or relative path to the JSON document. Leave blank to fall back to ~/.bowire/catalogue.json.',
                function () {
                    var input = el('input', {
                        type: 'text',
                        className: 'bowire-settings-input',
                        value: draft.localPath || '',
                        placeholder: '~/.bowire/catalogue.json'
                    });
                    input.oninput = function () { draft.localPath = input.value; _persistDiscoveryDraft(); };
                    return input;
                }));
        } else if (draft.providerId === 'http') {
            section.appendChild(renderSettingsRow('Catalogue URL',
                'HTTPS endpoint returning the catalogue document shape ({ "version": 1, "entries": [...] }). Required.',
                function () {
                    var input = el('input', {
                        type: 'url',
                        className: 'bowire-settings-input',
                        value: draft.httpUrl || '',
                        placeholder: 'https://catalogue.example.com/bowire.json'
                    });
                    input.oninput = function () { draft.httpUrl = input.value; _persistDiscoveryDraft(); };
                    return input;
                }));
            section.appendChild(renderSettingsRow('Authorization header',
                'Sent verbatim as the Authorization request header (e.g. "Bearer eyJ..."). Leave blank to keep the existing stored value; clear to remove the stored value.',
                function () {
                    var wrap = el('div', { className: 'bowire-settings-apikey-wrap' });
                    var input = el('input', {
                        type: 'password',
                        className: 'bowire-settings-input bowire-settings-apikey-input',
                        value: '',
                        placeholder: draft.httpAuthorizationSet ? '••••••••••• (leave blank to keep)' : 'Bearer …',
                        autocomplete: 'off',
                        spellcheck: false
                    });
                    input.oninput = function () { draft.httpAuthorization = input.value; };
                    wrap.appendChild(input);
                    if (draft.httpAuthorizationSet) {
                        wrap.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-settings-apikey-clear',
                            textContent: 'Clear stored header',
                            onClick: function () {
                                draft.httpAuthorization = '__clear__';
                                draft.httpAuthorizationSet = false;
                                renderSettingsDialog();
                            }
                        }));
                    }
                    return wrap;
                }));
        } else if (draft.providerId === 'consul') {
            section.appendChild(renderSettingsRow('Consul address',
                'Consul agent base URL — e.g. http://localhost:8500. Required.',
                function () {
                    var input = el('input', {
                        type: 'url',
                        className: 'bowire-settings-input',
                        value: draft.consulAddress || '',
                        placeholder: 'http://localhost:8500'
                    });
                    input.oninput = function () { draft.consulAddress = input.value; _persistDiscoveryDraft(); };
                    return input;
                }));
            section.appendChild(renderSettingsRow('ACL token',
                'Optional X-Consul-Token sent on every catalogue request. Leave blank to keep the existing stored value.',
                function () {
                    var wrap = el('div', { className: 'bowire-settings-apikey-wrap' });
                    var input = el('input', {
                        type: 'password',
                        className: 'bowire-settings-input bowire-settings-apikey-input',
                        value: '',
                        placeholder: draft.consulTokenSet ? '••••••••••• (leave blank to keep)' : 'Consul ACL token',
                        autocomplete: 'off',
                        spellcheck: false
                    });
                    input.oninput = function () { draft.consulToken = input.value; };
                    wrap.appendChild(input);
                    if (draft.consulTokenSet) {
                        wrap.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-settings-apikey-clear',
                            textContent: 'Clear stored token',
                            onClick: function () {
                                draft.consulToken = '__clear__';
                                draft.consulTokenSet = false;
                                renderSettingsDialog();
                            }
                        }));
                    }
                    return wrap;
                }));
            section.appendChild(renderSettingsRow('Datacenter (optional)',
                'When set, every catalogue API call is made with ?dc=<value>. Leave blank to let Consul pick the local DC.',
                function () {
                    var input = el('input', {
                        type: 'text',
                        className: 'bowire-settings-input',
                        value: draft.consulDatacenter || '',
                        placeholder: 'dc1'
                    });
                    input.oninput = function () { draft.consulDatacenter = input.value; _persistDiscoveryDraft(); };
                    return input;
                }));
            section.appendChild(renderSettingsRow('Tag filter (optional)',
                'When set, only services carrying this tag are surfaced. Useful for scoping to env:staging or team:payments.',
                function () {
                    var input = el('input', {
                        type: 'text',
                        className: 'bowire-settings-input',
                        value: draft.consulTag || '',
                        placeholder: 'bowire'
                    });
                    input.oninput = function () { draft.consulTag = input.value; _persistDiscoveryDraft(); };
                    return input;
                }));
            section.appendChild(renderSettingsRow('URL scheme',
                'Consul does not carry the scheme intrinsically; pick http or https for the materialised service URLs.',
                function () {
                    var select = el('select', { className: 'bowire-settings-select' });
                    ['http', 'https'].forEach(function (s) {
                        var opt = el('option', { value: s, textContent: s });
                        if (s === draft.consulScheme) opt.selected = true;
                        select.appendChild(opt);
                    });
                    select.onchange = function () { draft.consulScheme = select.value; _persistDiscoveryDraft(); };
                    return select;
                }));
        }

        // Action row — Save / Test connection / Refresh now / Clear.
        var actionRow = el('div', { className: 'bowire-settings-catalogue-actions' });

        var saveBtn = el('button', {
            className: 'bowire-settings-action-btn',
            textContent: discoveryState.saving ? 'Saving…' : (draft.providerId ? 'Save and apply' : 'Clear UI override'),
            disabled: discoveryState.saving ? true : undefined,
            onClick: function () { saveDiscoverySettings(); }
        });
        actionRow.appendChild(saveBtn);

        var testBtn = el('button', {
            className: 'bowire-settings-action-btn bowire-settings-catalogue-test',
            textContent: discoveryState.testing ? 'Testing…' : 'Test connection',
            disabled: discoveryState.testing ? true : undefined,
            title: 'Hits GET /api/catalogue/info — checks the catalogue wiring is up. Pass = a provider is active; does not contact the upstream until you press Refresh.',
            onClick: function () { testDiscoveryConnection(); }
        });
        actionRow.appendChild(testBtn);

        var refreshBtn = el('button', {
            className: 'bowire-settings-action-btn bowire-settings-catalogue-refresh',
            textContent: discoveryState.refreshing ? 'Refreshing…' : 'Refresh now',
            disabled: discoveryState.refreshing ? true : undefined,
            title: 'Hits POST /api/catalogue/refresh — fetches the catalogue from the active provider and reports the entry count.',
            onClick: function () { refreshDiscoveryEntries(); }
        });
        actionRow.appendChild(refreshBtn);

        section.appendChild(actionRow);

        // Result strip — shows the last action outcome inline so the
        // operator gets feedback without a toast going off-screen.
        if (discoveryState.saveResult) {
            section.appendChild(el('div', {
                className: 'bowire-settings-catalogue-result ' + (discoveryState.saveResult.kind === 'ok' ? 'ok' : 'err'),
                textContent: discoveryState.saveResult.text
            }));
        }
        if (discoveryState.testResult) {
            section.appendChild(el('div', {
                className: 'bowire-settings-catalogue-result ' + (discoveryState.testResult.kind === 'ok' ? 'ok' : 'err'),
                textContent: discoveryState.testResult.text
            }));
        }
        if (discoveryState.refreshResult) {
            section.appendChild(el('div', {
                className: 'bowire-settings-catalogue-result ' + (discoveryState.refreshResult.kind === 'ok' ? 'ok' : 'err'),
                textContent: discoveryState.refreshResult.text
            }));
        }

        // Fallback hint — call out the layering so an operator who
        // doesn't see the UI override applied understands why.
        if (!override.hasOverride && info.available) {
            section.appendChild(el('p', {
                className: 'bowire-settings-help',
                style: 'margin-top:10px;',
                textContent: 'The active provider comes from appsettings.json (Bowire:Discovery:Catalogue:Provider = ' + (info.defaultProviderId || info.providerId) + '). Saving from this UI replaces it.'
            }));
        }

        return section;
    }

    function _buildOverridePayload() {
        var d = discoveryState.draft;
        var payload = { provider: d.providerId || '' };
        if (d.providerId === 'local') {
            payload.local = { path: d.localPath || null };
        } else if (d.providerId === 'http') {
            payload.http = {
                url: d.httpUrl || null,
                // Empty string = "keep existing"; explicit "__clear__"
                // wipes the stored secret server-side; anything else
                // overwrites.
                authorization: d.httpAuthorization === '' ? '__keep__' : d.httpAuthorization
            };
        } else if (d.providerId === 'consul') {
            payload.consul = {
                address: d.consulAddress || null,
                token: d.consulToken === '' ? '__keep__' : d.consulToken,
                datacenter: d.consulDatacenter || null,
                tag: d.consulTag || null,
                scheme: d.consulScheme || 'http'
            };
        }
        return payload;
    }

    function saveDiscoverySettings() {
        if (discoveryState.saving) return;
        discoveryState.saving = true;
        discoveryState.saveResult = null;
        discoveryState.testResult = null;
        renderSettingsDialog();
        var p = discoveryPrefix();
        var d = discoveryState.draft;
        var req;
        if (!d.providerId) {
            // Clearing the override = DELETE /api/catalogue/config.
            req = fetch(p + '/api/catalogue/config', { method: 'DELETE' });
        } else {
            req = fetch(p + '/api/catalogue/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(_buildOverridePayload())
            });
        }
        req.then(function (r) {
            return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; })
                .catch(function () { return { ok: r.ok, status: r.status, body: null }; });
        })
            .then(function (resp) {
                discoveryState.saving = false;
                if (resp.ok) {
                    discoveryState.saveResult = {
                        kind: 'ok',
                        text: d.providerId
                            ? 'Saved. Active provider: ' + (resp.body && (resp.body.providerName || resp.body.providerId) || d.providerId) + '.'
                            : 'UI override cleared. Falling back to appsettings.'
                    };
                    // Reload status + override so the UI mirrors the
                    // server's view of the world.
                    discoveryState.loaded = false;
                    loadDiscoverySettings(true);
                    _persistDiscoveryDraft();
                } else {
                    discoveryState.saveResult = {
                        kind: 'err',
                        text: (resp.body && resp.body.title) || ('Save failed (HTTP ' + resp.status + ').')
                    };
                }
                renderSettingsDialog();
            })
            .catch(function (err) {
                discoveryState.saving = false;
                discoveryState.saveResult = { kind: 'err', text: 'Network error: ' + (err && err.message ? err.message : err) };
                renderSettingsDialog();
            });
    }

    function testDiscoveryConnection() {
        if (discoveryState.testing) return;
        discoveryState.testing = true;
        discoveryState.testResult = null;
        renderSettingsDialog();
        var p = discoveryPrefix();
        fetch(p + '/api/catalogue/info')
            .then(function (r) {
                return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; });
            })
            .then(function (resp) {
                discoveryState.testing = false;
                if (resp.ok && resp.body && resp.body.available) {
                    discoveryState.testResult = {
                        kind: 'ok',
                        text: 'OK — ' + (resp.body.providerName || resp.body.providerId) + ' wired and reachable from this host.'
                    };
                } else if (resp.ok) {
                    discoveryState.testResult = {
                        kind: 'err',
                        text: 'No provider active. Pick one + Save and try again.'
                    };
                } else {
                    discoveryState.testResult = {
                        kind: 'err',
                        text: 'Test failed (HTTP ' + resp.status + ').'
                    };
                }
                renderSettingsDialog();
            })
            .catch(function (err) {
                discoveryState.testing = false;
                discoveryState.testResult = { kind: 'err', text: 'Network error: ' + (err && err.message ? err.message : err) };
                renderSettingsDialog();
            });
    }

    function refreshDiscoveryEntries() {
        if (discoveryState.refreshing) return;
        discoveryState.refreshing = true;
        discoveryState.refreshResult = null;
        renderSettingsDialog();
        var p = discoveryPrefix();
        fetch(p + '/api/catalogue/refresh', { method: 'POST' })
            .then(function (r) {
                return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; });
            })
            .then(function (resp) {
                discoveryState.refreshing = false;
                if (resp.ok && resp.body) {
                    var entries = Array.isArray(resp.body.entries) ? resp.body.entries : [];
                    discoveryState.entryCount = entries.length;
                    discoveryState.refreshResult = {
                        kind: 'ok',
                        text: 'Refreshed — ' + entries.length + ' entr' + (entries.length === 1 ? 'y' : 'ies') + ' from ' + (resp.body.providerName || resp.body.providerId || 'provider') + '.'
                    };
                    // Push the freshly-fetched entries through the
                    // catalogue merge so the sidebar lights up without
                    // a reload.
                    if (typeof applyCatalogueToServerUrls === 'function') {
                        applyCatalogueToServerUrls(resp.body);
                        if (typeof render === 'function') render();
                    }
                } else {
                    discoveryState.refreshResult = {
                        kind: 'err',
                        text: (resp.body && resp.body.title) || ('Refresh failed (HTTP ' + resp.status + ').'),
                        count: null
                    };
                }
                renderSettingsDialog();
            })
            .catch(function (err) {
                discoveryState.refreshing = false;
                discoveryState.refreshResult = { kind: 'err', text: 'Network error: ' + (err && err.message ? err.message : err) };
                renderSettingsDialog();
            });
    }

    // ---- #153 UI phase — Tools (running reverse-proxies) ----
    //
    // Lists every reverse-proxy host the operator started via the
    // topbar Tools menu. Backed by GET /api/tools/reverse-proxy. Each
    // row carries a Stop button; a global "Stop all" handles the
    // multi-row case. The list also calls out that hosts die with
    // bowire.exe so an operator who expects daemons reads the right
    // story before they file an issue.
    function renderSettingsTools() {
        var section = el('div', { className: 'bowire-settings-section' });
        section.appendChild(el('h3', { className: 'bowire-settings-section-title',
            textContent: 'Tools' }));
        section.appendChild(el('div', { className: 'bowire-settings-section-hint',
            textContent: 'In-process tools you started from the workbench. Stops when Bowire shuts down — use `bowire proxy` for a long-running daemon.' }));

        // Reverse-proxy sub-section
        section.appendChild(el('h4', {
            className: 'bowire-settings-section-subtitle',
            style: 'margin-top:18px;font-size:13px;color:var(--bowire-text-secondary);text-transform:uppercase;letter-spacing:0.05em',
            textContent: 'Reverse proxy'
        }));

        if (!(typeof window !== 'undefined' && typeof window.bowireRefreshReverseProxies === 'function')) {
            section.appendChild(el('div', {
                className: 'bowire-settings-section-empty',
                textContent: 'Reverse-proxy launcher not loaded — check that the Bowire bundle is up to date.'
            }));
            return section;
        }

        // First open of this tab kicks the fetch. After that the
        // module-scope state cache is consulted directly so toggling
        // tabs doesn't refire HTTP for every render.
        if (!window.bowireReverseProxyState.loaded && !window.bowireReverseProxyState.loading) {
            window.bowireRefreshReverseProxies({ rerender: true });
        }
        var running = (window.bowireReverseProxyState.running) || [];

        var actionRow = el('div', { style: 'display:flex;gap:8px;margin-bottom:12px;align-items:center;flex-wrap:wrap' });
        actionRow.appendChild(el('button', {
            className: 'bowire-settings-action-btn',
            textContent: 'Start a reverse proxy…',
            onClick: function () { window.bowireOpenReverseProxyModal(); }
        }));
        if (running.length > 0) {
            actionRow.appendChild(el('button', {
                className: 'bowire-settings-action-btn',
                textContent: 'Stop all',
                onClick: function () { window.bowireStopAllReverseProxies(); }
            }));
        }
        actionRow.appendChild(el('button', {
            className: 'bowire-settings-action-btn',
            style: 'background:none;color:var(--bowire-text-tertiary)',
            textContent: 'Refresh',
            onClick: function () { window.bowireRefreshReverseProxies({ rerender: true }); }
        }));
        section.appendChild(actionRow);

        if (running.length === 0) {
            section.appendChild(el('div', {
                className: 'bowire-settings-section-empty',
                textContent: 'No reverse proxies running. Start one from the topbar Tools menu or the button above.'
            }));
            return section;
        }

        var list = el('div', { className: 'bowire-settings-tools-list',
            style: 'display:flex;flex-direction:column;gap:6px' });
        running.forEach(function (entry) {
            var row = el('div', {
                className: 'bowire-settings-tools-row',
                style: 'display:flex;align-items:center;gap:10px;padding:8px 10px;background:var(--bowire-bg-elevated);border:1px solid var(--bowire-border-subtle);border-radius:6px'
            });
            row.appendChild(el('code', {
                style: 'font-size:12px;color:var(--bowire-text);font-weight:500',
                textContent: ':' + entry.port
            }));
            row.appendChild(el('span', {
                style: 'color:var(--bowire-text-tertiary);font-size:12px',
                textContent: '→'
            }));
            row.appendChild(el('code', {
                style: 'font-size:12px;color:var(--bowire-text-secondary);flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap',
                textContent: entry.upstream
            }));
            row.appendChild(el('button', {
                className: 'bowire-settings-action-btn',
                textContent: 'Stop',
                onClick: function () { window.bowireStopReverseProxy(entry.port); }
            }));
            list.appendChild(row);
        });
        section.appendChild(list);

        section.appendChild(el('p', {
            className: 'bowire-settings-help',
            style: 'margin-top:12px',
            textContent: 'Stops when Bowire shuts down.'
        }));

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
                        logAction: { kind: 'history-clear', rail: 'settings',
                            title: 'Cleared call history (' + (Array.isArray(backup) ? backup.length : 0) + ' entries)',
                            undoSpec: { entries: backup },
                            redo: function () { clearHistory(); } }
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
                        logAction: { kind: 'favorites-clear', rail: 'settings',
                            title: 'Cleared favorites (' + (Array.isArray(backup) ? backup.length : 0) + ' entries)',
                            undoSpec: { entries: backup },
                            redo: function () { try { localStorage.removeItem(wsKey(FAVORITES_KEY)); } catch {} render(); } }
                    });
                }, { title: 'Clear Favorites', danger: true, confirmText: 'Clear' });
            }
        ));

        // #194 — Settings → Data: bulk-clear recordings + collections.
        // Routes through soft-delete (the per-rail trash buckets) so
        // an undo restores every entry to its original index, and
        // also records the bulk op in the action log.
        section.appendChild(renderSettingsAction(
            'Clear recordings',
            'Move every recording in this workspace to the recordings trash',
            'Clear Recordings',
            function () {
                bowireConfirm('Move all recordings to trash?', function () {
                    if (!Array.isArray(recordingsList) || recordingsList.length === 0) {
                        toast('No recordings to clear', 'info');
                        return;
                    }
                    var snapshot = recordingsList.map(function (r, idx) {
                        return { entry: JSON.parse(JSON.stringify(r)), originalIdx: idx };
                    });
                    var removed = recordingsList.splice(0, recordingsList.length);
                    if (typeof recordingActiveId !== 'undefined') recordingActiveId = null;
                    if (typeof recordingManagerSelectedId !== 'undefined') recordingManagerSelectedId = null;
                    if (Array.isArray(recordingsTrash)) {
                        for (var i = snapshot.length - 1; i >= 0; i--) {
                            recordingsTrash.unshift({
                                entry: snapshot[i].entry,
                                originalIdx: snapshot[i].originalIdx,
                                deletedAt: Date.now()
                            });
                        }
                        if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
                    }
                    if (typeof persistRecordings === 'function') persistRecordings();
                    render();
                    toast(removed.length + ' recordings moved to trash', 'success', {
                        undo: function () {
                            snapshot.slice().sort(function (a, b) { return a.originalIdx - b.originalIdx; })
                                .forEach(function (it) {
                                    if (!recordingsList.find(function (r) { return r.id === it.entry.id; })) {
                                        var idx = Math.min(it.originalIdx, recordingsList.length);
                                        recordingsList.splice(idx, 0, it.entry);
                                    }
                                });
                            if (Array.isArray(recordingsTrash)) {
                                var ids = {};
                                snapshot.forEach(function (it) { ids[it.entry.id] = true; });
                                for (var j = recordingsTrash.length - 1; j >= 0; j--) {
                                    if (recordingsTrash[j] && recordingsTrash[j].entry
                                        && ids[recordingsTrash[j].entry.id]) {
                                        recordingsTrash.splice(j, 1);
                                    }
                                }
                                if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
                            }
                            if (typeof persistRecordings === 'function') persistRecordings();
                            render();
                        },
                        logAction: {
                            kind: 'recordings-clear', rail: 'recordings',
                            title: 'Cleared recordings (' + snapshot.length + ' entries)',
                            undoSpec: { entries: snapshot }
                        }
                    });
                }, { title: 'Clear Recordings', danger: true, confirmText: 'Clear' });
            }
        ));

        section.appendChild(renderSettingsAction(
            'Clear collections',
            'Move every collection in this workspace to the collections trash',
            'Clear Collections',
            function () {
                bowireConfirm('Move all collections to trash?', function () {
                    if (!Array.isArray(collectionsList) || collectionsList.length === 0) {
                        toast('No collections to clear', 'info');
                        return;
                    }
                    var snapshot = collectionsList.map(function (c, idx) {
                        return { entry: JSON.parse(JSON.stringify(c)), originalIdx: idx };
                    });
                    var removed = collectionsList.splice(0, collectionsList.length);
                    if (typeof collectionManagerSelectedId !== 'undefined') collectionManagerSelectedId = null;
                    if (Array.isArray(collectionsTrash)) {
                        for (var i = snapshot.length - 1; i >= 0; i--) {
                            collectionsTrash.unshift({
                                entry: snapshot[i].entry,
                                originalIdx: snapshot[i].originalIdx,
                                deletedAt: Date.now()
                            });
                        }
                        if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
                    }
                    if (typeof persistCollections === 'function') persistCollections();
                    render();
                    toast(removed.length + ' collections moved to trash', 'success', {
                        undo: function () {
                            snapshot.slice().sort(function (a, b) { return a.originalIdx - b.originalIdx; })
                                .forEach(function (it) {
                                    if (!collectionsList.find(function (c) { return c.id === it.entry.id; })) {
                                        var idx = Math.min(it.originalIdx, collectionsList.length);
                                        collectionsList.splice(idx, 0, it.entry);
                                    }
                                });
                            if (Array.isArray(collectionsTrash)) {
                                var ids = {};
                                snapshot.forEach(function (it) { ids[it.entry.id] = true; });
                                for (var j = collectionsTrash.length - 1; j >= 0; j--) {
                                    if (collectionsTrash[j] && collectionsTrash[j].entry
                                        && ids[collectionsTrash[j].entry.id]) {
                                        collectionsTrash.splice(j, 1);
                                    }
                                }
                                if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
                            }
                            if (typeof persistCollections === 'function') persistCollections();
                            render();
                        },
                        logAction: {
                            kind: 'collections-clear', rail: 'collections',
                            title: 'Cleared collections (' + snapshot.length + ' entries)',
                            undoSpec: { entries: snapshot }
                        }
                    });
                }, { title: 'Clear Collections', danger: true, confirmText: 'Clear' });
            }
        ));

        section.appendChild(renderSettingsAction(
            'Migrate ${name} → {{name}}',
            'Rewrite legacy ${name} placeholders to the canonical {{name}} syntax (#145)',
            'Migrate',
            function () {
                bowireConfirm(
                    'Rewrite every ${name} placeholder in this workspace to {{name}}?\n\nThis touches recordings, collections, freeform requests, flows, environment variables and globals. The change persists to disk immediately.',
                    function () {
                        try {
                            if (typeof _runMigrationFromUi === 'function') _runMigrationFromUi();
                            else if (typeof migrateLegacyVars === 'function') migrateLegacyVars();
                        } catch (e) {
                            console.error('[settings] vars migration failed', e);
                            toast('Migration failed — see console.', 'error');
                        }
                    },
                    { title: 'Migrate ${} → {{}}', confirmText: 'Migrate' }
                );
            }
        ));

        section.appendChild(renderSettingsAction(
            'Reset all settings',
            'Clear localStorage and reload — undo restores from a pre-reset snapshot',
            'Reset All',
            function () {
                bowireConfirm('Reset ALL Bowire data (history, favorites, environments, settings)?\n\nThe action log keeps a pre-reset snapshot so Ctrl/Cmd+Z can put it back, but only until the page reloads.', function () {
                    // #194 — snapshot every localStorage key into an
                    // array of {key, value} pairs so undo can splat
                    // it back. Snapshot necessarily includes the
                    // action-log's own slot pre-reset, so a successful
                    // undo restores the log to its pre-reset state
                    // (dropping the reset entry itself). Operator can
                    // Ctrl/Cmd+Z synchronously before the reload
                    // timer fires.
                    var snapshot = [];
                    try {
                        for (var i = 0; i < localStorage.length; i++) {
                            var k = localStorage.key(i);
                            if (k != null) snapshot.push({ key: k, value: localStorage.getItem(k) });
                        }
                    } catch { /* defensive */ }
                    var keyCount = snapshot.length;
                    try { localStorage.clear(); } catch { /* ignore */ }
                    toast('All data cleared \u2014 Ctrl/Cmd+Z to undo, reload incoming', 'success', {
                        duration: 8000,
                        undo: function () {
                            try {
                                snapshot.forEach(function (pair) {
                                    if (pair && typeof pair.key === 'string' && pair.value != null) {
                                        localStorage.setItem(pair.key, pair.value);
                                    }
                                });
                            } catch { /* ignore */ }
                            toast('Settings restored from snapshot \u2014 reload for full effect', 'info');
                        },
                        logAction: {
                            kind: 'settings-reset', rail: 'settings',
                            title: 'Reset all settings (' + keyCount + ' keys snapshotted)',
                            undoSpec: { entries: snapshot }
                        }
                    });
                    // Wider window than the legacy 500 ms so the
                    // operator can react to the Undo button.
                    setTimeout(function () { location.reload(); }, 5000);
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
                // Order: name → tagline → version. User feedback: the
                // version was visually separating the name from the
                // tagline; reading it bottom-up communicates 'what is
                // this thing' before 'which version is it'.
                el('div', { className: 'bowire-settings-about-brand-tagline', textContent: 'The multi-protocol API workbench' }),
                el('div', { className: 'bowire-settings-about-brand-version', textContent: 'Version ' + (config.version || 'unknown') })
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
    // Installed UI extensions — surfaced alongside protocols in the
    // Plugins page so the operator can see whether the Map / Chart /
    // Table widget is loaded. Same /api/plugins fetch hydrates both
    // arrays (single round-trip).
    var installedExtensions = [];
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
        } else {
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
        }

        // UI extensions — the map widget, future chart / table viewers,
        // 3rd-party MIL-symbol renderers. Operator feedback: they
        // expected to see the Map plugin in this page (it's their main
        // entry point for "is the map installed?"), but the protocols
        // list above never included it because Map ships as a
        // [BowireExtension] rather than an [BowirePlugin] protocol. We
        // append a dedicated section that lists every loaded
        // IBowireUiExtension + any not-yet-installed packages the
        // suggestion table knows about, so both halves are visible.
        section.appendChild(renderInstalledExtensionsSection());

        return section;
    }

    /**
     * Render the "Installed UI extensions" section beneath the
     * protocols list. Each row carries the extension's display name,
     * its declaring package id, the semantic kinds it claims, the
     * Viewer / Editor capability pills, and an "Installed" status
     * pill. Packages from the bowirePackageSuggestions table that
     * aren't currently loaded get a grey "Suggested" row so the
     * operator discovers extensions before they ever hit an
     * un-rendered coordinate annotation.
     */
    function renderInstalledExtensionsSection() {
        var section = el('div', { className: 'bowire-settings-section bowire-settings-plugin-extensions' });
        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: 'Installed UI extensions'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-desc',
            textContent: 'Widgets that render semantic annotations — coordinates on a map, time-series in a chart, &c. Suggested rows are extensions Bowire knows a package for but hasn\'t loaded yet.'
        }));

        var rows = [];

        // Installed half — straight from the /api/plugins extensions
        // array. Sort by id so the order is stable across refreshes.
        var sortedExt = (installedExtensions || []).slice().sort(function (a, b) {
            var ai = (a.id || '').toLowerCase();
            var bi = (b.id || '').toLowerCase();
            return ai < bi ? -1 : ai > bi ? 1 : 0;
        });
        var loadedPackageIds = {};
        for (var ei = 0; ei < sortedExt.length; ei++) {
            (function (ext) {
                var pkgId = ext.packageId || ext.PackageId || '';
                if (pkgId) loadedPackageIds[pkgId.toLowerCase()] = true;
                rows.push(renderExtensionRow({
                    id: ext.id || ext.Id || '',
                    packageId: pkgId,
                    displayName: extensionDisplayName(ext.id || ext.Id || ''),
                    kinds: ext.kinds || ext.Kinds || [],
                    capabilities: ext.capabilities || ext.Capabilities || [],
                    status: 'installed',
                }));
            })(sortedExt[ei]);
        }

        // Suggested half — every distinct package id in the
        // suggestion table that didn't appear in the loaded set.
        // The suggestion table is also surfaced cross-cutting in
        // extensions.js (mountWidgetsForMethod renders a placeholder
        // when a kind has no viewer); listing them here gives the
        // operator a "what could I install" overview without waiting
        // for an unannotated payload to trigger the placeholder.
        var suggestionEntries = collectSuggestionEntries();
        for (var si = 0; si < suggestionEntries.length; si++) {
            var sug = suggestionEntries[si];
            if (loadedPackageIds[sug.packageId.toLowerCase()]) continue;
            rows.push(renderExtensionRow({
                id: '',
                packageId: sug.packageId,
                displayName: sug.packageId,
                kinds: sug.kinds,
                capabilities: [],
                status: 'suggested',
            }));
        }

        if (rows.length === 0) {
            section.appendChild(el('p', {
                className: 'bowire-settings-empty',
                textContent: 'No UI extensions loaded.'
            }));
            return section;
        }

        var list = el('div', { className: 'bowire-settings-plugin-list' });
        for (var ri = 0; ri < rows.length; ri++) list.appendChild(rows[ri]);
        section.appendChild(list);
        return section;
    }

    /**
     * Render one extension row — matches the protocol-row chrome
     * (name + id + meta line on the left, pill on the right). Status
     * "installed" → green pill; "suggested" → grey pill.
     */
    function renderExtensionRow(meta) {
        var row = el('div', { className: 'bowire-settings-plugin-row bowire-settings-extension-row' });

        var textBox = el('div', { className: 'bowire-settings-plugin-text' });
        textBox.appendChild(el('div', {
            className: 'bowire-settings-plugin-name',
            textContent: meta.displayName || meta.id || meta.packageId
        }));

        // Secondary identity line — the extension id (when loaded) or
        // the package id (when only suggested). Mirrors the protocol
        // row's "p.id" mono line.
        var subLine = el('div', { className: 'bowire-settings-plugin-id' });
        var subParts = [];
        if (meta.id) subParts.push(meta.id);
        if (meta.packageId && meta.packageId !== meta.id) subParts.push(meta.packageId);
        subLine.textContent = subParts.join(' · ');
        textBox.appendChild(subLine);

        // Kinds + capabilities — small chip line so the operator can
        // tell at a glance whether the extension handles their data
        // shape, and whether it's a viewer / editor / both.
        var chips = el('div', { className: 'bowire-settings-extension-chips' });
        var kinds = meta.kinds || [];
        for (var ki = 0; ki < kinds.length; ki++) {
            chips.appendChild(el('span', {
                className: 'bowire-settings-extension-chip is-kind',
                textContent: kinds[ki]
            }));
        }
        var caps = meta.capabilities || [];
        for (var ci = 0; ci < caps.length; ci++) {
            chips.appendChild(el('span', {
                className: 'bowire-settings-extension-chip is-capability',
                textContent: caps[ci]
            }));
        }
        if (chips.children.length > 0) textBox.appendChild(chips);

        row.appendChild(textBox);

        // Status pill — Installed / Suggested. Same right-aligned
        // slot the protocol row uses for its checkbox.
        var pill = el('span', {
            className: 'bowire-settings-extension-status is-' + meta.status,
            textContent: meta.status === 'installed' ? 'Installed' : 'Suggested'
        });
        row.appendChild(pill);
        return row;
    }

    /**
     * Walk the bowirePackageSuggestions table (declared in
     * extensions.js) and roll up entries by package id so we can
     * render a single row per package even when several kinds map to
     * the same package (coordinate.wgs84 / .latitude / .longitude all
     * point at Kuestenlogik.Bowire.Map).
     *
     * Returns [] when the framework isn't loaded yet — the section
     * renders just the installed half, which is still useful.
     */
    function collectSuggestionEntries() {
        var out = [];
        // The framework exposes a getter rather than the raw table,
        // so we probe with the known kinds Bowire ships with. The
        // list is small (3 today); if the framework grows new kinds
        // they can be added here without a server-side round-trip.
        var probes = [
            'coordinate.wgs84',
            'coordinate.latitude',
            'coordinate.longitude',
        ];
        var seen = {};
        var fw = window.__bowireExtFramework;
        if (!fw || typeof fw.packageSuggestionFor !== 'function') {
            return out;
        }
        for (var i = 0; i < probes.length; i++) {
            var kind = probes[i];
            var pkg = fw.packageSuggestionFor(kind);
            if (!pkg) continue;
            if (!seen[pkg]) {
                seen[pkg] = { packageId: pkg, kinds: [] };
                out.push(seen[pkg]);
            }
            seen[pkg].kinds.push(kind);
        }
        return out;
    }

    /**
     * Derive a friendly display name from an extension id. Ids follow
     * `{vendor}.{name}` (e.g. "kuestenlogik.maplibre"), so the trailing
     * segment with the first letter upper-cased reads cleanly as a
     * label. Falls back to the id unchanged when the heuristic can't
     * find a separator.
     */
    function extensionDisplayName(id) {
        if (!id) return '';
        var idx = id.lastIndexOf('.');
        if (idx < 0 || idx === id.length - 1) return id;
        var leaf = id.substring(idx + 1);
        return leaf.charAt(0).toUpperCase() + leaf.substring(1);
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
                    // `extensions` is the parallel UI-extension list
                    // surfaced alongside protocol plugins. Empty array
                    // when none are loaded — the suggestion rows fill
                    // the section so the operator still sees what's
                    // available for install.
                    installedExtensions = Array.isArray(body.extensions)
                        ? body.extensions
                        : [];
                    // Bug 1 — the tree surfaces installed UI extensions
                    // under the Plugins group, so a fetch that lands
                    // while the operator is sitting on a non-Plugins
                    // tab still needs a re-render to add those rows.
                    // The previous guard scoped re-render to the
                    // Plugins tab only, which left the tree empty
                    // until the user navigated to Plugins manually.
                    if (settingsOpen) {
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

    /**
     * Right-pane render for an installed UI extension (Map, Chart, …).
     * Surfaces what the operator wants to see when they click the
     * extension row in the Plugins tree: the package id, semantic kinds
     * it claims, capabilities (Viewer / Editor), and an Installed pill.
     * No enable/disable toggle yet — extensions ship enabled by their
     * mere presence; making them runtime-disableable is a separate
     * scope.
     */
    function renderExtensionSettings(ext) {
        var section = el('div', { className: 'bowire-settings-section' });
        var name = extensionDisplayName(ext.id || ext.Id || '');
        section.appendChild(el('h3', {
            className: 'bowire-settings-section-title',
            textContent: name + ' (UI extension)'
        }));
        section.appendChild(el('div', {
            className: 'bowire-settings-section-desc',
            textContent: 'Widget that renders semantic annotations on response payloads. Loaded into the workbench as part of the bundle that ships this extension.'
        }));

        function row(label, value) {
            return el('div', { className: 'bowire-settings-extension-meta-row' },
                el('span', { className: 'bowire-settings-extension-meta-label', textContent: label }),
                el('span', { className: 'bowire-settings-extension-meta-value', textContent: value }));
        }
        section.appendChild(row('Extension id', ext.id || ext.Id || '—'));
        var pkg = ext.packageId || ext.PackageId || '';
        if (pkg) section.appendChild(row('Package', pkg));
        var kinds = ext.kinds || ext.Kinds || [];
        if (kinds.length) section.appendChild(row('Semantic kinds', kinds.join(', ')));
        var caps = ext.capabilities || ext.Capabilities || [];
        if (caps.length) section.appendChild(row('Capabilities', caps.join(', ').toUpperCase()));
        var api = ext.bowireApi || ext.BowireApi || '';
        if (api) section.appendChild(row('Bowire API', api));

        return section;
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
