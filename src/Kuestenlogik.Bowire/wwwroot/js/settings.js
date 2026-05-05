    // ---- Settings Dialog ----
    // Postman-style modal with category sidebar + settings panel.
    // Bowire-owned categories first, then one section per protocol
    // plugin (Phase 2: IBowireProtocol.Settings).

    var settingsOpen = false;
    var settingsTab = 'general';

    function openSettings() {
        settingsOpen = true;
        settingsTab = 'general';
        renderSettingsDialog();
    }

    function closeSettings() {
        settingsOpen = false;
        renderSettingsDialog();
    }

    function renderSettingsDialog() {
        var existing = document.querySelector('.bowire-settings-overlay');
        if (existing) existing.remove();
        if (!settingsOpen) return;

        // ---- Left sidebar: categories ----
        // "About" lives in its own dedicated dialog (see openAbout / the
        // ?-button in the topbar) — kept out of Settings so the panel
        // stays focused on configuration rather than mixing config with
        // marketing/credits.
        var categories = [
            { id: 'general', label: 'General', icon: 'settings' },
            { id: 'shortcuts', label: 'Shortcuts', icon: 'list' },
            { id: 'data', label: 'Data', icon: 'trash' },
            // "Plugins" — per-plugin enable toggles. Always present, even
            // when no plugin contributes its own settings, because the
            // enable toggle itself doesn't need a plugin to opt in.
            { id: 'plugins', label: 'Plugins', icon: 'layers' }
        ];

        // Add per-plugin settings categories only for plugins that are
        // enabled AND contribute their own settings schema. Disabled
        // plugins don't show their own sidebar entry — the user re-enables
        // them from the Plugins page above first.
        for (var pi = 0; pi < protocols.length; pi++) {
            if (!isProtocolEnabled(protocols[pi].id)) continue;
            if (protocols[pi].settings && protocols[pi].settings.length > 0) {
                categories.push({
                    id: 'plugin-' + protocols[pi].id,
                    label: protocols[pi].name,
                    icon: null,
                    pluginIcon: protocols[pi].icon,
                    pluginId: protocols[pi].id
                });
            }
        }

        var leftPanel = el('div', { className: 'bowire-settings-left' });
        for (var ci = 0; ci < categories.length; ci++) {
            (function (cat) {
                leftPanel.appendChild(el('div', {
                    id: 'bowire-settings-cat-' + cat.id,
                    className: 'bowire-settings-cat' + (settingsTab === cat.id ? ' active' : ''),
                    onClick: function () { settingsTab = cat.id; renderSettingsDialog(); }
                },
                    el('span', { className: 'bowire-settings-cat-icon', innerHTML: cat.pluginIcon || svgIcon(cat.icon) }),
                    el('span', { textContent: cat.label })
                ));
            })(categories[ci]);
        }

        // ---- Right panel: settings for selected category ----
        // ID includes the active tab so morphdom fully replaces the
        // panel when switching categories instead of reusing stale DOM.
        var rightPanel = el('div', { id: 'bowire-settings-right-' + settingsTab, className: 'bowire-settings-right' });

        if (settingsTab === 'general') {
            rightPanel.appendChild(renderSettingsGeneral());
        } else if (settingsTab === 'shortcuts') {
            rightPanel.appendChild(renderSettingsShortcuts());
        } else if (settingsTab === 'data') {
            rightPanel.appendChild(renderSettingsData());
        } else if (settingsTab === 'plugins') {
            rightPanel.appendChild(renderSettingsPlugins());
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
                    toast('History cleared', 'success', { undo: function () { restoreHistory(backup); } });
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
                    localStorage.removeItem(FAVORITES_KEY);
                    render();
                    toast('Favorites cleared', 'success', { undo: function () { try { localStorage.setItem(FAVORITES_KEY, JSON.stringify(backup)); } catch {} render(); } });
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

        // ---- Branding header: Bowire logo + product + version ----
        var logoSrc = config.theme === 'dark' ? config.logoIconMono : config.logoIcon;
        section.appendChild(el('div', { className: 'bowire-settings-about-brand' },
            el('img', { src: logoSrc || '', alt: 'Bowire', className: 'bowire-settings-about-brand-logo' }),
            el('div', { className: 'bowire-settings-about-brand-text' },
                el('div', { className: 'bowire-settings-about-brand-name', textContent: 'Bowire' }),
                el('div', { className: 'bowire-settings-about-brand-version', textContent: 'Version ' + (config.version || 'unknown') }),
                el('div', { className: 'bowire-settings-about-brand-tagline', textContent: 'Multi-protocol API workbench for .NET' })
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
    function renderSettingsPlugins() {
        var section = el('div', { className: 'bowire-settings-section' });
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
