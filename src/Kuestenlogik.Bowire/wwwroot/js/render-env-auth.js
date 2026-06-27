    // ---- Render ----
    function isMobile() {
        return window.innerWidth <= 600;
    }

    // #193 Phase 2 — workspace pin-check banner. Reads the cached
    // workspacePinCheckState set by triggerWorkspacePinCheck() (see
    // prologue.js) and emits a bowire-alert-bar above the body when
    // the active workspace declares pluginPins the host doesn't have
    // loaded. Returns null when there's nothing to show so the caller
    // can append unconditionally.
    function renderWorkspacePinBanner() {
        var st = (typeof workspacePinCheckState !== 'undefined') ? workspacePinCheckState : null;
        if (!st || (st.missing.length === 0 && (!st.wrongVersion || st.wrongVersion.length === 0))) return null;

        var embedded = (typeof uiMode !== 'undefined' && uiMode === 'embedded');

        // Human-friendly summary — list the first 3 ids, then a tail
        // count. Keeps the banner one line on common viewports.
        var ids = st.missing.map(function (m) { return m.id; });
        var summary;
        if (ids.length <= 3) {
            summary = ids.join(', ');
        } else {
            summary = ids.slice(0, 3).join(', ') + ' and ' + (ids.length - 3) + ' more';
        }
        var text = embedded
            ? 'Workspace expects ' + summary + ' — read-only diff.'
            : 'Workspace needs ' + summary + ' — not loaded.';

        var bar = el('div', { className: 'bowire-alert-bar', role: 'status', 'aria-live': 'polite' });
        bar.appendChild(el('span', { className: 'bowire-alert-bar-dot', 'aria-hidden': 'true' }));
        bar.appendChild(el('span', { className: 'bowire-alert-bar-text', textContent: text }));

        // Per-plugin install status as small inline badges so the
        // operator sees progress without opening a modal.
        st.missing.forEach(function (entry) {
            var status = st.perPackage[entry.id];
            if (!status) return;
            var badge = el('span', {
                className: 'bowire-alert-bar-text',
                style: 'flex:0 0 auto; opacity:0.7; font-size:11px;',
                textContent: entry.id + ': ' + status
            });
            bar.appendChild(badge);
        });
        if (st.error) {
            bar.appendChild(el('span', {
                className: 'bowire-alert-bar-text',
                style: 'flex:0 0 auto; opacity:0.7; color:var(--bowire-status-error,#c33);',
                textContent: st.error
            }));
        }

        // Standalone-only actions. Embedded mode is read-only by
        // contract — never offer to mutate the host's plugin set
        // from a workspace file (an embedded Bowire usually runs
        // inside a production server).
        if (!embedded) {
            var installable = st.missing.some(function (m) { return m.packageId; });
            bar.appendChild(el('button', {
                className: 'bowire-alert-bar-action',
                textContent: st.installing ? 'Installing…' : 'Install',
                disabled: !installable || st.installing,
                onClick: function () { installAllWorkspacePins(); }
            }));
            bar.appendChild(el('button', {
                className: 'bowire-alert-bar-action',
                textContent: 'Edit pins',
                disabled: st.installing,
                onClick: function () {
                    // Item 3 lands the pin editor in Settings. Until
                    // then, jump to the Plugins rail so the operator
                    // sees what's loaded vs what's not.
                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                    railMode = 'discover';
                    if (typeof render === 'function') render();
                }
            }));
        }
        bar.appendChild(el('button', {
            className: 'bowire-alert-bar-close',
            'aria-label': 'Dismiss',
            textContent: '×',
            disabled: st.installing,
            onClick: function () { dismissWorkspacePinCheck(); }
        }));
        return bar;
    }

    // Drop-target side panel that pops in while the user drags a
    // method row out of the discover tree. Lists every workspace
    // collection as a drop zone plus a "+ New collection" entry at
    // the bottom; dropping on a zone appends a snapshot of the
    // dragged method onto that collection. Esc / clicking the
    // backdrop cancels the drag.
    function renderMethodDropPanel() {
        function _cancel() {
            methodDragPayload = null;
            render();
        }
        var shell = el('div', {
            className: 'bowire-method-drop-shell',
            onClick: function (e) {
                if (e.target === shell) _cancel();
            }
        });
        var panel = el('aside', {
            className: 'bowire-method-drop-panel',
            'aria-label': 'Drop on a collection'
        });
        panel.appendChild(el('div', { className: 'bowire-method-drop-header' },
            el('div', { className: 'bowire-method-drop-header-text' },
                el('span', { className: 'bowire-method-drop-title', textContent: 'Drop on a collection' }),
                el('span', { className: 'bowire-method-drop-method',
                    textContent: methodDragPayload.service + '.' + methodDragPayload.method })
            ),
            el('button', {
                type: 'button',
                className: 'bowire-method-drop-cancel',
                title: 'Cancel (Esc)',
                'aria-label': 'Cancel',
                innerHTML: svgIcon('close'),
                onClick: _cancel
            })
        ));

        var cols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
            ? collectionsList : [];

        function _itemFromPayload() {
            var svc = (typeof services !== 'undefined' && Array.isArray(services))
                ? services.find(function (s) { return s.name === methodDragPayload.service; }) : null;
            var m = svc && (svc.methods || []).find(function (x) { return x.name === methodDragPayload.method; });
            var body = '{}';
            if (m && typeof generateDefaultJson === 'function') {
                try { body = generateDefaultJson(m.inputType, 0); } catch { /* keep '{}' */ }
            }
            return {
                service: methodDragPayload.service,
                method: methodDragPayload.method,
                methodType: m ? (m.methodType || 'Unary') : 'Unary',
                protocol: svc ? svc.source : null,
                body: body,
                messages: [body],
                metadata: null,
                serverUrl: svc ? svc.originUrl : null
            };
        }

        function makeZone(opts, onDrop) {
            var z = el('div', {
                className: 'bowire-method-drop-zone' + (opts.kind === 'new' ? ' is-new' : ''),
                onDragover: function (e) {
                    e.preventDefault();
                    e.dataTransfer.dropEffect = 'copy';
                    z.classList.add('is-hover');
                },
                onDragleave: function () { z.classList.remove('is-hover'); },
                onDrop: function (e) {
                    e.preventDefault();
                    z.classList.remove('is-hover');
                    onDrop();
                }
            });
            if (opts.icon) {
                z.appendChild(el('span', {
                    className: 'bowire-method-drop-zone-icon',
                    innerHTML: svgIcon(opts.icon)
                }));
            }
            z.appendChild(el('span', { className: 'bowire-method-drop-zone-label', textContent: opts.label }));
            if (opts.meta) {
                z.appendChild(el('span', { className: 'bowire-method-drop-zone-meta', textContent: opts.meta }));
            }
            return z;
        }

        var list = el('div', { className: 'bowire-method-drop-list' });
        cols.forEach(function (col) {
            var itemCount = col.items ? col.items.length : 0;
            var z = makeZone({
                icon: 'folder',
                label: col.name,
                meta: itemCount + (itemCount === 1 ? ' item' : ' items')
            }, function () {
                if (typeof addToCollection !== 'function') return;
                addToCollection(col.id, _itemFromPayload());
                toast('Added to "' + col.name + '"', 'success');
                methodDragPayload = null;
                render();
            });
            list.appendChild(z);
        });
        if (cols.length === 0) {
            list.appendChild(el('div', {
                className: 'bowire-method-drop-empty',
                textContent: 'No collections yet — drop on the entry below to create one.'
            }));
        }
        // Trailing "+ New collection" zone — drop here creates a
        // fresh collection named after the dragged method and adds
        // the snapshot as its first item.
        list.appendChild(makeZone({
            icon: 'plus',
            label: 'New collection from this method',
            kind: 'new'
        }, function () {
            if (typeof createCollection !== 'function' || typeof addToCollection !== 'function') return;
            var col = createCollection(methodDragPayload.method + ' collection');
            addToCollection(col.id, _itemFromPayload());
            collectionsList = (typeof loadCollections === 'function') ? loadCollections() : collectionsList;
            collectionManagerSelectedId = col.id;
            toast('Created collection "' + col.name + '"', 'success');
            methodDragPayload = null;
            render();
        }));
        panel.appendChild(list);

        // ESC-to-cancel hint at the bottom — explicit affordance so
        // the modal doesn't read as "drop or stuck".
        panel.appendChild(el('div', { className: 'bowire-method-drop-footer' },
            el('span', { textContent: 'Press ' }),
            el('span', { className: 'bowire-method-drop-kbd', textContent: 'Esc' }),
            el('span', { textContent: ' to cancel' })
        ));
        shell.appendChild(panel);

        // ESC key handler. Polls every 200ms for state.methodDragPayload
        // being cleared by a drop or click — at that point the listener
        // self-removes so a fresh drag attaches its own.
        var keyHandler = function (ev) {
            if (ev.key === 'Escape') {
                document.removeEventListener('keydown', keyHandler);
                _cancel();
            }
        };
        document.addEventListener('keydown', keyHandler);
        setTimeout(function _gc() {
            if (!methodDragPayload) {
                document.removeEventListener('keydown', keyHandler);
                return;
            }
            setTimeout(_gc, 200);
        }, 200);

        return shell;
    }

    // MudBlazor-style responsive app drawer. Always rendered into
    // the layout; visibility is driven by the `is-open` class so
    // morphdom can keep the panel node stable across toggles (slide
    // transitions stay smooth, listeners survive). The B/burger
    // brand-mark button toggles appDrawerOpen.
    function renderAppDrawer() {
        var shell = el('div', {
            id: 'bowire-app-drawer',
            className: 'bowire-app-drawer' + (appDrawerOpen ? ' is-open' : ''),
            'aria-hidden': appDrawerOpen ? 'false' : 'true'
        });
        var backdrop = el('div', {
            className: 'bowire-app-drawer-backdrop',
            onClick: function () {
                appDrawerOpen = false;
                render();
            }
        });
        var panel = el('aside', {
            className: 'bowire-app-drawer-panel',
            role: 'navigation',
            'aria-label': 'Main menu',
            tabIndex: -1,
            onKeydown: function (e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    appDrawerOpen = false;
                    render();
                }
            }
        });
        function closeDrawer() {
            appDrawerOpen = false;
            render();
        }

        // Header reuses the exact brand-cluster markup from the topbar
        // so the B/burger button + wordmark sit pixel-aligned over
        // their topbar counterparts. CSS pins the header height to
        // the topbar's via --bowire-topbar-height and matches the
        // padding + border, so when the drawer opens it reads as the
        // topbar "growing downwards" rather than a separate surface.
        var header = el('div', { className: 'bowire-app-drawer-header' },
            renderBrandCluster({ withoutId: true })
        );
        panel.appendChild(header);

        var body = el('div', { className: 'bowire-app-drawer-body' });

        // --- Workspace section ---
        // Per-workspace navigation lives in the Workspaces rail; the
        // drawer's role here is identity + switch. Header shows the
        // active workspace as a chip (colour-coded), the rest of the
        // workspaces are listed underneath as direct-switch buttons.
        if (typeof workspaces !== 'undefined' && Array.isArray(workspaces)) {
            var aw = (typeof activeWorkspace === 'function') ? activeWorkspace() : null;
            var wsSection = el('section', { className: 'bowire-app-drawer-section' });
            wsSection.appendChild(el('div', {
                className: 'bowire-app-drawer-section-title',
                textContent: 'Workspace'
            }));
            if (aw) {
                wsSection.appendChild(el('div', { className: 'bowire-app-drawer-ws-active' },
                    el('span', {
                        className: 'bowire-app-drawer-ws-swatch',
                        style: 'background: ' + (aw.color || 'var(--bowire-accent)')
                    }),
                    el('span', { className: 'bowire-app-drawer-ws-name', textContent: aw.name || 'Untitled' })
                ));
            } else {
                wsSection.appendChild(el('div', {
                    className: 'bowire-app-drawer-ws-empty',
                    textContent: 'No workspace selected.'
                }));
            }
            // Other workspaces — quick-switch shortcuts. Cap at 5 to
            // keep the drawer compact; full management goes through
            // the Workspaces rail (button below).
            var others = workspaces.filter(function (w) {
                return aw ? w.id !== aw.id : true;
            }).slice(0, 5);
            if (others.length > 0) {
                others.forEach(function (w) {
                    wsSection.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-app-drawer-item bowire-app-drawer-item-ws',
                        onClick: function () {
                            if (typeof switchWorkspace === 'function') switchWorkspace(w.id);
                            closeDrawer();
                        }
                    },
                        el('span', {
                            className: 'bowire-app-drawer-ws-swatch',
                            style: 'background: ' + (w.color || 'var(--bowire-accent)')
                        }),
                        el('span', { className: 'bowire-app-drawer-item-label', textContent: w.name || 'Untitled' })
                    ));
                });
            }
            wsSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-app-drawer-item',
                onClick: function () {
                    try { railMode = 'workspaces'; } catch { /* let-shadow */ }
                    try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                    closeDrawer();
                }
            },
                el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('layers') }),
                el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Manage workspaces' })
            ));
            body.appendChild(wsSection);
        }

        // --- Quick actions section ---
        var actionsSection = el('section', { className: 'bowire-app-drawer-section' });
        actionsSection.appendChild(el('div', {
            className: 'bowire-app-drawer-section-title',
            textContent: 'Quick actions'
        }));
        actionsSection.appendChild(el('button', {
            type: 'button',
            className: 'bowire-app-drawer-item',
            onClick: function () {
                closeDrawer();
                requestAnimationFrame(function () {
                    var s = document.querySelector('.bowire-topbar-palette input, .bowire-topbar-palette-input');
                    if (s) { try { s.focus(); s.select && s.select(); } catch {} }
                });
            }
        },
            el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('search') }),
            el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Open command palette' }),
            el('span', { className: 'bowire-app-drawer-item-hint', textContent: 'Ctrl + /' })
        ));
        if (typeof createWorkspace === 'function') {
            actionsSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-app-drawer-item',
                onClick: function () {
                    closeDrawer();
                    bowirePrompt('Workspace name', {
                        title: 'New workspace',
                        confirmText: 'Create'
                    }).then(function (name) {
                        if (!name) return;
                        try {
                            var ws = createWorkspace(String(name).trim());
                            // #194 — record so Ctrl/Cmd+Z soft-deletes
                            // the workspace into trash + Redo brings it
                            // back. createWorkspace returns null when
                            // the name was already taken (it already
                            // toasted the reason), so skip logging
                            // a no-op create.
                            if (ws && typeof recordAction === 'function') {
                                recordAction({
                                    kind: 'workspace-create',
                                    rail: 'workspaces',
                                    title: 'Created workspace "' + ws.name + '"',
                                    undoSpec: { workspaceId: ws.id }
                                });
                            }
                        } catch (e) {
                            if (typeof toast === 'function') toast('Failed: ' + e.message, 'error');
                        }
                    });
                }
            },
                el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('plus') }),
                el('span', { className: 'bowire-app-drawer-item-label', textContent: 'New workspace…' })
            ));
        }
        if (typeof exportWorkspaceJson === 'function' && (typeof activeWorkspace === 'function') && activeWorkspace()) {
            actionsSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-app-drawer-item',
                onClick: function () {
                    closeDrawer();
                    try {
                        var ws = activeWorkspace();
                        var payload = exportWorkspaceJson(ws.id);
                        var blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
                        var a = document.createElement('a');
                        a.href = URL.createObjectURL(blob);
                        a.download = (ws.name || 'workspace') + '.bww';
                        a.click();
                        setTimeout(function () { URL.revokeObjectURL(a.href); }, 0);
                    } catch (e) {
                        if (typeof toast === 'function') toast('Export failed: ' + e.message, 'error');
                    }
                }
            },
                el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('download') }),
                el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Export workspace' })
            ));
        }
        if (typeof importWorkspaceJson === 'function') {
            actionsSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-app-drawer-item',
                onClick: function () {
                    closeDrawer();
                    var input = document.createElement('input');
                    input.type = 'file';
                    input.accept = '.json,.bww,application/json';
                    input.onchange = function () {
                        var f = input.files && input.files[0];
                        if (!f) return;
                        var reader = new FileReader();
                        reader.onload = function () {
                            try {
                                var data = JSON.parse(String(reader.result));
                                importWorkspaceJson(data);
                                if (typeof toast === 'function') toast('Workspace imported', 'success');
                            } catch (e) {
                                if (typeof toast === 'function') toast('Import failed: ' + e.message, 'error');
                            }
                        };
                        reader.readAsText(f);
                    };
                    input.click();
                }
            },
                el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('upload') }),
                el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Import workspace…' })
            ));
        }
        body.appendChild(actionsSection);

        // --- App-level footer: theme, settings, help, version ---
        var footerSection = el('section', { className: 'bowire-app-drawer-section bowire-app-drawer-footer' });
        // Theme cycle: auto → light → dark → auto. Shows the current
        // preference as the trailing hint so the operator sees what's
        // active without opening Settings.
        if (typeof themePreference !== 'undefined' && typeof setThemePreference === 'function') {
            var current = themePreference;
            var next = current === 'auto' ? 'light' : current === 'light' ? 'dark' : 'auto';
            var themeIcon = current === 'dark' ? 'moon' : current === 'light' ? 'sun' : 'themeAuto';
            footerSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-app-drawer-item',
                onClick: function () {
                    setThemePreference(next);
                    render();
                }
            },
                el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon(themeIcon) }),
                el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Theme' }),
                el('span', { className: 'bowire-app-drawer-item-hint', textContent: current })
            ));
        }
        if (typeof openSettings === 'function') {
            footerSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-app-drawer-item',
                onClick: function () {
                    closeDrawer();
                    openSettings();
                }
            },
                el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('settings') }),
                el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Settings' })
            ));
        }
        footerSection.appendChild(el('button', {
            type: 'button',
            className: 'bowire-app-drawer-item',
            onClick: function () {
                closeDrawer();
                if (typeof openSettings === 'function') openSettings('help');
            }
        },
            el('span', { className: 'bowire-app-drawer-item-icon', innerHTML: svgIcon('help') }),
            el('span', { className: 'bowire-app-drawer-item-label', textContent: 'Help & docs' })
        ));
        // Version footer — small, secondary, no interaction.
        var version = (config && config.version) || '';
        if (version) {
            footerSection.appendChild(el('div', {
                className: 'bowire-app-drawer-version',
                textContent: (config.title || 'Bowire') + ' · ' + version
            }));
        }
        body.appendChild(footerSection);

        panel.appendChild(body);
        shell.appendChild(backdrop);
        shell.appendChild(panel);
        // Focus the panel when it opens so Escape works without the
        // user clicking first. We do this in rAF so the node is in
        // the DOM before we touch focus.
        if (appDrawerOpen) {
            requestAnimationFrame(function () {
                var p = document.querySelector('.bowire-app-drawer-panel');
                if (p && document.activeElement !== p) {
                    try { p.focus({ preventScroll: true }); } catch { /* older browsers */ }
                }
            });
        }
        return shell;
    }

    function render() {
        const app = document.getElementById('bowire-app');
        if (!app) return;

        // Force-home rule retired. Pinning the operator to Home
        // whenever workspaces.length === 0 trapped rail clicks: any
        // attempt to leave Home re-fired the guard, snapping railMode
        // back to 'home', so every rail icon was visually clickable
        // (hover highlight + transition) but the click had no
        // effect. The user's complaint: 'die buttons werden als
        // klickbar dargestellt'. Drop the guard; each rail owns its
        // own no-workspace empty state already.

        // #248 Phase 1 — Fall back to Home if the operator just
        // disabled the currently-active rail in Settings. Without
        // this the rail icon disappears but the main pane keeps
        // rendering against the disabled mode (orphan state). Home
        // is always-on so the fallback never loops.
        if (typeof isRailEnabled === 'function'
            && typeof railMode !== 'undefined'
            && railMode !== 'home'
            && !isRailEnabled(railMode)) {
            railMode = 'home';
            try { localStorage.setItem('bowire_rail_mode', 'home'); } catch { /* ignore */ }
        }

        // #123 — lazy tab rehydrate. One-shot guard inside; cheap
        // no-op on every subsequent render once services land. Putting
        // it at the top of render() means tab persistence works even
        // when the discovery completion path doesn't end in a render()
        // call directly.
        rehydrateRequestTabs();
        if (typeof hydrateActionLog === 'function') hydrateActionLog();

        // Apply the persisted user-set sidebar width as an inline CSS var
        // override on #bowire-app. morphdom runs with `childrenOnly: true`
        // so it never touches the root's inline styles — the override sticks
        // across renders without needing to diff it. 0 means "use CSS
        // default" which lets the responsive media queries take over on
        // small viewports.
        if (sidebarWidth > 0 && !isMobile()) {
            app.style.setProperty('--bowire-sidebar-width', sidebarWidth + 'px');
        } else {
            app.style.removeProperty('--bowire-sidebar-width');
        }

        // Build the next DOM tree off-screen, then diff/merge it into the
        // live tree via morphdom. This preserves focus, selection, scroll
        // position, and existing event listeners wherever the old and new
        // trees agree structurally. The old approach of `innerHTML = ''`
        // + rebuild forced a full repaint of the sidebar on every state
        // change, burned scroll state, and dropped input focus.
        //
        // Event-listener safety: most Bowire onClick closures read global
        // `let`-state (e.g. selectedMethod, streamAutoScroll) rather than
        // capturing locals, so the "old" listener at a morphdom-preserved
        // node does the same work as the "new" listener we built. For
        // lists where position matters (service/method lists, history,
        // recordings), morphdom's default position-based matching plus
        // our stable list shape keeps the listener→node mapping aligned.
        const next = document.createElement('div');
        next.id = 'bowire-app';

        // Layout: topbar on row 1, body (sidebar + splitter + main) on row 2.
        // The topbar hosts brand / command palette / env selector / theme
        // toggle — the global-action bar for the whole app. The body
        // keeps the old horizontal layout unchanged otherwise.
        next.appendChild(renderTopbar());

        // Workspace identity cue — opt-in peripheral indicator in the
        // active workspace's chosen colour. Two localStorage keys:
        //   bowire_workspace_identity_enabled  (true/false)
        //   bowire_workspace_identity_variant  (top-strip/rail-tint)
        // The variant decides which surface paints; the rail-tint case
        // is applied in renderActivityRail using the same keys. Hidden
        // in embedded mode regardless (host owns workspace context) and
        // when there's no active workspace.
        var _identityOn = false, _identityVariant = 'top-strip';
        try {
            _identityOn = localStorage.getItem('bowire_workspace_identity_enabled') === 'true';
            _identityVariant = localStorage.getItem('bowire_workspace_identity_variant') || 'top-strip';
        } catch { /* ignore */ }
        var _identityWs = _identityOn && (typeof activeWorkspace === 'function')
            ? activeWorkspace() : null;
        if (_identityOn && _identityVariant === 'top-strip'
            && uiMode !== 'embedded' && _identityWs && _identityWs.color) {
            next.appendChild(el('div', {
                id: 'bowire-workspace-identity-band',
                className: 'bowire-workspace-identity-band',
                style: 'background:' + _identityWs.color,
                'aria-hidden': 'true',
                title: 'Workspace: ' + _identityWs.name
            }));
        }

        // #193 Phase 2 — workspace pin-check banner. Renders between
        // the topbar and the body when the active workspace's
        // pluginPins map declares protocols the host doesn't have
        // loaded. Standalone hosts get "Install missing" + "Edit
        // pins" actions; embedded hosts get a read-only diff so a
        // production host can't be coerced into installing plugins
        // from a checked-in workspace file. Session-dismissable via
        // the close affordance.
        var pinBanner = renderWorkspacePinBanner();
        if (pinBanner) next.appendChild(pinBanner);

        var body = el('div', { id: 'bowire-app-body', className: 'bowire-app-body' });

        // Mobile backdrop when sidebar is open — lives inside the body so
        // it only covers the content area, not the topbar.
        if (isMobile() && !sidebarCollapsed) {
            var backdrop = el('div', {
                className: 'bowire-sidebar-backdrop',
                onClick: function () { sidebarCollapsed = true; render(); }
            });
            body.appendChild(backdrop);
        }

        // #133 — activity rail. Sits at the very leftmost edge of the
        // workbench body, before the mode sidebar.
        body.appendChild(renderActivityRail());

        // #137 — sidebar visibility driven by the rail-mode
        // catalogue. Modes that declare sidebar.kind === 'none' skip
        // the sidebar + splitter entirely so the main pane spans
        // edge to edge. Home / Benchmarks / Parallel today; new
        // modes only need to set sidebar: { kind: 'none' } to
        // opt out.
        var modeHasSidebar = currentRailSidebarSpec().kind !== 'none';
        if (modeHasSidebar) {
            // The splitter doubles as the 'show sidebar' affordance when
            // collapsed — same DOM element, same hover behaviour, chevron
            // rotation + click intent flipped via a class. Operator
            // feedback: 'könnte man nicht einfach den splitter selbst
            // nutzen?'. No drag-to-resize when collapsed (nothing to
            // resize from a 0-width sidebar); layout.js's mousedown path
            // short-circuits when the body carries .bowire-sidebar-is-
            // collapsed and treats any down → up as a click.
            if (sidebarCollapsed) {
                body.classList.add('bowire-sidebar-is-collapsed');
            } else {
                body.appendChild(renderSidebar());
            }
            if (!isMobile()) {
                var splitterCls = 'bowire-sidebar-splitter';
                if (sidebarCollapsed) splitterCls += ' bowire-sidebar-splitter-collapsed';
                var splitter = el('div', {
                    className: splitterCls,
                    id: 'bowire-sidebar-splitter',
                    title: sidebarCollapsed
                        ? 'Show sidebar (Ctrl+B)'
                        : 'Drag to resize the sidebar — drag past the minimum to collapse, or double-click to toggle'
                });
                splitter.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-sidebar-edge-toggle bowire-sidebar-edge-toggle-'
                        + (sidebarCollapsed ? 'collapsed' : 'expanded'),
                    title: sidebarCollapsed ? 'Show sidebar (Ctrl+B)' : 'Hide sidebar (Ctrl+B) — drag to resize',
                    'aria-label': sidebarCollapsed ? 'Show sidebar' : 'Hide sidebar',
                    onClick: function (e) {
                        e.stopPropagation();
                        setSidebarCollapsed(!sidebarCollapsed);
                        render();
                    }
                }, el('span', { innerHTML: svgIcon('chevron') })));
                body.appendChild(splitter);
            }
        }
        // #160 — Workspace-context breadcrumb at the top of the main
        // pane. Returns null when there's nothing actionable (single
        // workspace, embedded mode); the main node renders as before
        // in that case.
        // Top-level workspace breadcrumb retired — Workspaces sub-views
        // render their own _renderWorkspaceBreadcrumb (workspace ▾ /
        // section / item) at the top of their main pane, and other rails
        // show the active workspace via the topbar chip. Two breadcrumbs
        // for the same workspace name read as duplicated chrome.
        var mainNode = renderMain();
        body.appendChild(mainNode);

        // #299 — Unified right-side drawer. Assistant (#90) and Help
        // (#154 Phase 3) share one drawer chrome on the right edge of
        // the body. When both are open, a tab strip lets the operator
        // switch; the active tab persists across renders. When only
        // one is open, the chrome renders without tabs. This replaces
        // the old "newest open wins, the other is silently hidden"
        // behavior — both can now coexist without competing.
        var helpUsable = helpDrawerOpen && helpAvailable
            && typeof _renderHelpDrawerContent === 'function';
        var testsUsable = (typeof testsDrawerOpen !== 'undefined') && testsDrawerOpen;
        var activityUsable = (typeof activityDrawerOpen !== 'undefined') && activityDrawerOpen;
        if (aiDrawerOpen || helpUsable || testsUsable || activityUsable) {
            body.classList.add('bowire-with-ai-drawer');
            body.appendChild(renderUnifiedRightDrawer({
                assistant: aiDrawerOpen,
                help: helpUsable,
                tests: testsUsable,
                activity: activityUsable
            }));
        }

        // #138 — Statusbar at the bottom. Hosts connection pill +
        // env selector + watch button (moved here from the topbar).
        // Sits below the body's main flex row via the wrapper
        // restructure below.

        // #133 Phase 2 — Security drawer retired. Security content
        // now lives in the main pane when the Security rail mode is
        // active (renderMain reads railMode and routes accordingly).
        // The right-side drawer concept is gone.

        // #164 v2 — bowire-app-middle wraps the body row + an optional
        // bottom-attached console panel so the layout reads:
        //   topbar
        //   middle (flex column)
        //     body  (flex row: rail + sidebar + main + right-drawer)
        //     bottom drawer  (when consoleOpen)
        //   statusbar
        // The bottom drawer spans the full middle width (sidebar
        // included). Resizable height via a top-edge splitter.
        var middle = el('div', { className: 'bowire-app-middle' });
        middle.appendChild(body);
        if (typeof consoleOpen !== 'undefined' && consoleOpen
            && typeof renderConsoleBottomDrawer === 'function') {
            middle.appendChild(renderConsoleBottomDrawer());
        }
        next.appendChild(middle);

        // #124 v2 — Omnibox modal overlay. When searchSuggestionsOpen
        // is true, the palette built in renderTopbar gets re-parented
        // into a centered modal card with a backdrop. Click on the
        // backdrop closes; Esc is handled in init.js's keydown.
        if (searchSuggestionsOpen && _omniboxPaletteForModal) {
            var backdrop = el('div', {
                id: 'bowire-omnibox-backdrop',
                className: 'bowire-omnibox-backdrop',
                onClick: function (e) {
                    if (e.target === e.currentTarget) {
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        render();
                    }
                }
            });
            var modal = el('div', { className: 'bowire-omnibox-modal' });
            modal.appendChild(_omniboxPaletteForModal);
            backdrop.appendChild(modal);
            next.appendChild(backdrop);
        }

        // #166 — Keyboard shortcut sheet overlay (Ctrl/Cmd+/). Renders
        // only when shortcutSheetOpen is true; the helper returns null
        // otherwise so the modal isn't even in the DOM unless asked
        // for.
        if (typeof renderShortcutSheet === 'function') {
            var shortcutNode = renderShortcutSheet();
            if (shortcutNode) next.appendChild(shortcutNode);
        }

        // #296 — Global Trash drawer. Modal-style overlay opened
        // from the topbar trash icon; aggregates every soft-deleted
        // bucket in the active workspace into one surface. Lives
        // alongside the other modal overlays here (not inside the
        // unified right drawer) so it can't visually collide with the
        // Activity tab while the operator is rolling back across
        // rails.
        if (typeof globalTrashOpen !== 'undefined' && globalTrashOpen
            && typeof renderGlobalTrashOverlay === 'function') {
            var trashOverlay = renderGlobalTrashOverlay();
            if (trashOverlay) next.appendChild(trashOverlay);
        }

        // #138 — Statusbar at the very bottom of the app. Hosts the
        // connection pill (moved from topbar), env selector + watch
        // button, plus future ambient indicators (hint count, save
        // state, workspace name).
        next.appendChild(renderStatusBar());

        // Drop-target side panel for method → collection drag &
        // drop. Mounted while a drag is live; lists every workspace
        // collection as a drop zone plus a "+ New collection" entry
        // at the bottom. See render-sidebar.js's onDragstart for the
        // entry point.
        if (typeof methodDragPayload !== 'undefined' && methodDragPayload) {
            next.appendChild(renderMethodDropPanel());
        }

        // MudBlazor-style responsive app drawer. Mounted last so its
        // fixed-position panel + backdrop sit on top of every other
        // surface. Hidden via display: none when closed (keeping the
        // element in the tree so morphdom doesn't need to recreate it
        // on every open/close).
        var drawer = renderAppDrawer();
        if (drawer) next.appendChild(drawer);

        if (typeof window.morphdom === 'function') {
            window.morphdom(app, next, {
                childrenOnly: true,
                onBeforeElUpdated: function (fromEl, toEl) {
                    // Fast-path: structurally identical subtree → skip.
                    // Big win for static toolbar/header regions that
                    // haven't changed across renders.
                    //
                    // CAVEAT: isEqualNode compares attributes only, not
                    // properties. Form inputs carry their state on the
                    // .value PROPERTY which isEqualNode can't see — so
                    // a subtree that looks "equal" can still hide a
                    // stale input value (e.g. after applyPreset writes
                    // formValues without changing attrs). Don't apply
                    // the optimization to subtrees that contain form
                    // fields — the value sync needs morphdom's special
                    // INPUT/TEXTAREA/SELECT handlers to run.
                    if (fromEl.isEqualNode && fromEl.isEqualNode(toEl)) {
                        var tag = fromEl.tagName;
                        if (tag !== 'INPUT' && tag !== 'TEXTAREA' && tag !== 'SELECT'
                            && !fromEl.querySelector('input, textarea, select')) {
                            return false;
                        }
                    }
                    // Preserve the live state of the currently focused
                    // input/textarea/select. morphdom has special handlers
                    // for these tags that copy `value` from toEl onto
                    // fromEl — that's the wrong direction when the user is
                    // mid-edit. Copy fromEl → toEl before morphdom syncs,
                    // and restore the selection on the next frame so the
                    // cursor doesn't jump.
                    if (fromEl === document.activeElement) {
                        var tag = fromEl.tagName;
                        if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') {
                            toEl.value = fromEl.value;
                            if (tag !== 'SELECT' && typeof fromEl.selectionStart === 'number') {
                                var s = fromEl.selectionStart;
                                var e = fromEl.selectionEnd;
                                requestAnimationFrame(function () {
                                    try { fromEl.setSelectionRange(s, e); } catch { /* ignore */ }
                                });
                            }
                        }
                    }
                    return true;
                }
            });
        } else {
            // Morphdom failed to load — fall back to the original
            // nuke-and-rebuild path so the UI is at least functional.
            app.replaceChildren();
            while (next.firstChild) app.appendChild(next.firstChild);
        }

        // Wire the sidebar-splitter drag handler. Idempotent via
        // data-dragHooked, so repeated render() calls don't stack
        // listeners on the same node.
        attachSidebarSplitterDrag();

        // #164 v3 — activity-rail overflow layout pass. Runs after
        // every render so adding/removing the bottom console drawer,
        // resizing the rail, or toggling the sidebar all re-flow the
        // mode buttons + hide the overflow into the '…' popover. The
        // ResizeObserver hook below catches geometry changes that
        // don't go through render().
        if (typeof _layoutActivityRail === 'function') {
            requestAnimationFrame(_layoutActivityRail);
        }
        if (typeof _attachActivityRailObserver === 'function') {
            _attachActivityRailObserver();
        }

        // #297 — Topbar right-cluster overflow layout pass. Same
        // pattern as the activity rail but horizontal. Re-runs after
        // every render so a workspace name change, env switch, or the
        // sidebar collapsing (which widens the body but doesn't affect
        // the topbar) all re-flow the cluster cleanly. The
        // ResizeObserver below catches viewport resizes that don't
        // bounce through render().
        if (typeof _layoutTopbarRight === 'function') {
            requestAnimationFrame(_layoutTopbarRight);
        }
        if (typeof _attachTopbarRightObserver === 'function') {
            _attachTopbarRightObserver();
        }

        // #281 — Notify the guided-tour engine so it can re-resolve
        // its current step's target + reposition the cutout + tooltip
        // after the morphdom commit. Scroll / resize events don't
        // fire on a morphdom rerender, so without this the tour's
        // spotlight drifts off the target as soon as something else
        // triggers a render.
        try {
            document.dispatchEvent(new CustomEvent('bowire-rendered'));
        } catch { /* CustomEvent missing — pre-modern browser, no-op */ }
    }

    // #297 — Topbar right-cluster overflow state. _topbarRightOverflowHidden
    // holds the descriptors {id, label, icon, priority, disabled} the
    // layout pass decided don't fit; the next render() consumes it to
    // build the popover rows. Module-scoped so render() can read it.
    var _topbarRightOverflowHidden = [];
    var _topbarRightObserver = null;
    var _topbarRightObservedEl = null;

    function _attachTopbarRightObserver() {
        if (typeof ResizeObserver === 'undefined') return;
        var rightEl = document.getElementById('bowire-topbar-right');
        if (!rightEl) return;
        if (rightEl === _topbarRightObservedEl) return;
        if (_topbarRightObserver) _topbarRightObserver.disconnect();
        // Watch the WHOLE topbar (not just the right cluster) so
        // changes to the brand cluster or palette spacer that
        // squeeze the right cluster's available width still trigger
        // a relayout. The observer's callback is cheap (re-measures
        // a handful of buttons) so we can be generous with what we
        // listen to.
        var bar = document.getElementById('bowire-topbar') || rightEl;
        _topbarRightObserver = new ResizeObserver(function () {
            if (typeof _layoutTopbarRight === 'function') _layoutTopbarRight();
        });
        _topbarRightObserver.observe(bar);
        _topbarRightObservedEl = rightEl;
    }

    // Layout pass. Walks the right-cluster's tagged buttons in priority
    // order (lowest = collapses first), measures the cluster against
    // its container, hides priority buckets until the cluster fits.
    // Populates _topbarRightOverflowHidden so the NEXT render builds
    // the popover from the same list. The pass itself does NOT call
    // render(), it just mutates display + the hidden-list — so calling
    // it from a ResizeObserver doesn't loop.
    function _layoutTopbarRight() {
        var rightEl = document.getElementById('bowire-topbar-right');
        var bar = document.getElementById('bowire-topbar');
        if (!rightEl || !bar) return;
        var overflowGroup = rightEl.querySelector('.bowire-topbar-right-overflow-group');
        var overflowBtn = document.getElementById('bowire-topbar-right-overflow-btn');
        if (!overflowGroup || !overflowBtn) return;

        // Catalogue every tagged button. data-topbar-priority of "1"
        // is the LAST to collapse (most important non-pinned), "5"
        // collapses FIRST. Always-visible buttons (Search /
        // Workspace / Env) have no priority attribute and are never
        // considered for collapse.
        var taggedNodes = rightEl.querySelectorAll('[data-topbar-priority]');
        var catalogue = [];
        for (var i = 0; i < taggedNodes.length; i++) {
            var n = taggedNodes[i];
            catalogue.push({
                node: n,
                id: n.id,
                priority: parseInt(n.dataset.topbarPriority, 10) || 99,
                label: n.dataset.topbarLabel || n.getAttribute('aria-label') || '',
                icon: n.dataset.topbarIcon || 'dots',
                // Semantic bucket carried into the popover so dividers
                // mirror the rail-strip's group separators.
                group: n.dataset.topbarGroup || null,
                disabled: !!n.disabled
            });
        }

        // Reset display + the trailing overflow-group so we measure
        // the natural (un-collapsed) width. Subsequent passes build
        // up the hidden set from a clean slate.
        for (var k = 0; k < catalogue.length; k++) catalogue[k].node.style.display = '';
        overflowGroup.style.display = 'none';
        // Force a layout sync so the measurements below reflect
        // the reset display values.
        // (Reading offsetWidth flushes pending layout.)
        // eslint-disable-next-line no-unused-vars
        var _flush = rightEl.offsetWidth;

        var available = bar.clientWidth;
        // Right cluster's natural width when nothing is hidden. If it
        // already fits, drop the overflow group + clear the hidden
        // list. We compare the right cluster's scrollWidth — when it
        // exceeds available we know flex-shrink has already squeezed
        // it (or the bar is letting it overflow).
        var natural = _measureTopbarRightFootprint(bar, rightEl);
        if (natural.fits) {
            _topbarRightOverflowHidden = [];
            overflowGroup.style.display = 'none';
            overflowBtn.classList.remove('has-overflow');
            return;
        }

        // Doesn't fit — show the overflow group and walk the
        // collapsible buttons low-priority-first (descending), hiding
        // until we fit. Within a priority bucket we collapse in the
        // visual order they appear (so e.g. About hides before Help
        // even though both are priority 5 — About is the right-most).
        overflowGroup.style.display = '';
        overflowBtn.classList.add('has-overflow');

        // Sort: highest priority number (= lowest importance) first;
        // tie-break by DOM order from right to left so the rightmost
        // sibling in a bucket collapses first.
        var domOrder = Array.prototype.indexOf;
        var sorted = catalogue.slice().sort(function (a, b) {
            if (a.priority !== b.priority) return b.priority - a.priority;
            // Right-to-left: later in the catalogue (= further right)
            // collapses first.
            return catalogue.indexOf(b) - catalogue.indexOf(a);
        });

        var hidden = [];
        for (var s = 0; s < sorted.length; s++) {
            // Re-measure after each hide. clientWidth/scrollWidth on
            // the cluster is the source of truth — the cluster is a
            // flex row, so hiding a child immediately shrinks it.
            var probe = _measureTopbarRightFootprint(bar, rightEl);
            if (probe.fits) break;
            sorted[s].node.style.display = 'none';
            hidden.push(sorted[s]);
        }

        // Final fit check + commit. Hidden order matches sort order
        // (lowest priority first); reverse to display the highest-
        // priority hidden item at the TOP of the popover so the
        // operator's eye lands on it first.
        hidden.reverse();
        _topbarRightOverflowHidden = hidden.map(function (h) {
            return { id: h.id, label: h.label, icon: h.icon, group: h.group, disabled: h.disabled };
        });

        // If the hidden list changed since the last render, refresh
        // the overflow button's aria-label + title so screen readers
        // get the up-to-date "More: …" list without waiting for the
        // next render. Cheap textContent update.
        var labels = _topbarRightOverflowHidden.map(function (h) { return h.label; });
        var ariaText = labels.length > 0 ? ('More: ' + labels.join(', ')) : 'More';
        overflowBtn.setAttribute('aria-label', ariaText);
        overflowBtn.setAttribute('title', ariaText);
    }

    // Helper: does the right cluster currently fit within its parent
    // bar? Uses the topbar's clientWidth minus the left/center
    // children's footprint as the budget. scrollWidth vs clientWidth
    // on the right cluster itself is unreliable because the cluster
    // has flex-shrink:0 — it just overflows the bar without
    // increasing its own scrollWidth.
    function _measureTopbarRightFootprint(bar, rightEl) {
        var barWidth = bar.clientWidth;
        // Sum the widths of every direct child of the bar EXCEPT
        // the right cluster; that's the budget consumed by the brand
        // cluster + the palette spacer. The remainder is what the
        // right cluster has to fit into.
        var siblingsWidth = 0;
        var children = bar.children;
        for (var i = 0; i < children.length; i++) {
            if (children[i] === rightEl) continue;
            siblingsWidth += children[i].getBoundingClientRect().width;
        }
        var available = barWidth - siblingsWidth;
        var actual = rightEl.getBoundingClientRect().width;
        // Tiny epsilon — sub-pixel rounding around the flex-gap can
        // make a cluster that visually fits report a 0.x px overflow.
        return { fits: actual <= available + 0.5, actual: actual, available: available };
    }

    // ResizeObserver hook for the activity rail. Idempotent — the
    // observer is created once and reused across renders. Re-runs the
    // layout pass whenever the rail's bounding box changes (e.g. the
    // bottom console drawer expanding live during a splitter drag).
    var _activityRailObserver = null;
    var _activityRailObservedEl = null;
    function _attachActivityRailObserver() {
        if (typeof ResizeObserver === 'undefined') return;
        var rail = document.getElementById('bowire-activity-rail');
        if (!rail) return;
        if (rail === _activityRailObservedEl) return;
        if (_activityRailObserver) _activityRailObserver.disconnect();
        _activityRailObserver = new ResizeObserver(function () {
            if (typeof _layoutActivityRail === 'function') _layoutActivityRail();
        });
        _activityRailObserver.observe(rail);
        _activityRailObservedEl = rail;
    }

    function attachSidebarSplitterDrag() {
        var splitter = document.getElementById('bowire-sidebar-splitter');
        var app = document.getElementById('bowire-app');
        if (!splitter || !app || splitter.dataset.dragHooked === '1') return;
        splitter.dataset.dragHooked = '1';
        // Helper — the collapsed marker lives on #bowire-app-body
        // (the app's inner row container), NOT document.body. Reading
        // it live keeps the handlers in sync with the latest render.
        function _isSidebarCollapsed() {
            var appBody = document.getElementById('bowire-app-body');
            return !!(appBody && appBody.classList.contains('bowire-sidebar-is-collapsed'));
        }
        // Click delegate with built-in double-click detection +
        // post-toggle dead-zone. Same source of truth for single +
        // double click; the browser's dblclick event is morphdom-
        // fragile (the first click can swap the splitter node before
        // dblclick fires). Tracking lastClickTime ourselves is robust.
        //
        // Behaviour:
        //   collapsed + ANY click on bar → expand, then ignore the
        //     trailing click of a dblclick for 500 ms so the user's
        //     double-tap doesn't toggle back to collapsed (which is
        //     the natural reaction if you expect double-click to work
        //     symmetrically with the expanded variant).
        //   expanded  + single click on bar → nothing (would be too
        //     easy to collapse accidentally aiming at the chevron).
        //   expanded  + double click on bar → collapse.
        var _lastClickTs = 0;
        var _toggleDeadUntil = 0;
        splitter.addEventListener('click', function (e) {
            // Click on chevron: its own onClick handles it; don't double-fire.
            if (e.target.closest('.bowire-sidebar-edge-toggle')) {
                _lastClickTs = 0;
                return;
            }
            // Suppress the browser's text-selection default on rapid
            // double-clicks. With this gone, dblclick on the bar no
            // longer triggers the OS / browser quick-action menu.
            e.preventDefault();
            var now = Date.now();
            // Inside the dead-zone (just expanded via the previous
            // click)? Swallow this click. Symmetry: a dblclick on the
            // collapsed bar reads as 'expand' (not 'expand-then-toggle').
            if (now < _toggleDeadUntil) {
                _lastClickTs = 0;
                return;
            }
            var isDouble = (now - _lastClickTs) < 500;
            _lastClickTs = isDouble ? 0 : now;
            if (_isSidebarCollapsed()) {
                if (typeof toggleSidebarCollapsed === 'function') toggleSidebarCollapsed();
                _toggleDeadUntil = now + 500;
            } else if (isDouble) {
                if (typeof toggleSidebarCollapsed === 'function') toggleSidebarCollapsed();
                _toggleDeadUntil = now + 500;
            }
        });
        // Suppress the browser's default dblclick action (text select,
        // word-highlight, OS quick-look) on the bar. The 'click'
        // handler above carries the toggle logic; this just keeps the
        // browser from drawing its own menu on top.
        splitter.addEventListener('dblclick', function (e) {
            if (e.target.closest('.bowire-sidebar-edge-toggle')) return;
            e.preventDefault();
        });
        splitter.addEventListener('mousedown', function (e) {
            // When the sidebar is collapsed: skip drag wiring — the
            // click delegate above handles the expand. The splitter
            // has cursor:pointer + no resize semantics.
            if (_isSidebarCollapsed()) {
                return;
            }
            // Don't preventDefault yet — we don't know if this is a
            // drag or a click on a child (like the chevron-toggle).
            // The browser will dispatch the chevron's click event on
            // mouseup-without-significant-movement; preventDefault
            // here would suppress it. We DO call preventDefault as
            // soon as a real drag is detected (below).
            e.stopPropagation();

            // Drag-vs-click threshold. Kept tiny (2 px) so the
            // chevron-toggle still clicks reliably on a steady
            // mousedown→mouseup, but small enough that the initial
            // "snap" when drag activates is imperceptible. Any
            // intentional drag overshoots 2 px in the first frame.
            var DRAG_THRESHOLD = 2;
            var startClientX = e.clientX;
            var dragStarted = false;

            // Pattern C extension — drag past this threshold (well
            // below SIDEBAR_WIDTH_MIN) triggers a collapse on mouse-
            // up. While dragging in the danger zone the sidebar gets
            // a translucent overlay as a "release to hide" preview.
            var COLLAPSE_THRESHOLD = 120; // px relative to the app
            var inCollapseZone = false;
            var sidebarEl = document.querySelector('#bowire-sidebar');
            // Lock in the click offset relative to the splitter's left
            // edge at mousedown — that's how far inside the splitter
            // the user grabbed. Subtracting it from clientX during
            // move keeps the splitter's left edge aligned with the
            // grabbed-point so the splitter follows the cursor cleanly
            // instead of jumping ahead by the offset.
            var splitterRect = splitter.getBoundingClientRect();
            var grabOffsetX = e.clientX - splitterRect.left;

            function _enterDragMode() {
                if (dragStarted) return;
                dragStarted = true;
                document.body.style.cursor = 'ew-resize';
                document.body.style.userSelect = 'none';
                app.classList.add('is-resizing');
                // No re-baseline of grabOffsetX. With THRESHOLD=2 the
                // implied 2 px "snap" is small enough to be invisible,
                // and keeping the original grabOffsetX means the
                // cursor stays at the same relative position INSIDE
                // the splitter for the entire drag (no offset drift).
            }

            function onMove(ev) {
                // Threshold gate — small wiggles below DRAG_THRESHOLD
                // don't enter drag mode, so a quick click on the
                // chevron stays a click.
                if (!dragStarted) {
                    if (Math.abs(ev.clientX - startClientX) < DRAG_THRESHOLD) return;
                    _enterDragMode();
                }
                // Compute sidebar width relative to the app container's
                // left edge. The activity rail (48 px fixed) sits BEFORE
                // the sidebar, so its width has to be subtracted from
                // the cursor's app-relative X to get the sidebar's right
                // edge — without it the sidebar lands 48 px too wide
                // and the splitter visibly jumps right of the cursor on
                // the first move. getBoundingClientRect on the rail
                // catches future width changes; .width is 0 when the
                // rail isn't in the DOM (collapsed / embedded mode).
                var rect = app.getBoundingClientRect();
                var rail = document.getElementById('bowire-activity-rail');
                var railWidth = rail ? rail.getBoundingClientRect().width : 0;
                var raw = Math.round(ev.clientX - rect.left - railWidth - grabOffsetX);
                inCollapseZone = raw < COLLAPSE_THRESHOLD;
                if (sidebarEl) sidebarEl.classList.toggle('bowire-sidebar-collapse-preview', inCollapseZone);

                var w = raw;
                if (w < SIDEBAR_WIDTH_MIN) w = SIDEBAR_WIDTH_MIN;
                if (w > SIDEBAR_WIDTH_MAX) w = SIDEBAR_WIDTH_MAX;
                // Also cap at 70% of the viewport to prevent the user
                // from pushing the main pane to zero on small screens.
                var cap = Math.round(window.innerWidth * 0.7);
                if (w > cap) w = cap;
                sidebarWidth = w;
                app.style.setProperty('--bowire-sidebar-width', w + 'px');
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                // No drag means no resize happened — nothing to clean
                // up or persist. The browser will still dispatch the
                // chevron's click event for the toggle action.
                if (!dragStarted) return;
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                app.classList.remove('is-resizing');
                if (sidebarEl) sidebarEl.classList.remove('bowire-sidebar-collapse-preview');
                try { localStorage.setItem(SIDEBAR_WIDTH_KEY, String(sidebarWidth)); } catch { /* ignore */ }
                // Pattern C — if the user released the drag inside
                // the collapse zone, finish the gesture by hiding the
                // sidebar entirely. The width they had still persists
                // for the next show.
                if (inCollapseZone) {
                    setSidebarCollapsed(true);
                    render();
                }
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });

        // Double-click on the splitter toggles the sidebar. Composes
        // with the chevron-button (pattern A) sitting on the same
        // splitter — chevron is the discoverable affordance, dblclick
        // is the muscle-memory shortcut once users know the pattern.
        // The dblclick event fires AFTER two mousedown→mouseup pairs
        // without intervening drag motion, so a normal resize drag
        // won't accidentally trigger it.
        splitter.addEventListener('dblclick', function (e) {
            e.preventDefault();
            e.stopPropagation();
            toggleSidebarCollapsed();
        });
    }

    // ---- Unified right-side drawer (#299) ----
    // Hosts Assistant + Help in a single right-edge slot. When both
    // panels are open a tab strip appears in the header; when only
    // one is open the chrome is identical to the legacy single
    // drawer. Close per-tab via the X on each tab; close drawer-wide
    // is implicit (closing the last tab dismisses the chrome).
    //
    // Active tab is sticky (localStorage via rightDrawerActiveTab in
    // prologue) so reopening lands the operator on the panel they
    // used last.
    // #164 — pass/fail accessory for the Tests drawer tab. Counts
    // results of the last assertion run for the active method. Returns
    // null when there's nothing to show (no method, no assertions).
    function _testsTabAccessory() {
        if (!selectedMethod || !selectedService) return null;
        if (typeof getTestsFor !== 'function') return null;
        var tests;
        try { tests = getTestsFor(selectedService.name, selectedMethod.name); }
        catch { return null; }
        if (!Array.isArray(tests) || tests.length === 0) return null;
        var results = (typeof lastAssertionResults === 'object' && lastAssertionResults) || {};
        var pass = 0, fail = 0, untested = 0;
        for (var i = 0; i < tests.length; i++) {
            var r = results[tests[i].id];
            if (r === undefined) untested++;
            else if (r.pass) pass++;
            else fail++;
        }
        var label, cls, title;
        if (fail > 0) {
            label = pass + '/' + tests.length;
            cls = 'bowire-tests-tab-accessory bowire-tests-tab-accessory-fail';
            title = fail + ' failing · ' + pass + ' passing · ' + untested + ' untested';
        } else if (untested === tests.length) {
            label = String(tests.length);
            cls = 'bowire-tests-tab-accessory';
            title = tests.length + ' assertion' + (tests.length === 1 ? '' : 's') + ' — none run yet';
        } else {
            label = pass + '/' + tests.length;
            cls = 'bowire-tests-tab-accessory bowire-tests-tab-accessory-pass';
            title = pass + ' passing · ' + untested + ' untested';
        }
        return el('span', { className: cls, title: title, textContent: label });
    }

    // #168 — Activity drawer body. Chronological list of every
    // recorded action (newest at top), each row with a one-line
    // description, a relative timestamp, and an Undo button while the
    // entry is still reversible. Expired entries (post-reload, closure
    // is gone) render greyed-out so the timeline still tells the
    // operator what happened, just without the undo affordance.
    function _renderActivityDrawerBody() {
        var wrap = el('div', { className: 'bowire-activity-drawer-body' });
        var header = el('div', { className: 'bowire-activity-drawer-header' });
        var redoableCount = (typeof actionLogRedoStack !== 'undefined'
            && Array.isArray(actionLogRedoStack)) ? actionLogRedoStack.length : 0;
        // #194 — multi-select bulk-undo. activitySelected lives in
        // prologue.js so other surfaces (e.g. command palette) could
        // pivot off it later. When at least one row is checked, the
        // header gains an 'Undo N selected' button that walks the
        // selected ids in newest-first log order.
        var selCount = (typeof activitySelected !== 'undefined'
            && activitySelected && typeof activitySelected.size === 'number')
            ? activitySelected.size : 0;
        header.appendChild(el('button', {
            type: 'button',
            className: 'bowire-activity-action',
            disabled: (typeof availableActionCount === 'function' && availableActionCount() === 0) ? 'disabled' : null,
            title: 'Undo most recent action (Ctrl/Cmd+Z)',
            textContent: 'Undo',
            onClick: function () {
                if (typeof undoLastAction === 'function') {
                    var u = undoLastAction();
                    if (u && typeof toast === 'function') toast('Undone: ' + u.title, 'info');
                    render();
                }
            }
        }));
        header.appendChild(el('button', {
            type: 'button',
            className: 'bowire-activity-action',
            disabled: redoableCount === 0 ? 'disabled' : null,
            title: 'Redo last undone action (Ctrl/Cmd+Shift+Z)',
            textContent: 'Redo',
            onClick: function () {
                if (typeof redoLastAction === 'function') {
                    var r = redoLastAction();
                    if (r && typeof toast === 'function') toast('Redone: ' + r.title, 'info');
                    render();
                }
            }
        }));
        if (selCount > 0) {
            header.appendChild(el('button', {
                type: 'button',
                className: 'bowire-activity-action bowire-activity-action-primary',
                title: 'Roll back the ' + selCount + ' selected actions in newest-first order',
                textContent: 'Undo ' + selCount + ' selected',
                onClick: function () {
                    if (typeof undoActionsByIds !== 'function') return;
                    var rolled = undoActionsByIds(Array.from(activitySelected));
                    activitySelected.clear();
                    if (rolled.length > 0 && typeof toast === 'function') {
                        toast('Undone ' + rolled.length + ' action' + (rolled.length === 1 ? '' : 's'), 'info');
                    }
                    render();
                }
            }));
            header.appendChild(el('button', {
                type: 'button',
                className: 'bowire-activity-action',
                title: 'Clear selection',
                textContent: 'Clear selection',
                onClick: function () {
                    activitySelected.clear();
                    render();
                }
            }));
        }
        header.appendChild(el('span', { style: 'flex:1' }));
        header.appendChild(el('button', {
            type: 'button',
            className: 'bowire-activity-action bowire-activity-action-danger',
            disabled: (typeof actionLog !== 'undefined' && actionLog.length === 0) ? 'disabled' : null,
            title: 'Clear the timeline (the underlying changes stay in place)',
            textContent: 'Clear',
            onClick: function () {
                if (typeof clearActionLog === 'function') clearActionLog();
                render();
            }
        }));
        wrap.appendChild(header);

        var list = el('div', { className: 'bowire-activity-list' });
        if (typeof actionLog === 'undefined' || actionLog.length === 0) {
            list.appendChild(el('p', {
                className: 'bowire-drawer-empty',
                textContent: 'No recorded actions yet. Reversible changes — deletions, renames, toggles — land here as you make them.'
            }));
        } else {
            actionLog.forEach(function (entry) {
                var isSelectable = entry.status === 'available' && typeof entry.undoFn === 'function';
                var isChecked = isSelectable && activitySelected && activitySelected.has(entry.id);
                var row = el('div', {
                    className: 'bowire-activity-row bowire-activity-row-' + entry.status
                        + (isChecked ? ' bowire-activity-row-selected' : '')
                });
                // #194 — per-row checkbox for multi-select. Only
                // selectable entries (status==='available' + has
                // undoFn) get a real checkbox; expired / undone
                // entries render a reserved-width spacer so the
                // rail / title columns stay aligned.
                if (isSelectable) {
                    row.appendChild(el('input', {
                        type: 'checkbox',
                        className: 'bowire-activity-row-check',
                        checked: isChecked ? 'checked' : null,
                        'aria-label': 'Select for bulk undo',
                        onChange: function (e) {
                            if (e.target.checked) activitySelected.add(entry.id);
                            else activitySelected.delete(entry.id);
                            render();
                        }
                    }));
                } else {
                    row.appendChild(el('span', { className: 'bowire-activity-row-check-spacer' }));
                }
                var meta = el('div', { className: 'bowire-activity-meta' });
                meta.appendChild(el('div', {
                    className: 'bowire-activity-title',
                    textContent: entry.title
                }));
                meta.appendChild(el('div', {
                    className: 'bowire-activity-time',
                    textContent: _formatRelativeTime(entry.ts) + ' · ' + entry.kind
                        + (entry.rail ? ' · ' + entry.rail : '')
                        + (entry.status === 'undone' ? ' · undone'
                            : (entry.status === 'expired' ? ' · expired' : ''))
                }));
                row.appendChild(meta);
                if (isSelectable) {
                    row.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-activity-row-undo',
                        title: 'Undo this action',
                        textContent: 'Undo',
                        onClick: function () {
                            try {
                                entry.undoFn();
                                entry.status = 'undone';
                                if (entry.redoFn) actionLogRedoStack.unshift(entry);
                                activitySelected.delete(entry.id);
                                _persistActionLog();
                                if (typeof toast === 'function') toast('Undone: ' + entry.title, 'info');
                                render();
                            } catch (e) {
                                console.warn('[actionLog] undo failed', entry.kind, e);
                                if (typeof toast === 'function') toast('Undo failed: ' + entry.title, 'error');
                            }
                        }
                    }));
                }
                list.appendChild(row);
            });
        }
        wrap.appendChild(list);
        return wrap;
    }

    function _formatRelativeTime(ts) {
        if (!ts) return 'just now';
        var diff = Date.now() - ts;
        if (diff < 0 || diff < 30 * 1000) return 'just now';
        if (diff < 60 * 1000) return Math.floor(diff / 1000) + 's ago';
        if (diff < 60 * 60 * 1000) return Math.floor(diff / 60000) + 'm ago';
        if (diff < 24 * 60 * 60 * 1000) return Math.floor(diff / 3600000) + 'h ago';
        return Math.floor(diff / 86400000) + 'd ago';
    }

    // #296 — Global Trash drawer. Modal-style overlay aggregating
    // every soft-deleted bucket in the active workspace: recordings
    // trash, collections trash, recently-closed request tabs, and
    // (placeholder until #194) workspaces. The per-rail trash
    // sections in the sidebar stay untouched — this is an additional
    // entry, not a replacement.
    //
    // TTL display per item is "Deleted N days/hours ago — will be
    // purged in (30-N) days" so the operator sees both how recent
    // the delete was and how much window remains. Aggregate
    // actions (Empty trash / Restore all) sit at the footer and
    // confirm before mutating.
    function renderGlobalTrashOverlay() {
        if (!globalTrashOpen) return null;

        var rec = (typeof recordingsTrash !== 'undefined' && Array.isArray(recordingsTrash))
            ? recordingsTrash : [];
        var col = (typeof collectionsTrash !== 'undefined' && Array.isArray(collectionsTrash))
            ? collectionsTrash : [];
        var tabs = (typeof tabsTrash !== 'undefined' && Array.isArray(tabsTrash))
            ? tabsTrash : [];
        // #194 — workspaces are now a real bucket here.
        var wsTrashLocal = (typeof workspacesTrash !== 'undefined' && Array.isArray(workspacesTrash))
            ? workspacesTrash : [];
        var totalCount = rec.length + col.length + tabs.length + wsTrashLocal.length;

        var backdrop = el('div', {
            id: 'bowire-trash-backdrop',
            className: 'bowire-trash-backdrop',
            onClick: function (e) {
                if (e.target === e.currentTarget) {
                    globalTrashOpen = false;
                    render();
                }
            }
        });
        var modal = el('div', {
            className: 'bowire-trash-modal',
            onClick: function (e) { e.stopPropagation(); }
        });

        // Header
        var header = el('div', { className: 'bowire-trash-modal-header' });
        header.appendChild(el('div', { className: 'bowire-trash-modal-title-row' },
            el('span', {
                className: 'bowire-trash-modal-title-icon',
                innerHTML: svgIcon('trash'),
                style: 'width:16px;height:16px;display:flex'
            }),
            el('span', {
                className: 'bowire-trash-modal-title',
                textContent: 'Trash — ' + totalCount + ' item' + (totalCount === 1 ? '' : 's')
            })
        ));
        header.appendChild(el('button', {
            type: 'button',
            className: 'bowire-trash-modal-close',
            title: 'Close (Esc)',
            'aria-label': 'Close trash drawer',
            innerHTML: svgIcon('close'),
            onClick: function () {
                globalTrashOpen = false;
                render();
            }
        }));
        modal.appendChild(header);

        var body = el('div', { className: 'bowire-trash-modal-body' });

        body.appendChild(_renderTrashBucketSection({
            id: 'recordings',
            label: 'Recordings',
            entries: rec,
            nameOf: function (e) { return e && e.name ? e.name : '(unnamed recording)'; },
            onRestore: function (t) {
                if (!t || !t.entry) return;
                if (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) {
                    recordingsList.splice(Math.min(t.originalIdx || recordingsList.length,
                        recordingsList.length), 0, t.entry);
                    if (typeof persistRecordings === 'function') persistRecordings();
                }
                var idx = rec.indexOf(t);
                if (idx >= 0) rec.splice(idx, 1);
                if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
            },
            onDeleteForever: function (t) {
                var idx = rec.indexOf(t);
                if (idx >= 0) rec.splice(idx, 1);
                if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
            }
        }));

        body.appendChild(_renderTrashBucketSection({
            id: 'collections',
            label: 'Collections',
            entries: col,
            nameOf: function (e) { return e && e.name ? e.name : '(unnamed collection)'; },
            onRestore: function (t) {
                if (!t || !t.entry) return;
                if (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) {
                    collectionsList.splice(Math.min(t.originalIdx || collectionsList.length,
                        collectionsList.length), 0, t.entry);
                    if (typeof persistCollections === 'function') persistCollections();
                }
                var idx = col.indexOf(t);
                if (idx >= 0) col.splice(idx, 1);
                if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
            },
            onDeleteForever: function (t) {
                var idx = col.indexOf(t);
                if (idx >= 0) col.splice(idx, 1);
                if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
            }
        }));

        body.appendChild(_renderTrashBucketSection({
            id: 'tabs',
            label: 'Recently closed tabs',
            entries: tabs,
            nameOf: function (e) {
                if (!e) return '(unknown tab)';
                if (e.methodKey && e.serviceKey) return e.serviceKey + ' · ' + e.methodKey;
                if (e.methodKey) return e.methodKey;
                if (e.serviceKey) return e.serviceKey;
                return '(unnamed tab)';
            },
            onRestore: function (t) {
                if (typeof restoreClosedTab === 'function') {
                    var ok = restoreClosedTab(t);
                    if (ok) {
                        var idx = tabs.indexOf(t);
                        if (idx >= 0) tabs.splice(idx, 1);
                        if (typeof persistTabsTrash === 'function') persistTabsTrash();
                    }
                }
            },
            onDeleteForever: function (t) {
                var idx = tabs.indexOf(t);
                if (idx >= 0) tabs.splice(idx, 1);
                if (typeof persistTabsTrash === 'function') persistTabsTrash();
            }
        }));

        // #194 — Workspaces bucket is now live. Soft-deleted via
        // deleteWorkspace → workspacesTrash; restore re-creates the
        // workspace + every per-workspace bucket from the snapshot.
        var ws = (typeof workspacesTrash !== 'undefined' && Array.isArray(workspacesTrash))
            ? workspacesTrash : [];
        body.appendChild(_renderTrashBucketSection({
            id: 'workspaces',
            label: 'Workspaces',
            entries: ws,
            nameOf: function (e) {
                if (!e || !e.workspace) return '(unnamed workspace)';
                return e.workspace.name || '(unnamed workspace)';
            },
            onRestore: function (t) {
                if (typeof restoreWorkspaceFromTrash === 'function') {
                    restoreWorkspaceFromTrash(t);
                }
            },
            onDeleteForever: function (t) {
                if (typeof purgeWorkspaceFromTrash === 'function') {
                    purgeWorkspaceFromTrash(t);
                }
            }
        }));

        modal.appendChild(body);

        // Footer with aggregate actions.
        var footer = el('div', { className: 'bowire-trash-modal-footer' });
        var emptyDisabled = totalCount === 0;
        var restoreDisabled = totalCount === 0;
        footer.appendChild(el('button', {
            type: 'button',
            className: 'bowire-trash-modal-action bowire-trash-modal-action-danger',
            disabled: emptyDisabled ? 'disabled' : null,
            title: emptyDisabled ? 'Trash is already empty' : 'Permanently delete every trashed item',
            textContent: 'Empty trash',
            onClick: function () {
                if (emptyDisabled) return;
                if (typeof bowireConfirm === 'function') {
                    bowireConfirm(
                        'Permanently delete all ' + totalCount + ' trashed items? This cannot be undone.',
                        function () {
                            if (Array.isArray(recordingsTrash)) recordingsTrash.length = 0;
                            if (Array.isArray(collectionsTrash)) collectionsTrash.length = 0;
                            if (Array.isArray(tabsTrash)) tabsTrash.length = 0;
                            // #194 — workspaces bucket: a real purge
                            // also wipes the per-workspace localStorage
                            // namespace via purgeWorkspaceFromTrash so
                            // the entries don't leave orphans behind.
                            if (Array.isArray(workspacesTrash)) {
                                while (workspacesTrash.length > 0) {
                                    if (typeof purgeWorkspaceFromTrash === 'function') {
                                        purgeWorkspaceFromTrash(workspacesTrash[0]);
                                    } else {
                                        workspacesTrash.shift();
                                    }
                                }
                                if (typeof persistWorkspacesTrash === 'function') persistWorkspacesTrash();
                            }
                            if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
                            if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
                            if (typeof persistTabsTrash === 'function') persistTabsTrash();
                            render();
                        },
                        { title: 'Empty trash', confirmText: 'Delete ' + totalCount, danger: true }
                    );
                }
            }
        }));
        footer.appendChild(el('button', {
            type: 'button',
            className: 'bowire-trash-modal-action',
            disabled: restoreDisabled ? 'disabled' : null,
            title: restoreDisabled ? 'Trash is empty' : 'Restore every trashed item back to its original list',
            textContent: 'Restore all',
            onClick: function () {
                if (restoreDisabled) return;
                if (typeof bowireConfirm === 'function') {
                    bowireConfirm(
                        'Restore all ' + totalCount + ' trashed items?',
                        function () {
                            // Recordings
                            if (Array.isArray(recordingsTrash)) {
                                for (var i = recordingsTrash.length - 1; i >= 0; i--) {
                                    var rt = recordingsTrash[i];
                                    if (rt && rt.entry && Array.isArray(recordingsList)) {
                                        recordingsList.splice(Math.min(rt.originalIdx || recordingsList.length,
                                            recordingsList.length), 0, rt.entry);
                                    }
                                }
                                recordingsTrash.length = 0;
                                if (typeof persistRecordings === 'function') persistRecordings();
                                if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
                            }
                            // Collections
                            if (Array.isArray(collectionsTrash)) {
                                for (var j = collectionsTrash.length - 1; j >= 0; j--) {
                                    var ct = collectionsTrash[j];
                                    if (ct && ct.entry && Array.isArray(collectionsList)) {
                                        collectionsList.splice(Math.min(ct.originalIdx || collectionsList.length,
                                            collectionsList.length), 0, ct.entry);
                                    }
                                }
                                collectionsTrash.length = 0;
                                if (typeof persistCollections === 'function') persistCollections();
                                if (typeof persistCollectionsTrash === 'function') persistCollectionsTrash();
                            }
                            // Tabs
                            if (Array.isArray(tabsTrash)) {
                                for (var k = tabsTrash.length - 1; k >= 0; k--) {
                                    var tt = tabsTrash[k];
                                    if (typeof restoreClosedTab === 'function') restoreClosedTab(tt);
                                }
                                tabsTrash.length = 0;
                                if (typeof persistTabsTrash === 'function') persistTabsTrash();
                            }
                            // #194 — Workspaces. Walk newest-first
                            // (matches the other buckets) so the
                            // original order is preserved.
                            if (Array.isArray(workspacesTrash)) {
                                for (var w = workspacesTrash.length - 1; w >= 0; w--) {
                                    if (typeof restoreWorkspaceFromTrash === 'function') {
                                        restoreWorkspaceFromTrash(workspacesTrash[w]);
                                    }
                                }
                            }
                            render();
                        },
                        { title: 'Restore all', confirmText: 'Restore ' + totalCount }
                    );
                }
            }
        }));
        footer.appendChild(el('span', { style: 'flex:1' }));
        footer.appendChild(el('button', {
            type: 'button',
            className: 'bowire-trash-modal-action',
            textContent: 'Cancel',
            onClick: function () {
                globalTrashOpen = false;
                render();
            }
        }));
        modal.appendChild(footer);

        backdrop.appendChild(modal);
        return backdrop;
    }

    // Per-bucket section inside the global trash overlay. Header row
    // is always rendered (so the count is visible even when collapsed
    // / empty); the item list only expands when the operator clicks
    // through. Items show: name, "Deleted X days ago — will be
    // purged in (30-X) days" TTL, and Restore + Delete-forever
    // actions. The bucket header keeps its own expand/collapse state
    // in globalTrashSectionOpen so a single render pass doesn't lose
    // the operator's drilldown.
    function _renderTrashBucketSection(opts) {
        var section = el('div', { className: 'bowire-trash-bucket' });
        var count = opts.entries.length;
        var isOpen = !!(globalTrashSectionOpen && globalTrashSectionOpen[opts.id]);
        var disabled = count === 0;

        var header = el('div', {
            className: 'bowire-trash-bucket-header'
                + (disabled ? ' bowire-trash-bucket-header-disabled' : ''),
            onClick: function () {
                if (disabled) return;
                globalTrashSectionOpen[opts.id] = !isOpen;
                render();
            }
        });
        header.appendChild(el('span', {
            className: 'bowire-trash-bucket-label',
            textContent: opts.label
        }));
        header.appendChild(el('span', {
            className: 'bowire-trash-bucket-count',
            textContent: '(' + count + ')'
        }));
        header.appendChild(el('span', { style: 'flex:1' }));
        header.appendChild(el('span', {
            className: 'bowire-trash-bucket-caret',
            textContent: disabled ? '' : (isOpen ? 'Collapse ▴' : 'Expand ▾')
        }));
        section.appendChild(header);

        if (isOpen && count > 0) {
            var list = el('div', { className: 'bowire-trash-bucket-list' });
            opts.entries.slice().forEach(function (t) {
                if (!t) return;
                var ageMs = Date.now() - (t.deletedAt || Date.now());
                var ageDays = Math.floor(ageMs / (24 * 60 * 60 * 1000));
                var ageLabel;
                if (ageMs < 60 * 60 * 1000) ageLabel = 'less than an hour ago';
                else if (ageMs < 24 * 60 * 60 * 1000) ageLabel = Math.floor(ageMs / 3600000) + 'h ago';
                else ageLabel = ageDays + ' day' + (ageDays === 1 ? '' : 's') + ' ago';
                var purgeIn = Math.max(0, 30 - ageDays);
                var ttlNote = 'Deleted ' + ageLabel
                    + ' — will be purged in ' + purgeIn + ' day' + (purgeIn === 1 ? '' : 's');

                var row = el('div', { className: 'bowire-trash-bucket-row' });
                row.appendChild(el('div', { className: 'bowire-trash-bucket-row-meta' },
                    el('div', {
                        className: 'bowire-trash-bucket-row-name',
                        textContent: opts.nameOf(t.entry)
                    }),
                    el('div', {
                        className: 'bowire-trash-bucket-row-ttl',
                        textContent: ttlNote
                    })
                ));
                row.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-trash-bucket-row-action',
                    title: 'Restore',
                    textContent: 'Restore',
                    onClick: function () {
                        try { opts.onRestore(t); } catch (e) {
                            console.warn('[trash] restore failed', opts.id, e);
                        }
                        render();
                    }
                }));
                row.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-trash-bucket-row-action bowire-trash-bucket-row-action-danger',
                    title: 'Delete forever',
                    textContent: 'Delete forever',
                    onClick: function () {
                        if (typeof bowireConfirm === 'function') {
                            bowireConfirm(
                                'Permanently delete "' + opts.nameOf(t.entry) + '"? This cannot be undone.',
                                function () {
                                    try { opts.onDeleteForever(t); } catch (e) {
                                        console.warn('[trash] delete-forever failed', opts.id, e);
                                    }
                                    render();
                                },
                                { title: 'Delete forever', confirmText: 'Delete', danger: true }
                            );
                        }
                    }
                }));
                list.appendChild(row);
            });
            section.appendChild(list);
        }
        return section;
    }

    function renderUnifiedRightDrawer(open) {
        var tabs = [];
        if (open.assistant) {
            // Build the Assistant status dot once, reuse it as the
            // tab's status accessory. Same three states as the
            // legacy single-drawer header.
            var aiSt = (typeof window.__bowireAi === 'object' && window.__bowireAi)
                ? window.__bowireAi.getStatus()
                : null;
            var statusClass, statusTitle;
            if (aiSt && aiSt.hasClient) {
                statusClass = 'bowire-ai-status-dot bowire-ai-status-dot-connected';
                statusTitle = 'Connected · ' + (aiSt.providerId || 'unknown')
                    + ' · ' + (aiSt.model || '(default model)');
            } else if (aiSt === null) {
                statusClass = 'bowire-ai-status-dot bowire-ai-status-dot-missing';
                statusTitle = 'AI package not installed — chat unavailable. Hints still work.';
            } else {
                statusClass = 'bowire-ai-status-dot bowire-ai-status-dot-idle';
                statusTitle = 'No model configured — open Settings → Assistant to connect one.';
            }
            tabs.push({
                id: 'assistant',
                label: 'Assistant',
                accessory: el('span', {
                    className: statusClass,
                    title: statusTitle,
                    'aria-label': statusTitle
                }),
                closeTitle: 'Close Assistant (Ctrl+Shift+A)',
                onClose: function () {
                    aiDrawerOpen = false;
                    try { localStorage.setItem('bowire_ai_drawer_open', '0'); } catch { /* ignore */ }
                    render();
                },
                renderContent: function () {
                    return window.__bowireAi
                        ? window.__bowireAi.renderPanel()
                        : el('p', {
                            className: 'bowire-drawer-empty',
                            textContent: 'AI module not loaded.'
                        });
                }
            });
        }
        if (open.help) {
            var topicCount = (typeof helpTopics !== 'undefined' && helpTopics)
                ? helpTopics.length : 0;
            tabs.push({
                id: 'help',
                label: 'Help',
                accessory: topicCount > 0 ? el('span', {
                    className: 'bowire-help-topic-count',
                    title: topicCount + ' topic' + (topicCount === 1 ? '' : 's') + ' loaded',
                    textContent: String(topicCount)
                }) : null,
                closeTitle: 'Close Help (Esc)',
                onClose: function () {
                    if (typeof helpCloseDrawer === 'function') helpCloseDrawer();
                },
                renderContent: function () {
                    return _renderHelpDrawerContent();
                }
            });
        }
        // #164 v2 — Console moved out of the right-drawer tabs into a
        // bottom-attached panel (renderConsoleBottomDrawer) so it
        // behaves like an IDE terminal panel (full-width, resizable
        // height) rather than competing with Assistant / Help for
        // right-side real estate. Tests stays as a right-drawer tab.

        // #168 — Activity tab. Mounts when the operator opens it via
        // the Statusbar Activity pill. Accessory shows the available
        // (still reversible) action count; close decrements
        // activityDrawerOpen so the drawer falls back to a sibling
        // tab on next render.
        if (open.activity) {
            var availCnt = (typeof availableActionCount === 'function')
                ? availableActionCount() : 0;
            tabs.push({
                id: 'activity',
                label: 'Activity',
                accessory: availCnt > 0 ? el('span', {
                    className: 'bowire-help-topic-count',
                    title: availCnt + ' reversible action'
                        + (availCnt === 1 ? '' : 's'),
                    textContent: String(availCnt)
                }) : null,
                closeTitle: 'Close Activity',
                onClose: function () {
                    activityDrawerOpen = false;
                    try { localStorage.setItem('bowire_activity_drawer_open', '0'); }
                    catch { /* ignore */ }
                    render();
                },
                renderContent: function () {
                    return _renderActivityDrawerBody();
                }
            });
        }
        // #164 — Tests tab. Accessory shows pass/fail of the last
        // assertion run for the active method; '?' when assertions
        // exist but haven't been run yet. No accessory when there are
        // no assertions configured.
        if (open.tests) {
            var testsAcc = _testsTabAccessory();
            tabs.push({
                id: 'tests',
                label: 'Tests',
                accessory: testsAcc,
                closeTitle: 'Close Tests',
                onClose: function () {
                    testsDrawerOpen = false;
                    try { localStorage.setItem('bowire_tests_drawer_open', '0'); } catch { /* ignore */ }
                    render();
                },
                renderContent: function () {
                    if (!selectedMethod || !selectedService) {
                        return el('div', { className: 'bowire-tests-empty bowire-main-pad' },
                            el('p', {
                                className: 'bowire-drawer-empty',
                                textContent: 'Pick a method from the sidebar to see and edit its assertions. Tests run automatically after every successful response.'
                            })
                        );
                    }
                    return typeof renderTestsTab === 'function'
                        ? renderTestsTab()
                        : el('p', { textContent: 'Tests module not loaded.' });
                }
            });
        }

        // Resolve active tab. If the sticky pick isn't currently open
        // (e.g. user closed Assistant while Help was the active tab),
        // fall back to the first open tab.
        var activeId = rightDrawerActiveTab;
        if (!tabs.find(function (t) { return t.id === activeId; })) {
            activeId = tabs[0] ? tabs[0].id : null;
        }
        var activeTab = tabs.find(function (t) { return t.id === activeId; });

        // Single-tab path: delegate the whole shape to the renderDrawer
        // primitive (#115). A single open tab is visually identical to
        // a standalone drawer (title + accessory + close + content), so
        // there's no reason to hand-roll the chrome here. The multi-tab
        // path below stays inline because the header is a tab strip,
        // not a single title row — renderDrawer doesn't model that.
        if (tabs.length <= 1) {
            if (!activeTab) {
                return el('div', {
                    id: 'bowire-right-drawer',
                    className: 'bowire-drawer bowire-right-drawer',
                    role: 'complementary',
                    'aria-label': 'Drawer'
                });
            }
            return renderDrawer({
                id: 'bowire-right-drawer',
                className: 'bowire-right-drawer',
                title: activeTab.label,
                titleAccessory: activeTab.accessory,
                closeTitle: activeTab.closeTitle,
                closeAriaLabel: activeTab.closeTitle || ('Close ' + activeTab.label),
                ariaLabel: activeTab.label,
                onClose: activeTab.onClose,
                content: activeTab.renderContent
            });
        }

        var drawer = el('div', {
            id: 'bowire-right-drawer',
            className: 'bowire-drawer bowire-right-drawer bowire-right-drawer-tabbed',
            role: 'complementary',
            'aria-label': activeTab ? activeTab.label : 'Drawer'
        });

        // Tab strip header — VS Code / JetBrains style. Each tab
        // is a button with its accessory next to the label, and a
        // single drawer-level X on the right closes JUST the active
        // tab so the others stay accessible.
        var tabStrip = el('div', {
            className: 'bowire-right-drawer-tabs',
            role: 'tablist'
        });
        tabs.forEach(function (t) {
            var isActive = t.id === activeId;
            // Stable id per tab — morphdom keyed-matches the button
            // to its previous node, otherwise the OLD click listener
            // stays attached to the now-wrong tab when the tab list
            // gets re-rendered with a different active tab (the
            // listener was a closure over the previous render's `t`).
            var tabBtn = el('button', {
                id: 'bowire-right-drawer-tab-' + t.id,
                type: 'button',
                className: 'bowire-right-drawer-tab'
                    + (isActive ? ' is-active' : ''),
                role: 'tab',
                'aria-selected': isActive ? 'true' : 'false',
                onClick: function () {
                    rightDrawerActiveTab = t.id;
                    try { localStorage.setItem('bowire_right_drawer_active_tab', t.id); }
                    catch { /* ignore */ }
                    render();
                }
            });
            tabBtn.appendChild(el('span', {
                className: 'bowire-right-drawer-tab-label',
                textContent: t.label
            }));
            if (t.accessory) tabBtn.appendChild(t.accessory);
            tabStrip.appendChild(tabBtn);
        });
        // Single drawer-level close on the right — Per-tab Xs caused
        // operators to misclick and dismiss the panel they meant to
        // switch to. VS Code uses the same single-close convention on
        // its right-side tool strip.
        tabStrip.appendChild(el('span', { style: 'flex:1' }));
        if (activeTab) {
            tabStrip.appendChild(el('button', {
                id: 'bowire-right-drawer-close',
                type: 'button',
                className: 'bowire-drawer-close bowire-right-drawer-close',
                title: activeTab.closeTitle || 'Close',
                'aria-label': activeTab.closeTitle || ('Close ' + activeTab.label),
                innerHTML: svgIcon('close'),
                onClick: function () {
                    if (typeof activeTab.onClose === 'function') activeTab.onClose();
                }
            }));
        }
        drawer.appendChild(tabStrip);

        var contentWrap = el('div', { className: 'bowire-drawer-content' });
        if (activeTab) {
            try {
                var content = activeTab.renderContent();
                if (content) contentWrap.appendChild(content);
            } catch (e) {
                console.warn('[right-drawer] content render failed', e);
                contentWrap.appendChild(el('p', {
                    className: 'bowire-drawer-empty',
                    textContent: 'Panel failed to render.'
                }));
            }
        }
        drawer.appendChild(contentWrap);
        return drawer;
    }

    // ---- Connection pill (#93) ----
    // Topbar at-a-glance for "where am I connected and is everything
    // healthy?". Aggregates the per-URL connectionStatuses + service
    // counts + discoveryErrors into a single dot + summary text;
    // expands on hover/click into a per-URL list with statuses, service
    // counts, and any error message + retry affordance.
    //
    // Hidden in embedded mode (the host owns the URL, no value in
    // adding chrome around a knob the user can't turn). Otherwise
    // renders as a quiet pill until something needs attention — same
    // visual contract as the env-selector to its right.
    function renderConnectionPill() {
        if (uiMode === 'embedded') return null;

        // ---- Aggregate state ----
        // Order matters: error wins over connecting wins over connected.
        // 'firstRun' is a soft state for "no URLs at all yet" — encourages
        // the user to click and open the source-selector.
        var urls = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) ? serverUrls : [];
        var urlsPresent = urls.filter(function (u) { return !!u; });
        var aggregate;
        var summary;
        if (urlsPresent.length === 0) {
            aggregate = 'first-run';
            summary = 'Pick a URL';
        } else {
            var counts = { connected: 0, connecting: 0, error: 0, disconnected: 0 };
            for (var ci = 0; ci < urlsPresent.length; ci++) {
                var st = (connectionStatuses && connectionStatuses[urlsPresent[ci]]) || 'disconnected';
                counts[st] = (counts[st] || 0) + 1;
            }
            if (counts.error > 0) {
                aggregate = 'error';
                summary = counts.error + ' / ' + urlsPresent.length + ' failed';
            } else if (counts.connecting > 0) {
                aggregate = 'connecting';
                summary = counts.connecting + ' / ' + urlsPresent.length + ' connecting…';
            } else if (counts.connected === urlsPresent.length) {
                aggregate = 'connected';
                summary = urlsPresent.length === 1
                    ? truncateMiddle(urlsPresent[0], 28)
                    : 'All ' + urlsPresent.length + ' connected';
            } else {
                aggregate = 'partial';
                summary = counts.connected + ' / ' + urlsPresent.length + ' connected';
            }
        }

        // ---- Pill button (the hover anchor + click trigger) ----
        // #92 — click opens the full URL/Schema management view in
        // the main pane (sidebarView='sources'). Hover still shows
        // the per-URL detail popover via CSS.
        var pill = el('div', {
            className: 'bowire-conn-pill bowire-conn-pill-' + aggregate + ' bowire-conn-pill-clickable',
            title: 'Click to manage URLs and schemas',
            onClick: function () {
                // #152 — pill now routes to the Sources rail mode;
                // legacy sidebarView='sources' shim retired.
                railMode = 'sources';
                try { localStorage.setItem('bowire_rail_mode', 'sources'); } catch { /* ignore */ }
                render();
            }
        });
        // Title carries the at-a-glance summary so the operator can
        // get the state via hover even when the visible chrome is
        // just the lamp. Long URLs in the inline text didn't help
        // anyway — they were truncated and didn't fit the name.
        pill.setAttribute('title', summary);
        // Plug glyph rather than a bare 4-px coloured dot — readable
        // at a glance, sits on the same 14-px icon baseline as the
        // env-selector globe + the status-bar entries beside it.
        var dot = el('span', { className: 'bowire-conn-pill-dot', innerHTML: svgIcon('plug') });
        pill.appendChild(dot);

        // ---- Hover popover ----
        // CSS :hover handles the show/hide so we don't need a state var
        // for popover-open. Positioned absolutely below the pill.
        var popover = el('div', { className: 'bowire-conn-popover', role: 'tooltip' });

        if (aggregate === 'first-run') {
            popover.appendChild(el('div', { className: 'bowire-conn-popover-empty',
                textContent: 'No URLs configured yet. Add one via the sidebar or the welcome card.' }));
        } else {
            var header = el('div', { className: 'bowire-conn-popover-header' },
                el('span', { textContent: 'Connections' }),
                el('span', { className: 'bowire-conn-popover-count', textContent: urlsPresent.length + ' URL' + (urlsPresent.length === 1 ? '' : 's') })
            );
            popover.appendChild(header);

            var list = el('div', { className: 'bowire-conn-popover-list' });
            for (var li = 0; li < urlsPresent.length; li++) {
                (function (url) {
                    var status = (connectionStatuses && connectionStatuses[url]) || 'disconnected';
                    var err = (discoveryErrors && discoveryErrors[url]) || null;

                    // Per-URL service count. We walk the discovered
                    // services list and tally — both how many services
                    // came from this URL and the total method count, so
                    // the user sees the *real* surface they get from it.
                    var svcCount = 0;
                    var methodCount = 0;
                    if (typeof services !== 'undefined' && Array.isArray(services)) {
                        for (var si = 0; si < services.length; si++) {
                            // Only count services that actually carry
                            // methods — OpenAPI discovery sometimes emits
                            // a host-only "service" with no operations
                            // which would inflate the per-URL tally past
                            // what the tree shows.
                            if (services[si].originUrl === url
                                && Array.isArray(services[si].methods)
                                && services[si].methods.length > 0) {
                                svcCount++;
                                methodCount += services[si].methods.length;
                            }
                        }
                    }

                    var row = el('div', { className: 'bowire-conn-popover-row bowire-conn-row-' + status });

                    // Show the alias instead of the URL when one is
                    // assigned — keeps the popover scannable when the
                    // user has six staging clones with near-identical
                    // hostnames. Full URL still in the tooltip.
                    var aliased = (typeof serverUrlAliases !== 'undefined' && serverUrlAliases[url])
                        ? serverUrlAliases[url]
                        : '';
                    var topRow = el('div', { className: 'bowire-conn-popover-row-top' },
                        el('span', { className: 'bowire-conn-popover-row-dot' }),
                        el('span', {
                            className: 'bowire-conn-popover-row-url',
                            textContent: aliased || truncateMiddle(url, 36),
                            title: url
                        }),
                        el('span', {
                            className: 'bowire-conn-popover-row-status',
                            textContent: status === 'connected' ? 'OK'
                                       : status === 'connecting' ? '…'
                                       : status === 'error' ? 'failed'
                                       : 'idle'
                        })
                    );
                    row.appendChild(topRow);
                    if (aliased) {
                        row.appendChild(el('div', {
                            className: 'bowire-conn-popover-row-url-secondary',
                            textContent: truncateMiddle(url, 48),
                            title: url
                        }));
                    }

                    if (status === 'connected' && svcCount > 0) {
                        row.appendChild(el('div', { className: 'bowire-conn-popover-row-meta',
                            textContent: svcCount + ' service' + (svcCount === 1 ? '' : 's') + ' · ' + methodCount + ' method' + (methodCount === 1 ? '' : 's') }));
                    }
                    if (err) {
                        row.appendChild(el('div', { className: 'bowire-conn-popover-row-err',
                            textContent: err }));
                    }
                    list.appendChild(row);
                })(urlsPresent[li]);
            }
            popover.appendChild(list);
        }
        pill.appendChild(popover);
        return pill;
    }

    // truncateMiddle — collapse long URLs like
    //   "https://petstore3.swagger.io/api/v3/openapi.json"
    // into
    //   "https://petstor…3/openapi.json"
    // so the pill + popover rows have a predictable width without
    // dropping the discriminating tail (which is what tells two URLs
    // on the same host apart).
    function truncateMiddle(s, max) {
        if (!s || s.length <= max) return s || '';
        var keepStart = Math.ceil((max - 1) * 0.5);
        var keepEnd = Math.floor((max - 1) * 0.5);
        return s.slice(0, keepStart) + '…' + s.slice(-keepEnd);
    }

    // Schema Watch toggle button — extracted in #138 so both
    // topbar (legacy) and statusbar mount it from the same
    // helper.
    function renderWatchButton() {
        return el('button', {
            id: 'bowire-schema-watch-btn',
            className: 'bowire-theme-toggle-btn' + (isSchemaWatchActive() ? ' is-watching' : ''),
            title: isSchemaWatchActive()
                ? 'Schema watch active — click to stop'
                : 'Start schema watch (re-discover every 15s)',
            'aria-label': isSchemaWatchActive() ? 'Stop schema watch' : 'Start schema watch',
            onClick: function () {
                if (isSchemaWatchActive()) { stopSchemaWatch(); }
                else { startSchemaWatch(15000); }
                render();
            }
        }, el('span', {
            innerHTML: svgIcon('replay'),
            style: 'width:16px;height:16px;display:flex'
        }));
    }

    // ---- Statusbar (#138) ----
    // Thin strip pinned to the bottom of the workbench. IDE-style
    // ambient telemetry — connection state, active env, watch button,
    // and (when their issues ship) hint counts, save indicators,
    // workspace name. Hosts items moved out of the topbar so the
    // topbar reads as navigation + identity, not "everything".
    function renderStatusBar() {
        if (uiMode === 'embedded') return el('div', { id: 'bowire-statusbar', style: 'display:none' });
        var bar = el('div', { id: 'bowire-statusbar', className: 'bowire-statusbar' });

        // Left cluster — #127 save-state pill. Idle = no chrome (the
        // operator doesn't need to be told 'nothing happened just
        // now'). Saved = brief green-tinted check + 'Saved
        // <target>' for ~2 s. Failed = amber-tinted warning + the
        // failed-target label, sticks for ~4 s.
        // #127 — save-pill is clickable in standalone mode: opens the
        // workspace's user-folder in the host OS's file manager so the
        // operator can poke around with their usual tooling. The click
        // is gated on canOpenWorkspaceFolder (probed at boot from
        // /api/workspace/can-open-folder), which is false in embedded
        // hosts. Hover-affordance only materialises when the click
        // would actually do something.
        function _onSavePillClick() {
            if (!canOpenWorkspaceFolder) return;
            var ws = typeof activeWorkspace === 'function' ? activeWorkspace() : null;
            var qs = (ws && ws.id) ? ('?workspaceId=' + encodeURIComponent(ws.id)) : '';
            fetch(config.prefix + '/api/workspace/open-folder' + qs, { method: 'POST' })
                .then(function (r) {
                    if (!r.ok && typeof toast === 'function') {
                        toast('Couldn’t open the workspace folder — see the server log.', 'error');
                    }
                })
                .catch(function () {
                    if (typeof toast === 'function') {
                        toast('Network error opening the workspace folder.', 'error');
                    }
                });
        }
        var _savePillClickable = canOpenWorkspaceFolder;
        var _savePillTitleSuffix = _savePillClickable
            ? '\nClick to open this workspace’s folder'
            : '';

        var left = el('div', { className: 'bowire-statusbar-left' });
        if (saveState && saveState.kind === 'saved') {
            var ws = typeof activeWorkspace === 'function' ? activeWorkspace() : null;
            left.appendChild(el('span', {
                className: 'bowire-save-pill bowire-save-pill-saved'
                    + (_savePillClickable ? ' bowire-save-pill-clickable' : ''),
                title: (ws ? ('Saved to ' + ws.name) : 'Saved to localStorage') + _savePillTitleSuffix,
                onClick: _savePillClickable ? _onSavePillClick : null,
            },
                el('span', { className: 'bowire-save-pill-dot' }),
                el('span', { textContent: ws ? ('Saved to ' + ws.name) : ('Saved ' + saveState.target) })
            ));
        } else if (saveState && saveState.kind === 'failed') {
            left.appendChild(el('span', {
                className: 'bowire-save-pill bowire-save-pill-failed'
                    + (_savePillClickable ? ' bowire-save-pill-clickable' : ''),
                title: 'Save failed — check browser console' + _savePillTitleSuffix,
                onClick: _savePillClickable ? _onSavePillClick : null,
            },
                el('span', { className: 'bowire-save-pill-dot' }),
                el('span', { textContent: 'Save failed: ' + saveState.target })
            ));
        }
        bar.appendChild(left);

        var centre = el('div', { className: 'bowire-statusbar-centre' });
        bar.appendChild(centre);

        // Right cluster — system status (connection state + watch
        // toggle). Connection anchors to the rightmost edge per
        // maintainer call; env selector moved back to the topbar
        // so the editor-context switch lives next to navigation.
        var right = el('div', { className: 'bowire-statusbar-right' });
        // Console toggle — promoted to the statusbar so the activity
        // log is reachable from any rail (Discover / Home / Recordings
        // / …) instead of only from the response-pane / benchmark
        // toolbars where renderConsoleToggleButton mounts it. Click
        // toggles consoleOpen + (re-)renders the floating panel
        // directly; the panel writes into document.body, not the
        // render tree, so no full render() is needed.
        right.appendChild(el('button', {
            id: 'bowire-statusbar-console-btn',
            className: 'bowire-theme-toggle-btn' + (consoleOpen ? ' active' : ''),
            title: consoleOpen
                ? 'Hide console (activity log)'
                : 'Show console — request / response activity log (' + consoleLog.length + ')',
            'aria-label': 'Toggle console',
            onClick: function () {
                // #164 v2 — Console toggles a bottom-attached drawer
                // (renderConsoleBottomDrawer); render() picks it up
                // and mounts/unmounts via bowire-app-middle.
                consoleOpen = !consoleOpen;
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon('clock'),
                style: 'width:14px;height:14px;display:flex'
            }),
            consoleLog.length > 0
                ? el('span', { className: 'bowire-statusbar-console-count', textContent: String(consoleLog.length) })
                : null
        ));
        // #168 — Activity pill. Count of recent reversible actions
        // (status === 'available'); click toggles the Activity drawer
        // tab. Hides entirely when the log is empty so the statusbar
        // stays calm during low-activity sessions.
        var availCount = (typeof availableActionCount === 'function')
            ? availableActionCount() : 0;
        if (availCount > 0 || activityDrawerOpen) {
            right.appendChild(el('button', {
                id: 'bowire-statusbar-activity-btn',
                className: 'bowire-theme-toggle-btn' + (activityDrawerOpen ? ' active' : ''),
                title: activityDrawerOpen
                    ? 'Hide activity drawer'
                    : ('Activity log — ' + availCount + ' reversible action'
                        + (availCount === 1 ? '' : 's') + ' (Ctrl/Cmd+Z to undo)'),
                'aria-label': 'Toggle activity log',
                onClick: function () {
                    activityDrawerOpen = !activityDrawerOpen;
                    try { localStorage.setItem('bowire_activity_drawer_open',
                        activityDrawerOpen ? '1' : '0'); }
                    catch { /* ignore */ }
                    if (activityDrawerOpen) {
                        rightDrawerActiveTab = 'activity';
                        try { localStorage.setItem('bowire_right_drawer_active_tab', 'activity'); }
                        catch { /* ignore */ }
                    }
                    render();
                }
            },
                el('span', { innerHTML: svgIcon('history'),
                    style: 'width:14px;height:14px;display:flex' }),
                availCount > 0
                    ? el('span', { className: 'bowire-statusbar-console-count',
                        textContent: String(availCount) })
                    : null
            ));
        }
        // #164 — Tests drawer toggle. Peer of the Console button —
        // shows / hides the Tests tab in the unified right drawer.
        // Accessory dot inherits pass/fail color from the active
        // method's last run when present.
        right.appendChild(el('button', {
            id: 'bowire-statusbar-tests-btn',
            className: 'bowire-theme-toggle-btn' + (testsDrawerOpen ? ' active' : ''),
            title: testsDrawerOpen
                ? 'Hide tests drawer'
                : 'Show tests drawer — assertions for the active method',
            'aria-label': 'Toggle tests',
            onClick: function () {
                testsDrawerOpen = !testsDrawerOpen;
                try { localStorage.setItem('bowire_tests_drawer_open', testsDrawerOpen ? '1' : '0'); }
                catch { /* ignore */ }
                if (testsDrawerOpen) {
                    rightDrawerActiveTab = 'tests';
                    try { localStorage.setItem('bowire_right_drawer_active_tab', 'tests'); }
                    catch { /* ignore */ }
                }
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon('beaker'),
                style: 'width:14px;height:14px;display:flex'
            })
        ));
        // #135 split-toggle. Cycles horizontal ↔ vertical. Icon
        // shows the CURRENT layout (state-pattern, matches how
        // browser-engine toggles work) — click flips. Reflowing the
        // content + persisting happens in the onClick.
        right.appendChild(el('button', {
            id: 'bowire-split-toggle-btn',
            className: 'bowire-theme-toggle-btn',
            title: splitMode === 'vertical'
                ? 'Vertical split — click to switch to horizontal (Ctrl/Cmd+Alt+\\)'
                : 'Horizontal split — click to switch to vertical (Ctrl/Cmd+Alt+\\)',
            'aria-label': 'Toggle split orientation',
            onClick: function () {
                splitMode = splitMode === 'horizontal' ? 'vertical' : 'horizontal';
                try { localStorage.setItem('bowire_split_mode', splitMode); } catch { /* ignore */ }
                render();
            }
        }, el('span', {
            innerHTML: svgIcon(splitMode === 'vertical' ? 'splitVertical' : 'splitHorizontal'),
            style: 'width:14px;height:14px;display:flex'
        })));
        if (typeof renderWatchButton === 'function') {
            var wb = renderWatchButton();
            if (wb) right.appendChild(wb);
        }
        right.appendChild(renderConnectionPill());
        bar.appendChild(right);

        return bar;
    }

    // ---- Topbar (brand + command palette + env + theme) ----
    //
    // Three-column grid: [brand | search palette | right controls]. On
    // narrow viewports the grid collapses to two rows (brand + controls
    // on row 1, search on row 2) — handled entirely in CSS.
    //
    // The search input here is the ONLY search in the app now. The old
    // sidebar search, sidebar header, sidebar env selector, and sidebar
    // footer theme toggle all moved into this topbar so the sidebar can
    // focus on service navigation.
    // Brand cluster (B-button + wordmark) used by both the topbar and
    // the app-drawer header — the drawer reuses the exact same markup
    // so when the drawer is open it lines up pixel-for-pixel over the
    // topbar's own brand and the "menu opened" affordance feels like
    // a single moving piece. `withoutId` skips the global id so the
    // drawer copy doesn't collide with the topbar copy in the DOM.
    function renderBrandCluster(opts) {
        opts = opts || {};
        var effectiveTheme = getEffectiveTheme();
        var logoSrc = effectiveTheme === 'dark'
            ? (config.logoIconMono || config.logoIcon)
            : config.logoIcon;
        var logoLayer = logoSrc
            ? el('img', { className: 'bowire-logo-icon', src: logoSrc, alt: '' })
            : el('div', { className: 'bowire-logo-icon', textContent: (config.title || 'B').charAt(0) });
        var burgerLayer = el('span', { className: 'bowire-logo-burger', 'aria-hidden': 'true' },
            el('span', { className: 'bowire-logo-burger-bar' }),
            el('span', { className: 'bowire-logo-burger-bar' }),
            el('span', { className: 'bowire-logo-burger-bar' })
        );
        var isOpen = (typeof appDrawerOpen !== 'undefined' && appDrawerOpen);
        var logoBtn = el('button', {
            type: 'button',
            className: 'bowire-logo-btn' + (isOpen ? ' is-open' : ''),
            title: isOpen ? 'Close menu' : 'Open menu',
            'aria-label': isOpen ? 'Close menu' : 'Open menu',
            'aria-expanded': isOpen ? 'true' : 'false',
            onClick: function () {
                if (typeof appDrawerOpen !== 'undefined') {
                    try { appDrawerOpen = !appDrawerOpen; } catch { /* let-shadow */ }
                }
                if (typeof render === 'function') render();
            }
        }, logoLayer, burgerLayer);
        var logoCol = el('div', { className: 'bowire-topbar-brand-logo-col' }, logoBtn);
        var brandAttrs = { className: 'bowire-topbar-brand' };
        if (!opts.withoutId) brandAttrs.id = 'bowire-topbar-brand';
        return el('div', brandAttrs,
            logoCol,
            el('div', { className: 'bowire-topbar-brand-text' },
                el('div', { className: 'bowire-logo-text', textContent: config.title }),
                config.description
                    ? el('div', { className: 'bowire-subtitle', textContent: config.description })
                    : null
            )
        );
    }

    function renderTopbar() {
        var bar = el('div', { id: 'bowire-topbar', className: 'bowire-topbar' });
        bar.appendChild(renderBrandCluster());

        // --- Command palette (center column) ---
        // #124 v2 — build paletteWrap unconditionally for stash on a
        // module-level slot so the modal-overlay mount in
        // render()'s body assembly can append it without rebuilding.
        var paletteWrap = el('div', { id: 'bowire-topbar-palette', className: 'bowire-topbar-palette' });
        _omniboxPaletteForModal = paletteWrap;

        var searchInputEl = el('input', {
            id: 'bowire-command-palette-input',
            className: 'bowire-command-palette-input',
            type: 'text',
            // Search-palette + autocomplete are meta-UI; opt out of
            // both the autocomplete-on-{{ trigger and the chip
            // overlay so the field stays a plain search box.
            'data-bowire-no-vars-chip': '1',
            'data-bowire-no-vars-ac': '1',
            placeholder: 'Search methods, recordings, mocks, modes\u2026 (Ctrl/Cmd+K)',
            value: searchQuery,
            autocomplete: 'off',
            spellcheck: 'false',
            onFocus: function () {
                // Always pop the dropdown on focus so users see either
                // their live search results OR — when the field is
                // empty — the recently-used methods. Recents act as
                // "quick re-jump" entries; without this we'd silently
                // hide them and the user would have to start typing
                // before realising the palette can do that at all.
                searchSuggestionsOpen = true;
                render();
            },
            onInput: function (e) {
                searchQuery = e.target.value;
                searchSuggestionIndex = 0;
                // Keep the dropdown open even when the field is empty —
                // empty-query falls back to the recents list below.
                searchSuggestionsOpen = true;
                render();
                // Re-focus input after render and restore caret position
                var input = document.getElementById('bowire-command-palette-input');
                if (input) {
                    input.focus();
                    input.selectionStart = input.selectionEnd = input.value.length;
                }
            },
            onKeyDown: function (e) {
                if (searchSuggestionsOpen && searchSuggestionCache.length > 0) {
                    if (e.key === 'ArrowDown') {
                        e.preventDefault();
                        searchSuggestionIndex = (searchSuggestionIndex + 1) % searchSuggestionCache.length;
                        render();
                        // Keep focus in the input so the user can keep
                        // typing while navigating suggestions.
                        var inp = document.getElementById('bowire-command-palette-input');
                        if (inp) inp.focus();
                        return;
                    }
                    if (e.key === 'ArrowUp') {
                        e.preventDefault();
                        searchSuggestionIndex = (searchSuggestionIndex - 1 + searchSuggestionCache.length) % searchSuggestionCache.length;
                        render();
                        var inp2 = document.getElementById('bowire-command-palette-input');
                        if (inp2) inp2.focus();
                        return;
                    }
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        var chosen = searchSuggestionCache[searchSuggestionIndex];
                        if (chosen && typeof chosen.onSelect === 'function') {
                            chosen.onSelect();
                            // Action sets searchSuggestionsOpen = false +
                            // clears searchQuery; the modal won't visibly
                            // close until the next render. Force one so
                            // the palette dismisses immediately after a
                            // pick.
                            render();
                        }
                        return;
                    }
                    if (e.key === 'Tab') {
                        // Close dropdown but don't steal focus
                        searchSuggestionsOpen = false;
                        render();
                        return;
                    }
                }
                if (e.key === 'Escape') {
                    e.preventDefault();
                    if (searchSuggestionsOpen) {
                        searchSuggestionsOpen = false;
                        render();
                    } else if (searchQuery) {
                        searchQuery = '';
                        render();
                    }
                    return;
                }
            }
        });

        var paletteRow = el('div', { className: 'bowire-command-palette-row' },
            el('span', { className: 'bowire-command-palette-icon', innerHTML: svgIcon('search') }),
            searchInputEl,
            el('span', {
                className: 'bowire-command-palette-hint',
                title: 'Focus search',
                textContent: '/'
            })
        );
        paletteWrap.appendChild(paletteRow);

        if (searchSuggestionsOpen) {
            // Empty query → recently-used methods (MRU re-jumps).
            // Non-empty query → live fuzzy match against everything.
            var suggestions = searchQuery.length > 0
                ? buildSearchSuggestions(searchQuery)
                : buildRecentMethodSuggestions();
            searchSuggestionCache = suggestions;
            if (searchSuggestionIndex >= suggestions.length) searchSuggestionIndex = 0;

            if (suggestions.length > 0) {
                var dropdown = el('div', {
                    id: 'bowire-command-palette-dropdown',
                    className: 'bowire-command-palette-dropdown',
                    // stopPropagation so the global outside-click handler
                    // doesn't swallow clicks on the dropdown items
                    onMouseDown: function (e) { e.stopPropagation(); }
                });

                var currentGroup = null;
                for (var si = 0; si < suggestions.length; si++) {
                    (function (s, idx) {
                        if (s.group !== currentGroup) {
                            currentGroup = s.group;
                            dropdown.appendChild(el('div', {
                                className: 'bowire-command-palette-group-header',
                                textContent: s.group
                            }));
                        }
                        var item = el('div', {
                            className: 'bowire-command-palette-item'
                                + (idx === searchSuggestionIndex ? ' selected' : ''),
                            dataset: { idx: String(idx) },
                            onMouseEnter: function () {
                                if (searchSuggestionIndex !== idx) {
                                    searchSuggestionIndex = idx;
                                    // Only update the selection class in place
                                    // to avoid a full render while hovering.
                                    var all = document.querySelectorAll('.bowire-command-palette-item');
                                    for (var i = 0; i < all.length; i++) {
                                        all[i].classList.toggle('selected', all[i].dataset.idx === String(idx));
                                    }
                                }
                            },
                            onClick: function (e) {
                                e.stopPropagation();
                                if (typeof s.onSelect === 'function') {
                                    s.onSelect();
                                    // Same reasoning as the Enter
                                    // handler: action mutates the
                                    // open-flag, render() is needed
                                    // to dismiss the modal.
                                    render();
                                }
                            }
                        });
                        if (s.icon) {
                            item.appendChild(el('span', {
                                className: 'bowire-command-palette-item-icon',
                                innerHTML: s.icon
                            }));
                        }
                        if (s.badge) {
                            item.appendChild(el('span', {
                                className: 'bowire-command-palette-item-badge',
                                dataset: { type: s.badgeType || '' },
                                textContent: s.badge
                            }));
                        }
                        item.appendChild(el('span', {
                            className: 'bowire-command-palette-item-label',
                            textContent: s.label
                        }));
                        if (s.sublabel) {
                            item.appendChild(el('span', {
                                className: 'bowire-command-palette-item-sublabel',
                                textContent: s.sublabel
                            }));
                        }
                        dropdown.appendChild(item);
                    })(suggestions[si], si);
                }
                paletteWrap.appendChild(dropdown);
            } else {
                // Two distinct empty states: no query → "you haven't
                // touched a method yet, type to search"; with query →
                // "I tried to match it and found nothing".
                var emptyText = searchQuery.length > 0
                    ? ('No matches for "' + searchQuery + '"')
                    : 'No recently-used methods yet \u2014 start typing to search';
                var emptyDropdown = el('div', {
                    id: 'bowire-command-palette-dropdown',
                    className: 'bowire-command-palette-dropdown empty'
                },
                    el('div', { className: 'bowire-command-palette-empty',
                        textContent: emptyText })
                );
                paletteWrap.appendChild(emptyDropdown);
            }
        } else {
            searchSuggestionCache = [];
        }

        // #124 v2 — compact magnifier button. Built here so it can
        // be reused on the right cluster below. Click or
        // Cmd/Ctrl+K opens the modal overlay.
        var omniboxTriggerBtn = el('button', {
            id: 'bowire-omnibox-trigger',
            type: 'button',
            className: 'bowire-theme-toggle-btn bowire-omnibox-trigger-btn',
            title: 'Search methods, recordings, modes… (Ctrl/Cmd+K)',
            'aria-label': 'Open command palette',
            onClick: function () {
                searchSuggestionsOpen = true;
                searchSuggestionIndex = 0;
                render();
                requestAnimationFrame(function () {
                    var input = document.getElementById('bowire-command-palette-input');
                    if (input) { input.focus(); input.select(); }
                });
            }
        }, el('span', {
            innerHTML: svgIcon('search'),
            style: 'width:16px;height:16px;display:flex'
        }));
        // Centre column stays empty — magnifier moves to the right
        // cluster where it groups naturally with Assistant + ⋮.
        bar.appendChild(el('div', { className: 'bowire-topbar-palette-spacer' }));
        // The actual paletteWrap (with input + suggestions) renders
        // INSIDE the modal overlay — see renderOmniboxModal below.
        // We still build it here to capture all the existing
        // suggestion + keyboard logic in one place.

        // Schema Watch toggle moved to the statusbar in #138 along
        // with the connection pill + env selector. Local definition
        // dropped from the topbar.

        // About button — pops the standalone About dialog (version,
        // open-source notices, Küstenlogik credit). 'info' glyph (i in
        // a circle) so it reads as 'what is this thing' and stays
        // distinct from the 'help' glyph (? in a circle) on the help
        // button right next to it. Both icons live in the topbar
        // directly now — overflow menu retired.
        var aboutBtn = el('button', {
            id: 'bowire-about-btn',
            className: 'bowire-theme-toggle-btn bowire-about-btn',
            title: 'About Bowire',
            'aria-label': 'About Bowire',
            // #297 — priority tag for the responsive horizontal overflow
            // pass. Lower priority = collapses first. data-topbar-label
            // feeds the ⋮ menu rows + the aria-label "More: …" summary.
            // data-topbar-icon names the svgIcon entry the popover
            // re-renders into its row.
            'data-topbar-priority': '5',
            'data-topbar-label': 'About',
            'data-topbar-icon': 'info',
            'data-topbar-group': 'info',
            onClick: openAbout
        }, el('span', {
            innerHTML: svgIcon('info'),
            // 14×14 matches the rest of the topbar's icon size — the
            // legacy 16×16 made the info / help pair read as visually
            // heavier than the neighbouring chips + buttons. Operator
            // feedback: 'das info/about-symbol passt von der größe her
            // nicht in die top bar'.
            style: 'width:14px;height:14px;display:flex'
        }));

        // Help button — opens the in-app docs drawer (F1). Greyed out
        // with an install hint when the Kuestenlogik.Bowire.Help
        // package isn't installed (capability probe at boot returned
        // available:false); click in that branch falls back to
        // opening bowire.io/docs externally.
        var helpBtn = el('button', {
            id: 'bowire-help-btn',
            className: 'bowire-theme-toggle-btn'
                + (helpAvailable && helpDrawerOpen ? ' active' : '')
                + (helpAvailable ? '' : ' bowire-theme-toggle-btn-disabled'),
            title: helpAvailable
                ? (helpDrawerOpen ? 'Close Help (F1)' : 'Help (F1)')
                : 'Install Kuestenlogik.Bowire.Help for in-app docs (or visit bowire.io/docs)',
            'aria-label': 'Help',
            // #297 — overflow priority tag (see aboutBtn).
            'data-topbar-priority': '5',
            'data-topbar-label': 'Help',
            'data-topbar-icon': 'help',
            'data-topbar-group': 'info',
            onClick: function () {
                if (helpAvailable) {
                    // #299 — toggle: closing on second click matches the
                    // Assistant button next to it and keeps F1 as a
                    // dedicated 'force open' shortcut (still calls
                    // helpOpenDrawer regardless of state).
                    if (helpDrawerOpen) {
                        helpCloseDrawer();
                    } else {
                        helpOpenDrawer();
                    }
                } else {
                    window.open('https://bowire.io/docs', '_blank', 'noopener');
                }
            }
        }, el('span', {
            innerHTML: svgIcon('help'),
            style: 'width:14px;height:14px;display:flex'
        }));

        // Settings button — wrapped so the plugin-update badge (when
        // opted-in + the cached snapshot reports pending updates) can
        // position itself relative to the gear without needing a
        // dedicated topbar slot of its own.
        var settingsBtn = el('button', {
            id: 'bowire-settings-btn',
            className: 'bowire-theme-toggle-btn',
            title: 'Settings',
            'aria-label': 'Settings',
            onClick: openSettings
        }, el('span', {
            innerHTML: svgIcon('settings'),
            style: 'width:16px;height:16px;display:flex'
        }));
        var updateBadge = typeof renderPluginUpdateBadge === 'function'
            ? renderPluginUpdateBadge() : null;
        if (updateBadge) {
            settingsBtn = el('div', {
                className: 'bowire-settings-btn-wrapper',
            }, settingsBtn, updateBadge);
        }

        // AI drawer toggle (#90) — promoted from the response-pane tab
        // strip to a topbar control so the assistant is reachable from
        // anywhere in the workbench, not just when looking at a method
        // response. Badge carries the live hint count from the hint
        // engine, same number the old tab badge showed.
        //
        // #310 — module gate. When Settings → Modules has the AI module
        // turned off (or the package isn't installed), the button stays
        // out of the topbar entirely + the hint count short-circuits to
        // zero so we don't spin the hint engine for a hidden surface.
        var aiModuleOn = (typeof isModuleEnabled === 'function')
            ? isModuleEnabled('ai')
            : !!window.__bowireAi;
        var aiHintCount = 0;
        try {
            aiHintCount = (aiModuleOn && window.__bowireAi)
                ? window.__bowireAi.hintCount() : 0;
        } catch { /* ignore */ }
        var aiToggleBtn = el('button', {
            id: 'bowire-ai-drawer-toggle',
            // Reuse the .bowire-theme-toggle-btn baseline so the AI
            // button matches the env / watch / theme / about / settings
            // chrome (34px height, bordered pill, transparent bg, hover).
            // The .bowire-ai-drawer-toggle modifier adds the active-state
            // highlight + anchors the hint-count badge. Originally used
            // a made-up .bowire-topbar-icon-btn that had no rules — the
            // button collapsed to a thin sliver without an icon.
            className: 'bowire-theme-toggle-btn bowire-ai-drawer-toggle' + (aiDrawerOpen ? ' active' : ''),
            title: aiDrawerOpen ? 'Close Assistant (Ctrl+Shift+A)' : 'Open Assistant (Ctrl+Shift+A)',
            'aria-label': 'Toggle Assistant',
            // #297 — overflow priority tag.
            'data-topbar-priority': '3',
            'data-topbar-label': 'Assistant',
            'data-topbar-icon': 'bot',
            'data-topbar-group': 'ai',
            onClick: function () {
                aiDrawerOpen = !aiDrawerOpen;
                try { localStorage.setItem('bowire_ai_drawer_open', aiDrawerOpen ? '1' : '0'); } catch { /* ignore */ }
                // #299 — opening the panel should also make it the
                // active tab in the unified right drawer.
                if (aiDrawerOpen) {
                    rightDrawerActiveTab = 'assistant';
                    try { localStorage.setItem('bowire_right_drawer_active_tab', 'assistant'); } catch { /* ignore */ }
                }
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon('bot'),
                style: 'width:16px;height:16px;display:flex',
            }),
            aiHintCount > 0
                ? el('span', { className: 'bowire-ai-drawer-badge', textContent: String(aiHintCount) })
                : null
        );
        // #310 — module-off → suppress the button. The Settings → Modules
        // panel stays the place to bring it back. We close the drawer
        // too so a switch-off-while-open doesn't leave the drawer pane
        // visible without a way to close it.
        if (!aiModuleOn) {
            aiToggleBtn = null;
            if (aiDrawerOpen) {
                aiDrawerOpen = false;
                try { localStorage.setItem('bowire_ai_drawer_open', '0'); } catch { /* ignore */ }
            }
        }

        // Security drawer toggle (#111). Sits next to the AI drawer
        // toggle. Shield icon signals "scanner / analysis tools" —
        // distinct from the assistant's bot icon. Drawer hosts the
        // Threat-Model surface that used to live inside the AI drawer.
        var securityToggleBtn = el('button', {
            id: 'bowire-security-drawer-toggle',
            className: 'bowire-theme-toggle-btn bowire-security-drawer-toggle' + (securityDrawerOpen ? ' active' : ''),
            title: securityDrawerOpen ? 'Close Security drawer' : 'Open Security drawer',
            'aria-label': 'Toggle Security drawer',
            onClick: function () {
                securityDrawerOpen = !securityDrawerOpen;
                try { localStorage.setItem('bowire_security_drawer_open', securityDrawerOpen ? '1' : '0'); } catch { /* ignore */ }
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon('shield'),
                style: 'width:16px;height:16px;display:flex',
            })
        );

        // #292 — Theme / Help / About promoted out of the ⋮ overflow
        // into direct topbar buttons. Theme is a three-state cycle
        // (auto → dark → light → auto), rendered by the existing
        // renderThemeToggle helper that picks the right icon for the
        // current state.
        var themeBtn = (typeof renderThemeToggle === 'function') ? renderThemeToggle() : null;

        // #296 — Topbar history group: Undo + Redo + Trash. Visible,
        // discoverable equivalents of the existing Ctrl/Cmd+Z + Shift+Z
        // shortcuts plus the aggregated Trash drawer that surfaces
        // every soft-deleted bucket in the active workspace. The
        // buttons reuse the existing undoLastAction / redoLastAction
        // helpers from prologue.js so there's no parallel state
        // machine.
        var _undoLabel = (typeof nextUndoLabel === 'function') ? nextUndoLabel() : null;
        var _redoLabel = (typeof nextRedoLabel === 'function') ? nextRedoLabel() : null;
        var _undoDisabled = !_undoLabel;
        var _redoDisabled = !_redoLabel;
        var undoBtn = el('button', {
            id: 'bowire-topbar-undo-btn',
            type: 'button',
            className: 'bowire-theme-toggle-btn bowire-topbar-history-btn'
                + (_undoDisabled ? ' bowire-theme-toggle-btn-disabled' : ''),
            disabled: _undoDisabled ? 'disabled' : null,
            'aria-disabled': _undoDisabled ? 'true' : 'false',
            title: _undoDisabled
                ? 'Nothing to undo (Ctrl/Cmd+Z)'
                : ('Undo: ' + _undoLabel + ' (Ctrl/Cmd+Z)'),
            'aria-label': _undoDisabled ? 'Undo (nothing to undo)' : ('Undo: ' + _undoLabel),
            // #297 — overflow priority tag. Undo + Redo are the last
            // collapsible buttons because they're the most discoverable
            // editor-context affordances; Trash is rarer.
            'data-topbar-priority': '1',
            'data-topbar-label': 'Undo',
            'data-topbar-icon': 'undo',
            'data-topbar-group': 'history',
            onClick: function () {
                if (_undoDisabled) return;
                if (typeof undoLastAction === 'function') {
                    var u = undoLastAction();
                    if (u && typeof toast === 'function') toast('Undone: ' + u.title, 'info');
                    render();
                }
            }
        }, el('span', {
            innerHTML: svgIcon('undo'),
            style: 'width:16px;height:16px;display:flex'
        }));
        var redoBtn = el('button', {
            id: 'bowire-topbar-redo-btn',
            type: 'button',
            className: 'bowire-theme-toggle-btn bowire-topbar-history-btn'
                + (_redoDisabled ? ' bowire-theme-toggle-btn-disabled' : ''),
            disabled: _redoDisabled ? 'disabled' : null,
            'aria-disabled': _redoDisabled ? 'true' : 'false',
            title: _redoDisabled
                ? 'Nothing to redo (Ctrl/Cmd+Shift+Z)'
                : ('Redo: ' + _redoLabel + ' (Ctrl/Cmd+Shift+Z)'),
            'aria-label': _redoDisabled ? 'Redo (nothing to redo)' : ('Redo: ' + _redoLabel),
            // #297 — overflow priority tag.
            'data-topbar-priority': '1',
            'data-topbar-label': 'Redo',
            'data-topbar-icon': 'redo',
            'data-topbar-group': 'history',
            onClick: function () {
                if (_redoDisabled) return;
                if (typeof redoLastAction === 'function') {
                    var r = redoLastAction();
                    if (r && typeof toast === 'function') toast('Redone: ' + r.title, 'info');
                    render();
                }
            }
        }, el('span', {
            innerHTML: svgIcon('redo'),
            style: 'width:16px;height:16px;display:flex'
        }));
        // Aggregate trash count across every bucket in the active
        // workspace. Workspaces section is a placeholder until #194
        // soft-delete lands; it always contributes 0 today.
        var _trashRec = (typeof recordingsTrash !== 'undefined' && Array.isArray(recordingsTrash)) ? recordingsTrash.length : 0;
        var _trashCol = (typeof collectionsTrash !== 'undefined' && Array.isArray(collectionsTrash)) ? collectionsTrash.length : 0;
        var _trashTabs = (typeof tabsTrash !== 'undefined' && Array.isArray(tabsTrash)) ? tabsTrash.length : 0;
        var _trashTotal = _trashRec + _trashCol + _trashTabs;
        var trashBtn = el('button', {
            id: 'bowire-topbar-trash-btn',
            type: 'button',
            className: 'bowire-theme-toggle-btn bowire-topbar-history-btn'
                + (globalTrashOpen ? ' active' : ''),
            title: globalTrashOpen
                ? 'Close trash drawer'
                : ('Trash — ' + _trashTotal + ' item' + (_trashTotal === 1 ? '' : 's')
                    + ' across recordings / collections / closed tabs'),
            'aria-label': 'Toggle trash drawer',
            // #297 — overflow priority tag. Trash collapses before
            // Undo/Redo (rarer affordance, less muscle-memory cost).
            'data-topbar-priority': '2',
            'data-topbar-label': 'Trash',
            'data-topbar-group': 'history',
            'data-topbar-icon': 'trash',
            onClick: function () {
                globalTrashOpen = !globalTrashOpen;
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon('trash'),
                style: 'width:16px;height:16px;display:flex'
            }),
            _trashTotal > 0
                ? el('span', {
                    className: 'bowire-topbar-trash-badge',
                    textContent: String(_trashTotal)
                })
                : null
        );

        // #116 Workspaces Phase 1 — workspace switcher chip.
        // Click opens a small menu with every workspace + a
        // 'New workspace…' action.
        var ws = activeWorkspace();
        // Chip is always a dropdown trigger — even with zero workspaces
        // the operator clicks the chip and sees a menu. The menu carries
        // the "+ New workspace…" action so the empty state is one click
        // (chip) → one click (item) instead of a direct dialog. Keeps
        // the affordance discoverable + uniform across the two states.
        var wsChip = el('button', {
            id: 'bowire-workspace-chip',
            type: 'button',
            className: 'bowire-workspace-chip' + (workspaceMenuOpen ? ' active' : ''),
            title: ws ? ('Workspace: ' + ws.name) : 'No workspace — click to create one',
            'aria-label': ws ? 'Switch workspace' : 'New workspace',
            onClick: function (e) {
                e.stopPropagation();
                // Close any other topbar dropdown before toggling
                // ours. The env dropdown is mounted imperatively
                // (envBtnWrapper.appendChild(menu) + a setTimeout
                // outside-click handler) instead of via the render-
                // state path the workspace menu uses, so a click
                // here doesn't bubble into its outside-handler — we
                // have to remove the menu node ourselves. Without
                // this the env dropdown stayed open in the
                // background when the user clicked the workspace
                // chip and vice versa.
                var openEnvMenu = document.querySelector('.bowire-env-dropdown-menu');
                if (openEnvMenu) openEnvMenu.remove();
                workspaceMenuOpen = !workspaceMenuOpen;
                render();
            }
        },
            (function () {
                // Same Lucide 'layers' glyph as the dropdown rows — the
                // top "leaf" picks up the active workspace's chosen
                // colour, the lower two layers inherit currentColor.
                // When there's no active workspace, the top layer falls
                // back to tertiary so the chip reads as "empty / pick".
                var chipColor = ws ? (ws.color || 'var(--bowire-accent)') : 'var(--bowire-text-tertiary)';
                var chipGlyph = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="14" height="14">'
                    + '<polygon points="12 2 2 7 12 12 22 7 12 2" fill="' + chipColor + '" stroke="' + chipColor + '"/>'
                    + '<polyline points="2 17 12 22 22 17"/>'
                    + '<polyline points="2 12 12 17 22 12"/>'
                    + '</svg>';
                return el('span', { className: 'bowire-workspace-chip-dot', innerHTML: chipGlyph });
            })(),
            el('span', {
                className: 'bowire-workspace-chip-name',
                textContent: ws ? ws.name : 'No workspace'
            }),
            el('span', { className: 'bowire-workspace-chip-caret', innerHTML: svgIcon('chevronDown') })
        );

        // #297 — Responsive horizontal overflow. ⋮ button is always
        // rendered; CSS hides it unless _layoutTopbarRight() flags the
        // cluster as overflowing (adds .has-overflow). The popover
        // below is rendered only when both the open-flag is set AND
        // the layout pass actually hid something. Catalogue order
        // mirrors the rail-overflow pattern (#164 v3) so the popover
        // can be rebuilt deterministically from data-topbar-priority
        // attributes after every render.
        var hiddenItems = (_topbarRightOverflowHidden || []).slice();
        var topbarRightOverflowBtn = el('button', {
            type: 'button',
            id: 'bowire-topbar-right-overflow-btn',
            className: 'bowire-theme-toggle-btn bowire-topbar-right-overflow-btn'
                + (topbarRightOverflowOpen ? ' active' : ''),
            title: hiddenItems.length > 0
                ? ('More: ' + hiddenItems.map(function (h) { return h.label; }).join(', '))
                : 'More',
            'aria-label': hiddenItems.length > 0
                ? ('More: ' + hiddenItems.map(function (h) { return h.label; }).join(', '))
                : 'More',
            'aria-haspopup': 'menu',
            'aria-expanded': topbarRightOverflowOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                topbarRightOverflowOpen = !topbarRightOverflowOpen;
                render();
            }
        }, el('span', {
            innerHTML: svgIcon('dots'),
            style: 'width:16px;height:16px;display:flex'
        }));

        // Popover with the hidden items. Re-fires each hidden button's
        // click handler via id lookup so the menu rows behave EXACTLY
        // like the inline buttons would (disabled state, drawer toggle,
        // theme cycle…). No parallel onClick logic to drift.
        var topbarRightOverflowPopover = null;
        if (topbarRightOverflowOpen && hiddenItems.length > 0) {
            topbarRightOverflowPopover = el('div', {
                id: 'bowire-topbar-right-overflow-popover',
                className: 'bowire-topbar-right-overflow-popover',
                role: 'menu',
                onClick: function (e) { e.stopPropagation(); }
            });
            // Mirror the rail-strip's group dividers inside the topbar
            // overflow popover so semantic groups (history, ai,
            // appearance, info) stay visible when items hide.
            var _topbarPopoverLastGroup = null;
            hiddenItems.forEach(function (h) {
                if (_topbarPopoverLastGroup !== null && h.group !== _topbarPopoverLastGroup) {
                    topbarRightOverflowPopover.appendChild(el('div', {
                        className: 'bowire-topbar-right-overflow-divider',
                        role: 'separator'
                    }));
                }
                _topbarPopoverLastGroup = h.group;
                topbarRightOverflowPopover.appendChild(el('button', {
                    type: 'button',
                    role: 'menuitem',
                    className: 'bowire-topbar-right-overflow-item',
                    disabled: h.disabled ? 'disabled' : null,
                    onClick: function (e) {
                        e.stopPropagation();
                        topbarRightOverflowOpen = false;
                        // Resolve the source button at click time —
                        // morphdom may have replaced the original node
                        // between popover open and click (Memory:
                        // morphdom-stale-handler-pitfall). Click the
                        // live element so its onClick handler runs in
                        // whatever closure the latest render gave it.
                        var live = document.getElementById(h.id);
                        if (live && !live.disabled) live.click();
                        else render();
                    }
                },
                    el('span', {
                        className: 'bowire-topbar-right-overflow-item-icon',
                        innerHTML: svgIcon(h.icon || 'dots')
                    }),
                    el('span', {
                        className: 'bowire-topbar-right-overflow-item-label',
                        textContent: h.label
                    })
                ));
            });
        }

        var right = el('div', { id: 'bowire-topbar-right', className: 'bowire-topbar-right' },
            // Search trigger leads the right cluster (operator
            // feedback: 'search tool sollte noch vor die auswahl des
            // workspaces kommen'). Click or Cmd/Ctrl+K opens the
            // omnibox modal. Hosted as a bare button — its own group
            // with no neighbours — so it reads as the primary
            // discovery affordance ahead of the editor-context group.
            el('div', { className: 'bowire-topbar-group bowire-topbar-search' },
                omniboxTriggerBtn
            ),
            // #116 workspace + #138 env selector form the editor-
            // context group — both describe 'what state am I
            // editing right now'.
            el('div', { className: 'bowire-topbar-group bowire-topbar-context' },
                wsChip,
                workspaceMenuOpen ? el('div', {
                    className: 'bowire-workspace-menu',
                    onClick: function (e) { e.stopPropagation(); }
                },
                    el('div', { className: 'bowire-workspace-menu-section' },
                        // Cross-cutting workspace sort applies in the
                        // dropdown too (same workspacesSortBy state as
                        // the sidebar + overview). No search filter
                        // here — the dropdown is a quick-pick surface,
                        // not a full search UI.
                        (typeof getSortedWorkspaces === 'function'
                            ? getSortedWorkspaces()
                            : workspaces).map(function (w) {
                            var isActive = w.id === activeWorkspaceId;
                            // Per-row leading glyph: the Workspaces rail icon
                            // (Lucide 'layers') with the top "leaf" filled in
                            // the workspace's chosen colour and the lower two
                            // layers inheriting currentColor. Reads as "this
                            // is a workspace, identified by its colour" — more
                            // contextual than a plain coloured dot, and the
                            // top-layer-only colouring keeps the layered
                            // metaphor recognisable instead of flooding the
                            // whole glyph with the accent.
                            var wsColor = w.color || 'var(--bowire-accent)';
                            var glyph = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="14" height="14">'
                                + '<polygon points="12 2 2 7 12 12 22 7 12 2" fill="' + wsColor + '" stroke="' + wsColor + '"/>'
                                + '<polyline points="2 17 12 22 22 17"/>'
                                + '<polyline points="2 12 12 17 22 12"/>'
                                + '</svg>';
                            // #276 — Topbar dropdown trimmed to quick-
                            // access (Postman / VS Code convention):
                            // the rows only carry the active-state ✓
                            // marker now. Rename / Edit / Delete moved
                            // to the Workspaces rail sidebar so the
                            // topbar dropdown reads as a fast pick
                            // surface instead of a management
                            // panel.
                            // Per-row tools: active-state ✓ marker +
                            // hover-revealed Rename / Save-as-template /
                            // Delete. User feedback after #276: the
                            // dropdown is the quick-access surface AND
                            // a practical reach for management when
                            // the sidebar is collapsed — full parity
                            // with sidebar + overview was the right
                            // call, not the trim. Action handlers
                            // delegate to _workspaceRowActionDefs if
                            // present so the three surfaces (sidebar /
                            // overview / dropdown) share one source of
                            // truth.
                            // Def shape from _workspaceRowActionDefs:
                            // { key, icon, label, title, onClick }. The
                            // dropdown surfaces a CURATED quick-access
                            // subset (not every action) — Settings +
                            // Rename + Delete. Save-as-template +
                            // Duplicate are rarer operations that live
                            // on the sidebar + overview, where there's
                            // room to carry the full set without
                            // crowding the quick-pick surface.
                            var defs = (typeof _workspaceRowActionDefs === 'function')
                                ? _workspaceRowActionDefs(w) : null;
                            var settingsDef = defs && defs.find(function (d) { return d.key === 'settings'; });
                            var renameDef = defs && defs.find(function (d) { return d.key === 'rename'; });
                            var deleteDef = defs && defs.find(function (d) { return d.key === 'delete'; });
                            function _toolBtn(def, danger) {
                                if (!def) return null;
                                var classes = 'bowire-workspace-menu-item-tool'
                                    + (danger ? ' bowire-workspace-menu-item-tool-danger' : '');
                                return el('button', {
                                    type: 'button',
                                    className: classes,
                                    title: def.title || def.label,
                                    'aria-label': def.label,
                                    innerHTML: (typeof svgIcon === 'function') ? svgIcon(def.icon) : def.icon,
                                    onClick: function (e) {
                                        e.stopPropagation();
                                        workspaceMenuOpen = false;
                                        if (typeof def.onClick === 'function') def.onClick();
                                    }
                                });
                            }
                            return el('div', {
                                className: 'bowire-workspace-menu-item' + (isActive ? ' active' : ''),
                                onClick: function () {
                                    if (!isActive) switchWorkspace(w.id);
                                    workspaceMenuOpen = false;
                                    render();
                                }
                            },
                                el('span', { className: 'bowire-workspace-menu-item-glyph', innerHTML: glyph }),
                                el('span', { className: 'bowire-workspace-menu-item-name', textContent: w.name }),
                                el('div', { className: 'bowire-workspace-menu-item-tools' },
                                    // Layout matches the sidebar
                                    // (master surface for the row
                                    // shape): hover tools first
                                    // (Settings / Rename / Delete),
                                    // then the active-indicator at
                                    // the END. Active = filled
                                    // accent-color checkmark, always
                                    // visible. Inactive = hover-
                                    // revealed click-to-activate
                                    // button, tool-color. Both share
                                    // the same rightmost slot
                                    // (fixed-width 18×18).
                                    _toolBtn(settingsDef, false),
                                    _toolBtn(renameDef, false),
                                    _toolBtn(deleteDef, true),
                                    isActive
                                        ? el('span', {
                                            className: 'bowire-workspace-menu-item-check is-active',
                                            innerHTML: (typeof svgIcon === 'function') ? svgIcon('check') : '✓',
                                            'aria-hidden': 'false',
                                            title: 'Active workspace'
                                        })
                                        : el('button', {
                                            type: 'button',
                                            className: 'bowire-workspace-menu-item-check bowire-workspace-menu-item-activate',
                                            innerHTML: (typeof svgIcon === 'function') ? svgIcon('check') : '✓',
                                            title: 'Switch to this workspace',
                                            'aria-label': 'Switch to this workspace',
                                            onClick: function (e) {
                                                e.stopPropagation();
                                                switchWorkspace(w.id);
                                                workspaceMenuOpen = false;
                                                render();
                                            }
                                        })
                                )
                            );
                        })
                    ),
                    workspaces.length > 0 ? el('div', { className: 'bowire-workspace-menu-divider' }) : null,
                    el('div', {
                        className: 'bowire-workspace-menu-item bowire-workspace-menu-item-action',
                        onClick: function () {
                            workspaceMenuOpen = false;
                            render();
                            openCreateWorkspaceDialog();
                        }
                    },
                        el('span', { className: 'bowire-workspace-menu-item-icon', textContent: '+' }),
                        el('span', { textContent: 'New workspace…' })
                    ),
                    // Secondary entry — leads to the full Workspaces
                    // overview. The dropdown carries the quick-access
                    // actions (switch / + new), the overview is the
                    // place to do the heavier management (rename /
                    // duplicate / save-as-template / delete in a list
                    // surface). Both no-workspace + has-workspaces
                    // states get this entry so the welcome state can
                    // funnel through it too (the empty overview
                    // itself guides toward the first create).
                    el('div', {
                        className: 'bowire-workspace-menu-item bowire-workspace-menu-item-action',
                        onClick: function () {
                            workspaceMenuOpen = false;
                            railMode = 'workspaces';
                            try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                            workspaceTreeSelection = { kind: 'workspaces-overview' };
                            render();
                        }
                    },
                        el('span', { className: 'bowire-workspace-menu-item-icon',
                            innerHTML: (typeof svgIcon === 'function') ? svgIcon('list') : '☰' }),
                        el('span', { textContent: 'Manage workspaces' })
                    )
                    // Bottom "Rename current…" / "Edit current…" / "Delete
                    // current" items retired — per-row pencil / gear / trash
                    // cover every workspace including the active one, so a
                    // duplicate bottom block would just be noise.
                ) : null,
                renderEnvSelector(),
            ),
            // #296 — history group: Undo / Redo / Trash. Sits between
            // the editor-context group (workspace + env) and the
            // drawers group so the affordances read as "global
            // history affordances" — clearly tied to the action log
            // foundation (#168) and the cross-rail trash buckets
            // rather than the editor's current state.
            el('div', { className: 'bowire-topbar-group bowire-topbar-history' },
                undoBtn,
                redoBtn,
                trashBtn
            ),
            // Drawer + utility group. Security toggle retired in
            // #133 Phase 2 (Security is a rail mode now, not a
            // drawer). Assistant stays as a drawer because it's
            // cross-cutting. Theme / Help / About promoted out of
            // the old ⋮ overflow (#292) — Settings stays at the
            // activity-rail bottom per VS Code / JetBrains convention.
            // (Omnibox search trigger moved to its own group AHEAD
            // of the editor-context group — leads the right cluster.)
            el('div', { className: 'bowire-topbar-group bowire-topbar-drawers' },
                aiToggleBtn,
                themeBtn,
                helpBtn,
                aboutBtn
            ),
            // #297 — overflow ⋮ button sits in its own trailing group so
            // the divider-between-groups CSS rule reads it as a peer
            // cluster instead of crowding the Drawers separator. The
            // popover is appended OUTSIDE the group so the group's
            // overflow:visible isn't required — popover is absolutely
            // positioned relative to bowire-topbar-right.
            el('div', {
                className: 'bowire-topbar-group bowire-topbar-right-overflow-group',
                style: 'display:none' // unhidden by _layoutTopbarRight when needed
            }, topbarRightOverflowBtn),
            topbarRightOverflowPopover
        );
        bar.appendChild(right);

        return bar;
    }

    // Theme toggle — three-state cycle (auto → dark → light → auto).
    //
    // CRITICAL: the onClick handler reads themePreference FRESH each
    // time it fires instead of closing over a captured value. morphdom
    // preserves DOM nodes across renders, which means it also preserves
    // the original click listeners — a captured `currentTheme` from the
    // first render would stay stuck at "dark" and every click would
    // compute `next = "light"`, so light → dark would silently no-op.
    // Reading the state at click time works regardless of how many
    // times the DOM has been re-diffed underneath us.
    // #129 — shared theme cycle so the overflow menu (and any future
    // caller) can advance the theme without rendering the visible
    // toggle button. Same auto → dark → light → auto rotation as the
    // toggle's onClick.
    function cycleTheme() {
        var cur = themePreference;
        var next = cur === 'auto' ? 'dark'
                 : cur === 'dark' ? 'light'
                 : 'auto';
        setThemePreference(next);
    }

    function renderThemeToggle() {
        var pref = themePreference;
        var iconName;
        var nextLabel;
        // State-pattern: icon shows the CURRENT theme, not the action
        // that a click would perform. Matches what GitHub / Apple /
        // VS Code / MudBlazor do — the Tooltip communicates the action
        // (see title attribute below). Action-pattern (sun while dark)
        // was confusing because the visible symbol didn't match the
        // actual state.
        if (pref === 'auto') {
            iconName = 'themeAuto';
            nextLabel = 'dark';
        } else if (pref === 'dark') {
            iconName = 'moon';  // we're in dark mode → show moon
            nextLabel = 'light';
        } else {
            iconName = 'sun';   // we're in light mode → show sun
            nextLabel = 'auto';
        }

        // Icon-only button \u2014 the topbar cluster is dense (env, watch,
        // theme, AI, about, settings). The visible label was redundant
        // with the icon's state-pattern, so dropped. The full state +
        // next-action is in the tooltip; the accessible name is in
        // aria-label.
        return el('button', {
            id: 'bowire-theme-toggle-btn',
            className: 'bowire-theme-toggle-btn' + (pref === 'auto' ? ' is-auto' : ''),
            title: 'Theme: ' + pref + ' \u2014 click for ' + nextLabel + ' (T)',
            'aria-label': 'Theme: ' + pref,
            // #297 \u2014 overflow priority tag. data-topbar-icon mirrors
            // the icon picked above so the popover row shows the same
            // glyph as the inline button.
            'data-topbar-priority': '4',
            'data-topbar-label': 'Theme',
            'data-topbar-group': 'appearance',
            'data-topbar-icon': iconName,
            onClick: function () {
                var cur = themePreference;
                var next = cur === 'auto' ? 'dark'
                         : cur === 'dark' ? 'light'
                         : 'auto';
                setThemePreference(next);
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon(iconName),
                style: 'width:16px;height:16px;display:flex'
            })
        );
    }

    // Empty-query suggestions: the methods the user touched most
    // recently, in MRU order. Filtered to only those still present in
    // the live `services` set so a stale recent doesn't dead-end the
    // user with an unselectable row. The onSelect mirrors the live
    // search variant exactly so picking a recent feels identical.
    function buildRecentMethodSuggestions() {
        var recents = getRecentMethods();
        if (!recents.length) return [];
        var out = [];
        for (var i = 0; i < recents.length; i++) {
            var r = recents[i];
            var svc = null, method = null;
            for (var si = 0; si < services.length && !method; si++) {
                if (services[si].name !== r.service) continue;
                svc = services[si];
                if (!svc.methods) break;
                for (var mi = 0; mi < svc.methods.length; mi++) {
                    if (svc.methods[mi].name === r.method) {
                        method = svc.methods[mi];
                        break;
                    }
                }
            }
            if (!svc || !method) continue;
            (function (mm, ms) {
                var proto = protocols.find(function (p) { return p.id === ms.source; });
                out.push({
                    group: 'Recently used',
                    label: mm.name,
                    sublabel: ms.name,
                    icon: proto ? proto.icon : null,
                    badge: mm.methodType === 'Unary' ? null : (mm.methodType || ''),
                    badgeType: (mm.methodType || '').toLowerCase(),
                    onSelect: function () {
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        openTab(ms, mm);
                    }
                });
            })(method, svc);
        }
        return out;
    }

    // #124 Phase 2 — scoring layer for the method-match ranking.
    // Returns 0 = no hit, higher = better. Layers as the issue body
    // enumerates:
    //   1000  exact name match (case-insensitive)
    //    500  name prefix match
    //    200  camelCase / snake / kebab boundary match in name
    //    100  plain substring in name
    //     50  substring elsewhere (fullName / service / description /
    //         summary / httpPath)
    // Plus boosts on top of the base score:
    //     +75 method is in the recently-used list (MRU bonus)
    //     +25 same-protocol bonus when a protocol filter is active
    // Workspace-affinity is implicit because services[] is already
    // workspace-scoped — no extra bonus needed today.
    function _scoreMethodMatch(method, svc, qLower) {
        var name = (method.name || '').toLowerCase();
        var base = 0;
        if (name === qLower) base = 1000;
        else if (name.indexOf(qLower) === 0) base = 500;
        else {
            // camelCase / snake / kebab boundary: query matches the start
            // of a name segment (after upper-case, _, - or /).
            var boundaryHit = false;
            var raw = method.name || '';
            for (var i = 0; i < raw.length - qLower.length + 1; i++) {
                var prevChar = i === 0 ? '_' : raw.charAt(i - 1);
                var atBoundary = i === 0
                    || prevChar === '_' || prevChar === '-' || prevChar === '/'
                    || (prevChar === prevChar.toLowerCase() && raw.charAt(i) !== raw.charAt(i).toLowerCase());
                if (atBoundary && raw.substring(i, i + qLower.length).toLowerCase() === qLower) {
                    boundaryHit = true;
                    break;
                }
            }
            if (boundaryHit) base = 200;
            else if (name.indexOf(qLower) !== -1) base = 100;
            else {
                var extras = (
                    (method.fullName || '') + ' ' +
                    (svc.name || '') + ' ' +
                    (method.description || '') + ' ' +
                    (method.summary || '') + ' ' +
                    (method.httpPath || '')
                ).toLowerCase();
                if (extras.indexOf(qLower) !== -1) base = 50;
            }
        }
        if (base === 0) return 0;
        // MRU bonus.
        try {
            var recents = (typeof getRecentMethods === 'function') ? getRecentMethods() : [];
            for (var r = 0; r < recents.length; r++) {
                if (recents[r].service === svc.name && recents[r].method === method.name) {
                    base += 75;
                    break;
                }
            }
        } catch { /* recents may not be loaded yet */ }
        // Protocol-filter affinity.
        try {
            if (typeof protocolFilter !== 'undefined' && protocolFilter.size > 0
                && protocolFilter.has(svc.source)) {
                base += 25;
            }
        } catch { /* ignore */ }
        // #156 — favorite bonus. Methods the operator has explicitly
        // pinned rise above tied matches; the dropdown also renders
        // a star glyph on the row so the user sees why it's at the
        // top. Same +50 weight as a substring-elsewhere hit, so a
        // favorite that matches by name beats an unfavorite that
        // matches by name (both have base 100), but a favorite
        // matched only by description (base 50) still loses to an
        // unfavorite matched by name (base 100). Intent: nudge, not
        // override.
        try {
            if (typeof isFavorite === 'function' && isFavorite(svc.name, method.name)) {
                base += 50;
            }
        } catch { /* ignore */ }
        return base;
    }

    // Builds the list of suggestions shown under the command palette.
    // Order: AI lane (?-prefix) → Methods (scored top 8) → Filters →
    // Recordings → Mocks → Navigate → Protocols → Environments. Each
    // suggestion carries an onSelect callback that does the work.
    function buildSearchSuggestions(query) {
        var q = (query || '').trim();
        if (!q) return [];
        var qLower = q.toLowerCase();
        var out = [];

        // --- #124 AI lane via `?` prefix ---
        // Single ? — empty prompt, just an affordance row.
        // ?<text> — routes the query to the Assistant. Phase 1 opens
        // the AI drawer with the question seeded; inline-streaming the
        // answer into the dropdown is Phase 2 (richer UI, needs
        // multiline rendering + cancellation).
        if (q.charAt(0) === '?') {
            var prompt = q.substring(1).trim();
            out.push({
                group: 'Assistant',
                label: prompt ? ('Ask Assistant: ' + prompt) : 'Ask Assistant…',
                sublabel: prompt
                    ? 'Opens the Assistant drawer with this prompt'
                    : 'Type your question after the ? prefix',
                icon: svgIcon('spark'),
                onSelect: function () {
                    searchSuggestionsOpen = false;
                    searchQuery = '';
                    aiDrawerOpen = true;
                    try { localStorage.setItem('bowire_ai_drawer_open', '1'); } catch { /* ignore */ }
                    if (prompt && typeof sendChat === 'function') {
                        try { sendChat(prompt); }
                        catch (e) { console.warn('[ai] sendChat failed', e); }
                    }
                    render();
                }
            });
            // ?-prefix returns exclusively the AI lane — the user opted
            // into the AI channel, don't pollute the dropdown with
            // method matches for "how"/"what" prefixes.
            return out;
        }

        // --- Methods (scored, top 8) ---
        var methodMatches = [];
        for (var si = 0; si < services.length; si++) {
            var svc = services[si];
            if (!svc.methods) continue;
            for (var mi = 0; mi < svc.methods.length; mi++) {
                var m = svc.methods[mi];
                var score = _scoreMethodMatch(m, svc, qLower);
                if (score > 0) methodMatches.push({ svc: svc, method: m, score: score });
            }
        }
        methodMatches.sort(function (a, b) { return b.score - a.score; });
        if (methodMatches.length > 8) methodMatches = methodMatches.slice(0, 8);
        for (var mmi = 0; mmi < methodMatches.length; mmi++) {
            (function (mm) {
                var proto = protocols.find(function (p) { return p.id === mm.svc.source; });
                // #156 — flag favorites in the sublabel so the operator
                // sees WHY the row ranks at the top. Star renders as
                // a leading glyph in the sublabel because the existing
                // suggestion shape doesn't have a separate trailing
                // marker slot.
                var fav = false;
                try { fav = (typeof isFavorite === 'function') && isFavorite(mm.svc.name, mm.method.name); }
                catch { /* ignore */ }
                out.push({
                    group: 'Methods',
                    // Label = just the method name. The service goes on
                    // the right as a sublabel — users asked for this so
                    // the scan reads as "method ← service" instead of
                    // duplicating the service name in two places.
                    label: mm.method.name,
                    sublabel: (fav ? '★ ' : '') + mm.svc.name,
                    icon: proto ? proto.icon : null,
                    badge: mm.method.methodType === 'Unary' ? null : (mm.method.methodType || ''),
                    badgeType: (mm.method.methodType || '').toLowerCase(),
                    onSelect: function () {
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        openTab(mm.svc, mm.method);
                    }
                });
            })(methodMatches[mmi]);
        }

        // --- Apply as name filter ---
        out.push({
            group: 'Filters',
            label: 'Apply as name filter: "' + q + '"',
            sublabel: 'Pins the query as a persistent filter chip',
            icon: svgIcon('filter'),
            onSelect: function () {
                setNameFilter(q);
                searchQuery = '';
                searchSuggestionsOpen = false;
                render();
            }
        });

        // #124 — Recordings (by name). Up to 5 matches.
        if (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) {
            var recMatches = 0;
            for (var ri = 0; ri < recordingsList.length && recMatches < 5; ri++) {
                (function (rec) {
                    if ((rec.name || '').toLowerCase().indexOf(qLower) === -1) return;
                    recMatches++;
                    var steps = (rec.steps ? rec.steps.length : 0);
                    out.push({
                        group: 'Recordings',
                        label: rec.name,
                        sublabel: steps + ' step' + (steps === 1 ? '' : 's'),
                        icon: svgIcon('filmstrip'),
                        onSelect: function () {
                            recordingManagerSelectedId = rec.id;
                            railMode = 'recordings';
                            try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                            searchSuggestionsOpen = false;
                            searchQuery = '';
                            render();
                        }
                    });
                })(recordingsList[ri]);
            }
        }

        // #124 — Collections (by name). Selecting one promotes the
        // Workspaces rail with the matching collection focused inside
        // the workspace tree's main pane — same place the operator
        // manages collections day-to-day.
        if (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) {
            var colMatches = 0;
            for (var ci = 0; ci < collectionsList.length && colMatches < 5; ci++) {
                (function (col) {
                    if ((col.name || '').toLowerCase().indexOf(qLower) === -1) return;
                    colMatches++;
                    var items = Array.isArray(col.items) ? col.items.length : 0;
                    out.push({
                        group: 'Collections',
                        label: col.name,
                        sublabel: items + ' item' + (items === 1 ? '' : 's'),
                        icon: svgIcon('folder'),
                        onSelect: function () {
                            if (typeof collectionManagerSelectedId !== 'undefined') {
                                collectionManagerSelectedId = col.id;
                            }
                            var ws = (typeof activeWorkspace === 'function') ? activeWorkspace() : null;
                            if (ws && typeof workspaceTreeSelection !== 'undefined') {
                                workspacesSelectedId = ws.id;
                                workspaceTreeSelection = { wsId: ws.id, kind: 'collection', value: col.id };
                            }
                            railMode = 'workspaces';
                            try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                            searchSuggestionsOpen = false;
                            searchQuery = '';
                            render();
                        }
                    });
                })(collectionsList[ci]);
            }
        }

        // #124 — Workspaces (switch active). Substring match on
        // workspace name. The active workspace is skipped so the
        // palette doesn't suggest "switch to the one you're already
        // on". Selecting reloads via switchWorkspace, same as the
        // topbar dropdown.
        if (typeof workspaces !== 'undefined' && Array.isArray(workspaces)) {
            for (var wi = 0; wi < workspaces.length; wi++) {
                (function (ws) {
                    if (ws.id === activeWorkspaceId) return;
                    if ((ws.name || '').toLowerCase().indexOf(qLower) === -1) return;
                    out.push({
                        group: 'Workspaces',
                        label: 'Switch to: ' + ws.name,
                        sublabel: 'Workspace',
                        icon: svgIcon('layers'),
                        onSelect: function () {
                            searchSuggestionsOpen = false;
                            searchQuery = '';
                            if (typeof switchWorkspace === 'function') switchWorkspace(ws.id);
                        }
                    });
                })(workspaces[wi]);
            }
        }

        // #124 — Active mocks (by recording name + port).
        if (typeof mocksList !== 'undefined' && Array.isArray(mocksList)) {
            for (var moi = 0; moi < mocksList.length; moi++) {
                (function (mk) {
                    var hay = (mk.recordingName || '') + ' ' + String(mk.port);
                    if (hay.toLowerCase().indexOf(qLower) === -1) return;
                    out.push({
                        group: 'Active mocks',
                        label: mk.recordingName || ('mock-' + mk.port),
                        sublabel: 'port ' + mk.port,
                        icon: svgIcon('server'),
                        onSelect: function () {
                            mockSelectedId = mk.mockId;
                            railMode = 'mocks';
                            try { localStorage.setItem('bowire_rail_mode', 'mocks'); } catch { /* ignore */ }
                            searchSuggestionsOpen = false;
                            searchQuery = '';
                            render();
                        }
                    });
                })(mocksList[moi]);
            }
        }

        // #124 — Rail-mode jumps. Lets the operator type 'home',
        // 'security', 'bench' etc. to navigate without clicking the
        // rail. Substring match on mode label + id. Collections,
        // Environments, and Recordings dropped — they live inside
        // the Workspaces tree now and have their own palette lanes
        // above (search by entity name), so a "Go to" jump would
        // navigate nowhere visible.
        var railJumps = [
            { id: 'home',         label: 'Home',              icon: 'house' },
            { id: 'discover',     label: 'Discover',          icon: 'discover' },
            { id: 'mocks',        label: 'Mocks',             icon: 'mock' },
            { id: 'flows',        label: 'Flows',             icon: 'flow' },
            { id: 'proxy',        label: 'Proxy / MITM',      icon: 'disconnect' },
            { id: 'benchmarks',   label: 'Benchmarks',        icon: 'chart' },
            { id: 'security',     label: 'Security',          icon: 'shield' },
            { id: 'workspaces',   label: 'Workspaces',        icon: 'layers' },
        ];
        for (var rj = 0; rj < railJumps.length; rj++) {
            (function (mode) {
                if (mode.label.toLowerCase().indexOf(qLower) === -1
                    && mode.id.toLowerCase().indexOf(qLower) === -1) return;
                if (railMode === mode.id) return; // already there
                out.push({
                    group: 'Navigate',
                    label: 'Go to ' + mode.label,
                    sublabel: 'Rail mode',
                    icon: svgIcon(mode.icon),
                    onSelect: function () {
                        railMode = mode.id;
                        try { localStorage.setItem('bowire_rail_mode', mode.id); } catch { /* ignore */ }
                        // Mirror the rail-button click handler's
                        // sidebarView sync so the legacy main-pane
                        // routing picks up the right editor.
                        if (mode.id === 'environments') sidebarView = 'environments';
                        else if (mode.id === 'flows') sidebarView = 'flows';
                        else if (mode.id === 'proxy') sidebarView = 'proxy';
                        else if (mode.id === 'discover') sidebarView = 'services';
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        render();
                    }
                });
            })(railJumps[rj]);
        }

        // --- #252 — Compose entry-points ---
        // Two palette commands mirror the Home tiles: 'Compose new
        // request' (self-contained URL) and 'New from source…' (URL
        // bound to a workspace-managed Source). Match against label +
        // common keywords so 'new', 'compose', 'freeform', 'ad-hoc'
        // all surface them.
        var composeKeys = ['compose', 'new', 'request', 'freeform', 'ad-hoc', 'adhoc'];
        var composeMatch = composeKeys.some(function (k) { return k.indexOf(qLower) === 0 || qLower.indexOf(k) === 0; });
        if (composeMatch || 'compose new request'.indexOf(qLower) >= 0) {
            out.push({
                group: 'New',
                label: 'Compose new request',
                sublabel: 'Self-contained — URL lives inline on this request',
                icon: svgIcon('plus'),
                onSelect: function () {
                    if (typeof startFreeformRequest === 'function') {
                        startFreeformRequest({ urlMode: 'inline' });
                    }
                    searchSuggestionsOpen = false;
                    searchQuery = '';
                }
            });
        }
        var hasAnySrc = typeof serverUrls !== 'undefined'
            && Array.isArray(serverUrls) && serverUrls.length > 0;
        if (hasAnySrc && (composeMatch || 'new from source'.indexOf(qLower) >= 0 || 'source'.indexOf(qLower) === 0)) {
            out.push({
                group: 'New',
                label: 'New from source…',
                sublabel: 'URL bound to a workspace-managed Source',
                icon: svgIcon('connect'),
                onSelect: function () {
                    if (typeof openNewFromSourceDialog === 'function') {
                        openNewFromSourceDialog();
                    } else if (typeof startFreeformRequest === 'function') {
                        startFreeformRequest({ urlMode: 'source', sourceUrl: serverUrls[0] });
                    }
                    searchSuggestionsOpen = false;
                    searchQuery = '';
                }
            });
        }

        // --- Protocol filter matches ---
        for (var pi = 0; pi < protocols.length; pi++) {
            (function (p) {
                if (!isProtocolEnabled(p.id)) return;
                if (p.name.toLowerCase().indexOf(qLower) === -1
                    && p.id.toLowerCase().indexOf(qLower) === -1) return;
                var count = services.filter(function (s) { return s.source === p.id; })
                    .reduce(function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
                if (count === 0) return;
                var already = protocolFilter.has(p.id);
                out.push({
                    group: 'Protocols',
                    label: (already ? 'Remove filter: ' : 'Filter by: ') + p.name,
                    sublabel: count + ' method' + (count === 1 ? '' : 's'),
                    icon: p.icon,
                    onSelect: function () {
                        if (already) protocolFilter.delete(p.id);
                        else protocolFilter.add(p.id);
                        persistProtocolFilter();
                        refreshSelectedProtocolFromFilter();
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        render();
                    }
                });
            })(protocols[pi]);
        }

        // --- Environments ---
        var envs = getEnvironments();
        for (var ei = 0; ei < envs.length; ei++) {
            (function (env) {
                if (env.name.toLowerCase().indexOf(qLower) === -1) return;
                out.push({
                    group: 'Environments',
                    label: 'Switch to environment: ' + env.name,
                    sublabel: (Object.keys(env.vars || {}).length) + ' variable'
                        + (Object.keys(env.vars || {}).length === 1 ? '' : 's'),
                    icon: svgIcon('globe'),
                    onSelect: function () {
                        setActiveEnvId(env.id);
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        render();
                    }
                });
            })(envs[ei]);
        }

        // #124 — Settings tabs. Both 'settings' as a keyword and the
        // tab names (General / Shortcuts / Data / Assistant / Plugins)
        // surface a jump. Selecting deep-links via openSettings(tab).
        var settingsTabs = [
            { id: null,        label: 'Open Settings',            keywords: ['settings', 'preferences', 'options'] },
            { id: 'general',   label: 'Settings → General',       keywords: ['settings general'] },
            { id: 'shortcuts', label: 'Settings → Shortcuts',     keywords: ['settings shortcuts', 'keymap', 'bindings'] },
            { id: 'data',      label: 'Settings → Data',          keywords: ['settings data', 'reset', 'clear'] },
            { id: 'ai',        label: 'Settings → Assistant',     keywords: ['settings ai', 'assistant', 'llm'] },
            { id: 'plugins',   label: 'Settings → Plugins',       keywords: ['settings plugins'] }
        ];
        for (var sti = 0; sti < settingsTabs.length; sti++) {
            (function (t) {
                var hay = t.label.toLowerCase() + ' ' + t.keywords.join(' ');
                if (hay.indexOf(qLower) === -1) return;
                out.push({
                    group: 'Settings',
                    label: t.label,
                    sublabel: 'Preferences',
                    icon: svgIcon('settings'),
                    onSelect: function () {
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        if (typeof openSettings === 'function') openSettings(t.id || undefined);
                    }
                });
            })(settingsTabs[sti]);
        }

        // #124 — Help. Opens the Help drawer, plus surfaces specific
        // help topics matched against the loaded topic catalogue.
        if (('help'.indexOf(qLower) !== -1) || ('docs'.indexOf(qLower) !== -1)) {
            out.push({
                group: 'Help',
                label: 'Open Help drawer',
                sublabel: 'Contextual + topic search',
                icon: svgIcon('help'),
                onSelect: function () {
                    searchSuggestionsOpen = false;
                    searchQuery = '';
                    if (typeof helpOpenDrawer === 'function') helpOpenDrawer();
                    else render();
                }
            });
        }
        if (typeof helpTopics !== 'undefined' && Array.isArray(helpTopics)) {
            var topicMatches = 0;
            for (var hi = 0; hi < helpTopics.length && topicMatches < 5; hi++) {
                (function (t) {
                    var hay = ((t.title || '') + ' ' + (t.id || '')).toLowerCase();
                    if (hay.indexOf(qLower) === -1) return;
                    topicMatches++;
                    out.push({
                        group: 'Help',
                        label: t.title || t.id,
                        sublabel: 'Help topic',
                        icon: svgIcon('help'),
                        onSelect: function () {
                            searchSuggestionsOpen = false;
                            searchQuery = '';
                            if (typeof helpOpenDrawer === 'function') helpOpenDrawer(t.id);
                            else render();
                        }
                    });
                })(helpTopics[hi]);
            }
        }

        // #124 — Workbench commands. Static built-ins plus a window-
        // scoped extension hook so plugins can register their own
        // entries via window.bowireRegisterPaletteCommand(...). Each
        // command has { id, label, sublabel?, icon?, keywords?,
        // when?, run }. when() returns false to suppress (e.g. "Stop
        // all mocks" only when there are mocks running).
        var builtinCommands = [
            { id: 'cmd:start-recording', label: 'Start recording',     icon: 'recording',
              keywords: 'record start capture',
              when: function () { return typeof startRecording === 'function'; },
              run: function () {
                  if (typeof startRecording === 'function') startRecording();
                  railMode = 'discover';
                  try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
              } },
            { id: 'cmd:stop-all-mocks', label: 'Stop all mocks',       icon: 'stop',
              keywords: 'mock stop disable',
              when: function () { return typeof mocksList !== 'undefined' && Array.isArray(mocksList) && mocksList.length > 0; },
              run: function () {
                  if (typeof stopAllMocks === 'function') stopAllMocks();
                  else if (typeof mocksList !== 'undefined') mocksList.forEach(function (m) {
                      if (typeof stopMock === 'function') stopMock(m.mockId);
                  });
              } },
            { id: 'cmd:toggle-console', label: 'Toggle Console panel', icon: 'clock',
              keywords: 'console log activity terminal',
              run: function () { if (typeof toggleConsole === 'function') toggleConsole(); } },
            { id: 'cmd:toggle-tests',   label: 'Toggle Tests drawer',  icon: 'beaker',
              keywords: 'tests assertions',
              run: function () {
                  if (typeof testsDrawerOpen !== 'undefined') {
                      testsDrawerOpen = !testsDrawerOpen;
                      try { localStorage.setItem('bowire_tests_drawer_open', testsDrawerOpen ? '1' : '0'); }
                      catch { /* ignore */ }
                  }
              } },
            { id: 'cmd:toggle-assistant', label: 'Toggle Assistant drawer', icon: 'bot',
              keywords: 'assistant ai chat',
              run: function () {
                  aiDrawerOpen = !aiDrawerOpen;
                  try { localStorage.setItem('bowire_ai_drawer_open', aiDrawerOpen ? '1' : '0'); }
                  catch { /* ignore */ }
              } },
            { id: 'cmd:toggle-sidebar',  label: 'Toggle sidebar',      icon: 'sidebar',
              keywords: 'sidebar collapse expand',
              run: function () {
                  if (typeof toggleSidebarCollapsed === 'function') toggleSidebarCollapsed();
              } },
            { id: 'cmd:open-shortcuts',  label: 'Show keyboard shortcuts', icon: 'list',
              keywords: 'shortcuts cheatsheet keybindings',
              run: function () { if (typeof shortcutSheetOpen !== 'undefined') shortcutSheetOpen = true; } },
            { id: 'cmd:reset-hints',     label: 'Reset all hints and warnings', icon: 'info',
              keywords: 'hint banner notification reset clear',
              when: function () {
                  return typeof listDismissedHints === 'function' && listDismissedHints().length > 0;
              },
              run: function () {
                  if (typeof resetAllDismissedHints === 'function') resetAllDismissedHints();
              } }
        ];
        var pluginCommands = (typeof window !== 'undefined' && Array.isArray(window.__bowirePaletteCommands))
            ? window.__bowirePaletteCommands : [];
        var allCommands = builtinCommands.concat(pluginCommands);
        for (var cmi = 0; cmi < allCommands.length; cmi++) {
            (function (cmd) {
                if (typeof cmd.when === 'function') {
                    try { if (!cmd.when()) return; } catch { return; }
                }
                var hay = ((cmd.label || '') + ' ' + (cmd.keywords || '')).toLowerCase();
                if (hay.indexOf(qLower) === -1) return;
                out.push({
                    group: 'Commands',
                    label: cmd.label,
                    sublabel: cmd.sublabel || 'Workbench command',
                    icon: typeof cmd.icon === 'string'
                        ? svgIcon(cmd.icon) : (cmd.icon || svgIcon('lightning')),
                    onSelect: function () {
                        searchSuggestionsOpen = false;
                        searchQuery = '';
                        try { cmd.run(); }
                        catch (e) { console.warn('[palette] command failed', cmd.id, e); }
                        if (typeof render === 'function') render();
                    }
                });
            })(allCommands[cmi]);
        }

        return out;
    }

    // ---- Environments UI ----

    function renderEnvSelector() {
        var bar = el('div', { id: 'bowire-sidebar-env-bar', className: 'bowire-env-bar' });

        var envs = getEnvironments();
        var activeId = getActiveEnvId();
        var activeEnv = envs.find(function (e) { return e.id === activeId; });
        var activeColor = activeEnv && activeEnv.color
            ? activeEnv.color : 'var(--bowire-text-tertiary)';
        var envBtnWrapper = el('div', { className: 'bowire-env-dropdown-wrapper' });

        // Lucide 'globe' filled with the env's chosen colour. The full
        // outer circle takes the colour as both stroke + fill, so the
        // glyph reads as a coloured planet at a glance; the meridian
        // + horizon lines render on top in currentColor so the
        // latitude / longitude marks remain visible (and adapt to
        // theme + hover state). When no env is active the fill falls
        // back to tertiary text so the chip reads as "empty / pick".
        function _envGlyph(color) {
            // Outer circle: filled with the env's colour, outline drawn
            // in currentColor so the planet still reads as a globe (with
            // a visible silhouette) instead of a flat coloured disc.
            // Meridian + horizon also currentColor → all three "globe
            // lines" share the same stroke and pop against the fill.
            return '<svg viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" width="14" height="14">'
                + '<circle cx="12" cy="12" r="10" fill="' + color + '"/>'
                + '<line x1="2" y1="12" x2="22" y2="12" fill="none"/>'
                + '<path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z" fill="none"/>'
                + '</svg>';
        }

        // Environments are workspace-scoped (#A). Without an active
        // workspace the env chip should NOT advertise 'Workspace
        // defaults' (which doesn't exist) and shouldn't open a picker.
        // Render as a non-clickable, greyed-out label saying 'No
        // workspace'. Same gate-spirit as the env-create disable in
        // the dropdown action below.
        //
        // The onClick handler RE-READS activeWorkspaceId at click time
        // — morphdom preserves this trigger button across renders, so
        // capturing the render-time flag in the closure leaves the
        // gate stuck on its first-render value (e.g. after 0→1
        // workspace transition the button gets re-rendered with the
        // 'enabled' class but the closure still holds noWorkspaceActive
        // = true, so click does nothing). See feedback-morphdom-stale-
        // handler-pitfall.
        var noWorkspaceActive = !activeWorkspaceId;

        var envBtn = el('button', {
            id: 'bowire-env-dropdown-btn',
            className: 'bowire-env-dropdown-btn'
                + (noWorkspaceActive ? ' bowire-env-dropdown-btn-disabled' : ''),
            title: noWorkspaceActive
                ? 'Activate a workspace first — environments live inside a workspace'
                : 'Active environment — click to switch',
            'aria-disabled': noWorkspaceActive ? 'true' : null,
            onClick: function (e) {
                e.stopPropagation();
                // Re-read at click time, NOT the closure-captured
                // render-time value.
                if (!activeWorkspaceId) return;
                // Close the workspace dropdown if it's open. They
                // sit next to each other in the topbar and only one
                // should ever be visible at a time.
                if (workspaceMenuOpen) {
                    workspaceMenuOpen = false;
                    render();
                }
                var existing = envBtnWrapper.querySelector('.bowire-env-dropdown-menu');
                if (existing) { existing.remove(); return; }
                // Re-read every piece of state at click time. morphdom
                // can preserve the trigger button across renders; its old
                // onClick closure would otherwise capture the previous
                // render's `activeId` / `envs` and the freshly-opened
                // dropdown would build with stale data — check ends up on
                // the wrong row, list misses newly-created envs, etc.
                // See feedback-morphdom-stale-handler-pitfall for the
                // general recipe.
                var envs = getEnvironments();
                var activeId = getActiveEnvId();
                var menu = el('div', { className: 'bowire-env-dropdown-menu' });

                function _envRow(env, isActive, label, color, onPick) {
                    var tools = el('div', { className: 'bowire-env-dropdown-item-tools' },
                        el('span', {
                            className: 'bowire-env-dropdown-item-check' + (isActive ? ' is-active' : ''),
                            textContent: '✓',
                            'aria-hidden': isActive ? 'false' : 'true'
                        })
                    );
                    if (env) {
                        // Per-row pencil → bowirePrompt rename, persisted
                        // via updateEnvironment. Mirrors the workspace
                        // dropdown's pencil affordance — pencil first
                        // because rename is the most common per-row tweak.
                        tools.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-env-dropdown-item-rename',
                            title: 'Rename environment',
                            'aria-label': 'Rename environment',
                            innerHTML: svgIcon('pencil'),
                            onClick: function (ev) {
                                ev.stopPropagation();
                                var envId = env.id;
                                var oldName = env.name || '';
                                menu.remove();
                                bowirePrompt('Rename environment', {
                                    title: 'Rename',
                                    defaultValue: oldName,
                                    confirmText: 'Rename',
                                    validator: function (val) {
                                        var trimmed = String(val || '').trim();
                                        if (!trimmed) return 'Name required';
                                        if (trimmed.toLowerCase() === String(oldName || '').trim().toLowerCase()) return null;
                                        if (typeof _isEnvironmentNameTaken === 'function'
                                            && _isEnvironmentNameTaken(trimmed, envId)) {
                                            if (typeof toast === 'function') {
                                                toast('An environment named "' + trimmed + '" already exists.', 'error');
                                            }
                                            return 'Duplicate';
                                        }
                                        return null;
                                    }
                                }).then(function (renamed) {
                                    if (renamed) {
                                        if (typeof updateEnvironment === 'function') {
                                            updateEnvironment(envId, { name: renamed });
                                        }
                                        render();
                                    }
                                });
                            }
                        }));
                        // Per-row gear → jump straight to the env's editor
                        // in the Workspaces rail (mirrors the workspace
                        // chip's per-row "edit settings" affordance).
                        tools.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-env-dropdown-item-edit',
                            title: 'Edit environment',
                            'aria-label': 'Edit environment',
                            innerHTML: svgIcon('settings'),
                            onClick: function (ev) {
                                ev.stopPropagation();
                                menu.remove();
                                if (typeof envSidebarSelectedId !== 'undefined') {
                                    envSidebarSelectedId = env.id;
                                }
                                workspacesSelectedId = activeWorkspaceId;
                                workspaceTreeSelection = { wsId: activeWorkspaceId, kind: 'env', value: env.id };
                                railMode = 'workspaces';
                                try { localStorage.setItem('bowire_rail_mode', 'workspaces'); }
                                catch { /* ignore */ }
                                render();
                            }
                        }));
                        // Per-row trash. Mirrors the workspace dropdown
                        // pattern — delete is one hover away on every row.
                        tools.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-env-dropdown-item-trash',
                            title: 'Delete environment',
                            'aria-label': 'Delete environment',
                            innerHTML: svgIcon('trash'),
                            onClick: function (ev) {
                                ev.stopPropagation();
                                var envId = env.id;
                                var envName = env.name || '(unnamed)';
                                menu.remove();
                                bowireConfirm(
                                    'Delete environment "' + envName + '"? Variables stored in this environment are removed.',
                                    function () {
                                        if (typeof deleteEnvironment === 'function') deleteEnvironment(envId);
                                        render();
                                    },
                                    { title: 'Delete environment', confirmText: 'Delete', danger: true }
                                );
                            }
                        }));
                    }
                    return el('div', {
                        className: 'bowire-env-dropdown-item' + (isActive ? ' active' : ''),
                        onClick: onPick
                    },
                        el('span', {
                            className: 'bowire-env-dropdown-item-glyph',
                            innerHTML: _envGlyph(color)
                        }),
                        el('span', { className: 'bowire-env-dropdown-item-name', textContent: label }),
                        tools
                    );
                }

                // Renamed from "No environment" — the empty-env state
                // doesn't disable variables, it just turns off the env
                // override layer. Globals + workspace defaults still
                // resolve (see getMergedVars in history-env.js). The
                // label reflects what's actually active.
                menu.appendChild(_envRow(null, !activeId, 'Workspace defaults',
                    'var(--bowire-text-tertiary)',
                    function () { setActiveEnvId(''); menu.remove(); render(); }
                ));
                for (var di = 0; di < envs.length; di++) {
                    (function (env) {
                        menu.appendChild(_envRow(env, env.id === activeId, env.name,
                            env.color || 'var(--bowire-accent)',
                            function () { setActiveEnvId(env.id); menu.remove(); render(); }
                        ));
                    })(envs[di]);
                }

                menu.appendChild(el('div', { className: 'bowire-env-dropdown-divider' }));
                // Environments are workspace-scoped (#A — self-
                // contained workspaces). Without an active workspace
                // there's no bucket to create the env in. Disable the
                // 'New environment…' action + show a hint instead of
                // landing the operator on a dialog that can't persist
                // the result.
                var canCreateEnv = !!activeWorkspaceId;
                menu.appendChild(el('div', {
                    className: 'bowire-env-dropdown-item bowire-env-dropdown-item-action'
                        + (canCreateEnv ? '' : ' bowire-env-dropdown-item-disabled'),
                    title: canCreateEnv
                        ? 'Create a new environment in the active workspace'
                        : 'Activate a workspace first — environments live inside a workspace',
                    'aria-disabled': canCreateEnv ? null : 'true',
                    onClick: canCreateEnv ? function () {
                        menu.remove();
                        if (typeof openCreateEnvironmentDialog !== 'function') return;
                        openCreateEnvironmentDialog(function (env) {
                            if (typeof envSidebarSelectedId !== 'undefined') {
                                envSidebarSelectedId = env.id;
                            }
                            if (typeof setActiveEnvId === 'function') setActiveEnvId(env.id);
                            // Jump operator straight into the new env's editor
                            // so editing the variables happens in one flow.
                            workspacesSelectedId = activeWorkspaceId;
                            workspaceTreeSelection = { wsId: activeWorkspaceId, kind: 'env', value: env.id };
                            railMode = 'workspaces';
                            try { localStorage.setItem('bowire_rail_mode', 'workspaces'); }
                            catch { /* ignore */ }
                            render();
                        });
                    } : function (e) { e.stopPropagation(); }
                },
                    el('span', { className: 'bowire-env-dropdown-item-icon', textContent: '+' }),
                    el('span', { textContent: canCreateEnv
                        ? 'New environment…'
                        : 'New environment… (activate a workspace first)' })
                ));

                envBtnWrapper.appendChild(menu);
                setTimeout(function () {
                    document.addEventListener('click', function h() { menu.remove(); document.removeEventListener('click', h); }, { once: true });
                }, 0);
            }
        },
            el('span', {
                className: 'bowire-env-btn-glyph',
                innerHTML: _envGlyph(activeColor)
            }),
            el('span', { className: 'bowire-env-btn-text', textContent: noWorkspaceActive
                ? 'No workspace'
                : (activeEnv ? activeEnv.name : 'Workspace defaults') }),
            noWorkspaceActive
                ? null
                : el('span', { className: 'bowire-env-btn-chevron', innerHTML: svgIcon('chevronDown') })
        );
        envBtnWrapper.appendChild(envBtn);
        bar.appendChild(envBtnWrapper);

        // Lock indicator when active env has an auth helper configured
        if (activeEnvHasAuth()) {
            var authLabel = (function () {
                var a = activeEnv && activeEnv.auth;
                if (!a) return 'Auth';
                if (a.type === 'bearer') return 'Bearer';
                if (a.type === 'basic')  return 'Basic';
                if (a.type === 'apikey') return 'API Key';
                return 'Auth';
            })();
            bar.appendChild(el('span', {
                className: 'bowire-env-auth-badge',
                title: authLabel + ' auth applied to every request from this environment',
                innerHTML: svgIcon('lock')
            }));
        }

        // Old env settings gear button removed — environments have
        // their own sidebar tab now, and settings live in the global
        // Settings dialog.

        // Suppress unused-var lint
        void activeEnv;
        return bar;
    }


    // renderEnvManager removed — environment editing now lives
    // inline in the main pane (renderEnvironmentEditor in render-main.js).
    // The function stub remains so any stale call sites don't crash.
    function renderEnvManager() {
        var existing = $('.bowire-env-modal-overlay');
        if (existing) existing.remove();
    }

    // ---- Environment Diff ----
    // The compare dropdown lives at the top of the variable editor when
    // an env is selected. Picking a target env flips the right panel to
    // a diff table; picking "(none)" returns to the editor view.
    function renderEnvCompareDropdown(currentEnv, allEnvs) {
        var bar = el('div', { className: 'bowire-env-compare-bar' });
        bar.appendChild(el('span', {
            className: 'bowire-env-compare-label',
            textContent: 'Compare with'
        }));
        var select = el('select', {
            id: 'bowire-env-compare-select',
            className: 'bowire-env-compare-select',
            onChange: function (e) {
                var val = e.target.value;
                envManagerDiffTargetId = val || null;
                // renderEnvManager is a no-op stub since the env editor
                // moved inline (renderEnvironmentEditor in render-main.js)
                // — call the global render() so the diff body actually
                // re-renders when the user picks a compare-target.
                render();
            }
        });
        select.appendChild(el('option', { value: '', textContent: '(none)' }));
        for (var i = 0; i < allEnvs.length; i++) {
            var opt = allEnvs[i];
            if (opt.id === currentEnv.id) continue;
            var optEl = el('option', { value: opt.id, textContent: opt.name });
            if (opt.id === envManagerDiffTargetId) optEl.selected = true;
            select.appendChild(optEl);
        }
        bar.appendChild(select);
        return bar;
    }

    // Builds the diff table itself. Each row is one variable name that
    // appears in either env, classified as equal / changed / only-in-A
    // / only-in-B. Globals are folded in so the user sees the effective
    // value at request time, not just what's per-env. The diff sorts
    // alphabetically because there's no meaningful order across envs.
    function renderEnvDiffPane(envA, envB, allEnvs) {
        var pane = el('div', { className: 'bowire-env-diff-pane' });
        pane.appendChild(renderEnvCompareDropdown(envA, allEnvs));

        if (!envB) {
            pane.appendChild(el('div', { className: 'bowire-env-diff-empty',
                textContent: 'Select an environment to compare against.' }));
            return pane;
        }

        var globals = getGlobalVars();
        // Effective vars = globals overridden by per-env vars, mirroring
        // getMergedVars() behaviour at request time.
        function effective(env) {
            var out = {};
            for (var k in globals) if (Object.prototype.hasOwnProperty.call(globals, k)) out[k] = globals[k];
            var ev = env.vars || {};
            for (var k2 in ev) if (Object.prototype.hasOwnProperty.call(ev, k2)) out[k2] = ev[k2];
            return out;
        }
        var aVars = effective(envA);
        var bVars = effective(envB);

        var keySet = {};
        for (var k1 in aVars) keySet[k1] = true;
        for (var k2 in bVars) keySet[k2] = true;
        var allKeys = Object.keys(keySet).sort();

        var summary = { equal: 0, changed: 0, onlyA: 0, onlyB: 0 };
        var rows = allKeys.map(function (key) {
            var inA = Object.prototype.hasOwnProperty.call(aVars, key);
            var inB = Object.prototype.hasOwnProperty.call(bVars, key);
            var aVal = inA ? String(aVars[key]) : '';
            var bVal = inB ? String(bVars[key]) : '';
            var status;
            if (inA && inB) {
                if (aVal === bVal) { status = 'equal'; summary.equal++; }
                else { status = 'changed'; summary.changed++; }
            } else if (inA) {
                status = 'only-a'; summary.onlyA++;
            } else {
                status = 'only-b'; summary.onlyB++;
            }
            return { key: key, aVal: aVal, bVal: bVal, status: status };
        });

        // Header summary line — quick scan of how the envs relate.
        pane.appendChild(el('div', { className: 'bowire-env-diff-summary' },
            el('span', { className: 'bowire-env-diff-summary-title',
                textContent: envA.name + ' vs ' + envB.name }),
            el('span', { className: 'bowire-env-diff-chip equal',
                textContent: summary.equal + ' equal' }),
            el('span', { className: 'bowire-env-diff-chip changed',
                textContent: summary.changed + ' changed' }),
            el('span', { className: 'bowire-env-diff-chip only-a',
                textContent: summary.onlyA + ' only in ' + envA.name }),
            el('span', { className: 'bowire-env-diff-chip only-b',
                textContent: summary.onlyB + ' only in ' + envB.name })
        ));

        if (rows.length === 0) {
            pane.appendChild(el('div', { className: 'bowire-env-diff-empty',
                textContent: 'Both environments are empty.' }));
            return pane;
        }

        var table = el('table', { className: 'bowire-env-diff-table' });
        var thead = el('thead');
        thead.appendChild(el('tr', {},
            el('th', { textContent: 'Variable' }),
            el('th', { textContent: envA.name }),
            el('th', { textContent: envB.name })
        ));
        table.appendChild(thead);
        var tbody = el('tbody');
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i];
            tbody.appendChild(el('tr', { className: 'bowire-env-diff-row ' + r.status },
                el('td', { className: 'bowire-env-diff-key', textContent: r.key }),
                el('td', { className: 'bowire-env-diff-val', textContent: r.aVal }),
                el('td', { className: 'bowire-env-diff-val', textContent: r.bVal })
            ));
        }
        table.appendChild(tbody);
        pane.appendChild(table);
        return pane;
    }

    // ---- Auth Section (per-environment) ----
    // Type dropdown + dynamic config fields. Auth is stored on the env as
    // { type: 'none'|'bearer'|'basic'|'apikey', ...config }. Substitution
    // happens at request time, so users can store secrets as variables and
    // reference them as ${token}, ${apiKey}, etc.

    function renderAuthSection(getAuth, setAuth) {
        var section = el('div', { className: 'bowire-auth-section' });

        section.appendChild(el('div', { className: 'bowire-auth-header' },
            el('span', { innerHTML: svgIcon('lock'), className: 'bowire-auth-icon' }),
            el('span', { className: 'bowire-auth-title', textContent: 'Authentication' })
        ));

        var auth = getAuth() || { type: 'none' };

        // Type dropdown
        var typeRow = el('div', { className: 'bowire-auth-row' });
        typeRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Type' }));
        var typeSelect = el('select', {
            className: 'bowire-auth-select',
            onChange: function (e) {
                var nextType = e.target.value;
                // Reset config to defaults for the new type
                var next = { type: nextType };
                if (nextType === 'bearer') next.token = '';
                else if (nextType === 'basic') { next.username = ''; next.password = ''; }
                else if (nextType === 'apikey') { next.key = '', next.value = ''; }
                else if (nextType === 'jwt') {
                    next.algorithm = 'HS256';
                    next.header = '{\n  "alg": "HS256",\n  "typ": "JWT"\n}';
                    next.payload = '{\n  "sub": "user123",\n  "iat": ${now},\n  "exp": ${now+3600}\n}';
                    next.secret = '';
                }
                else if (nextType === 'oauth2_cc') {
                    next.tokenUrl = '';
                    next.clientId = '';
                    next.clientSecret = '';
                    next.scope = '';
                    next.audience = '';
                    clearOauthTokenCache();
                }
                else if (nextType === 'custom_token') {
                    next.tokenUrl = '';
                    next.tokenMethod = 'POST';
                    next.tokenContentType = 'application/json';
                    next.tokenBody = '{\n  "username": "${user}",\n  "password": "${password}"\n}';
                    next.tokenHeaders = '';
                    next.tokenJsonPath = 'token';
                    next.expiresInJsonPath = 'expiresIn';
                    next.tokenPrefix = 'Bearer ';
                    clearCustomTokenCache();
                }
                else if (nextType === 'aws_sigv4') {
                    next.accessKey = '';
                    next.secretKey = '';
                    next.region = 'us-east-1';
                    next.service = 'execute-api';
                    next.sessionToken = '';
                }
                else if (nextType === 'oauth2_ac') {
                    next.authorizationUrl = '';
                    next.tokenUrl = '';
                    next.clientId = '';
                    next.clientSecret = '';
                    next.scope = '';
                    clearOauth2AcTokenCacheForEnv(getActiveEnvId());
                }
                else if (nextType === 'mtls') {
                    next.certificate = '';
                    next.privateKey = '';
                    next.passphrase = '';
                    next.caCertificate = '';
                    next.allowSelfSigned = false;
                }
                setAuth(next);
                renderEnvManager();
                render(); // refresh sidebar lock indicator
            }
        });
        var types = [
            { value: 'none', label: 'No Auth' },
            { value: 'bearer', label: 'Bearer Token' },
            { value: 'session', label: 'Use my session token (forwards workbench login)' },
            { value: 'basic', label: 'Basic Auth' },
            { value: 'apikey', label: 'API Key' },
            { value: 'jwt', label: 'JWT (HMAC / RSA / ECDSA)' },
            { value: 'oauth2_cc', label: 'OAuth 2.0 (client_credentials)' },
            { value: 'oauth2_ac', label: 'OAuth 2.0 (authorization_code + PKCE)' },
            { value: 'custom_token', label: 'Custom Token Endpoint (auto-refresh)' },
            { value: 'aws_sigv4', label: 'AWS Signature v4 (REST only)' },
            { value: 'mtls', label: 'mTLS (Client Certificate)' }
        ];
        for (var ti = 0; ti < types.length; ti++) {
            var opt = el('option', { value: types[ti].value, textContent: types[ti].label });
            if (auth.type === types[ti].value) opt.setAttribute('selected', 'selected');
            typeSelect.appendChild(opt);
        }
        typeRow.appendChild(typeSelect);
        section.appendChild(typeRow);

        // Type-specific config inputs
        if (auth.type === 'bearer') {
            section.appendChild(renderAuthField('Token', 'bowire-auth-token', auth.token || '',
                'Bearer token (supports ${var})', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { type: 'bearer', token: v })); }));
        } else if (auth.type === 'session') {
            // #32 — zero-config mode. The workbench's own login token
            // (when an OIDC / SSO provider is active and exposes its
            // access token) flows through to the target as a Bearer
            // header on every invoke. No fields to fill in.
            section.appendChild(el('p', {
                className: 'bowire-auth-session-note',
                textContent: 'Forwards your workbench login (OIDC / SSO) to the target service as a Bearer header. Only useful when the same identity provider gates both Bowire and the target.'
            }));
        } else if (auth.type === 'basic') {
            section.appendChild(renderAuthField('Username', 'bowire-auth-username', auth.username || '',
                'Username (supports ${var})', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { type: 'basic', username: v })); }));
            section.appendChild(renderAuthField('Password', 'bowire-auth-password', auth.password || '',
                'Password (supports ${var})', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { type: 'basic', password: v })); }));
        } else if (auth.type === 'apikey') {
            // Location toggle — header (default) vs query string. Some
            // services (legacy REST APIs, public APIs that want a clickable
            // URL) expect the key in the URL instead of an HTTP header.
            var locRow = el('div', { className: 'bowire-auth-row' });
            locRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Location' }));
            var locSel = el('select', {
                className: 'bowire-auth-select',
                onChange: function (e) {
                    setAuth(Object.assign({}, getAuth(), { location: e.target.value }));
                }
            });
            [
                { value: 'header', label: 'Header' },
                { value: 'query', label: 'Query string' }
            ].forEach(function (loc) {
                var o = el('option', { value: loc.value, textContent: loc.label });
                if ((auth.location || 'header') === loc.value) o.setAttribute('selected', 'selected');
                locSel.appendChild(o);
            });
            locRow.appendChild(locSel);
            section.appendChild(locRow);

            var keyLabel = (auth.location === 'query') ? 'Query parameter name' : 'Header name';
            var keyPlaceholder = (auth.location === 'query') ? 'e.g. api_key' : 'e.g. X-Api-Key';
            section.appendChild(renderAuthField(keyLabel, 'bowire-auth-key', auth.key || '',
                keyPlaceholder, 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { type: 'apikey', key: v })); }));
            section.appendChild(renderAuthField('Value', 'bowire-auth-value', auth.value || '',
                'API key value (supports ${var})', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { type: 'apikey', value: v })); }));
        } else if (auth.type === 'jwt') {
            // Algorithm dropdown — HMAC, RSA, and ECDSA all in one list.
            // The asymmetric algorithms switch the secret field to a PEM
            // textarea below.
            var algRow = el('div', { className: 'bowire-auth-row' });
            algRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Algorithm' }));
            var algSel = el('select', {
                className: 'bowire-auth-select',
                onChange: function (e) {
                    setAuth(Object.assign({}, getAuth(), { algorithm: e.target.value }));
                    // Same renderEnvManager-stub story as the compare-
                    // dropdown above: the env editor moved inline, the
                    // helper is now a no-op, full render() is required
                    // to actually re-paint the secret-label / textarea.
                    render();
                }
            });
            var jwtAlgGroups = [
                { label: 'HMAC (shared secret)', algs: ['HS256', 'HS384', 'HS512'] },
                { label: 'RSA (PEM private key)', algs: ['RS256', 'RS384', 'RS512'] },
                { label: 'ECDSA (PEM private key)', algs: ['ES256', 'ES384', 'ES512'] }
            ];
            for (var jg = 0; jg < jwtAlgGroups.length; jg++) {
                var grp = el('optgroup', { label: jwtAlgGroups[jg].label });
                for (var ja = 0; ja < jwtAlgGroups[jg].algs.length; ja++) {
                    var aName = jwtAlgGroups[jg].algs[ja];
                    var o = el('option', { value: aName, textContent: aName });
                    if ((auth.algorithm || 'HS256') === aName) o.setAttribute('selected', 'selected');
                    grp.appendChild(o);
                }
                algSel.appendChild(grp);
            }
            algRow.appendChild(algSel);
            section.appendChild(algRow);

            // Header textarea
            section.appendChild(renderAuthTextarea('Header', auth.header || '',
                '{ "alg": "HS256", "typ": "JWT" }',
                function (v) { setAuth(Object.assign({}, getAuth(), { header: v })); }));

            // Payload textarea
            section.appendChild(renderAuthTextarea('Payload', auth.payload || '',
                '{ "sub": "user123", "iat": ${now}, "exp": ${now+3600} }',
                function (v) { setAuth(Object.assign({}, getAuth(), { payload: v })); }));

            // Secret — text input for HMAC, multi-line PEM textarea for
            // RSA / ECDSA so users can paste the full key block.
            if (isAsymmetricJwtAlg(auth.algorithm)) {
                section.appendChild(renderAuthTextarea('Private Key (PEM, PKCS#8)', auth.secret || '',
                    '-----BEGIN PRIVATE KEY-----\nMIIEvQIBADAN...\n-----END PRIVATE KEY-----',
                    function (v) { setAuth(Object.assign({}, getAuth(), { secret: v })); }));
                section.appendChild(el('div', {
                    className: 'bowire-auth-hint',
                    textContent: 'Tip: convert OpenSSL "RSA/EC PRIVATE KEY" to PKCS#8 with: openssl pkcs8 -topk8 -nocrypt -in old.pem -out new.pem'
                }));
            } else {
                section.appendChild(renderAuthField('Secret', 'bowire-auth-secret', auth.secret || '',
                    'Signing secret (supports ${var})', 'password',
                    function (v) { setAuth(Object.assign({}, getAuth(), { secret: v })); }));
            }

            // Live preview
            section.appendChild(renderJwtPreview(auth));
        } else if (auth.type === 'oauth2_cc') {
            section.appendChild(renderAuthField('Token URL', 'bowire-auth-tokenurl', auth.tokenUrl || '',
                'https://login.example.com/oauth2/token', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenUrl: v })); clearOauthTokenCache(); }));
            section.appendChild(renderAuthField('Client ID', 'bowire-auth-clientid', auth.clientId || '',
                'Client ID (supports ${var})', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { clientId: v })); clearOauthTokenCache(); }));
            section.appendChild(renderAuthField('Client Secret', 'bowire-auth-clientsecret', auth.clientSecret || '',
                'Client secret (supports ${var})', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { clientSecret: v })); clearOauthTokenCache(); }));
            section.appendChild(renderAuthField('Scope', 'bowire-auth-scope', auth.scope || '',
                'Optional, space-separated', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { scope: v })); clearOauthTokenCache(); }));
            section.appendChild(renderAuthField('Audience', 'bowire-auth-audience', auth.audience || '',
                'Optional', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { audience: v })); clearOauthTokenCache(); }));

            // Test button + cache status
            section.appendChild(renderOauthTestButton(auth));
        } else if (auth.type === 'custom_token') {
            section.appendChild(renderAuthField('Token URL', 'bowire-auth-tokenurl', auth.tokenUrl || '',
                'https://api.example.com/login', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenUrl: v })); clearCustomTokenCache(); }));

            // HTTP method dropdown
            var methRow = el('div', { className: 'bowire-auth-row' });
            methRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Method' }));
            var methSel = el('select', {
                className: 'bowire-auth-select',
                onChange: function (e) {
                    setAuth(Object.assign({}, getAuth(), { tokenMethod: e.target.value }));
                    clearCustomTokenCache();
                }
            });
            ['POST', 'GET', 'PUT'].forEach(function (m) {
                var o = el('option', { value: m, textContent: m });
                if ((auth.tokenMethod || 'POST') === m) o.setAttribute('selected', 'selected');
                methSel.appendChild(o);
            });
            methRow.appendChild(methSel);
            section.appendChild(methRow);

            section.appendChild(renderAuthField('Content-Type', 'bowire-auth-contenttype', auth.tokenContentType || 'application/json',
                'application/json', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenContentType: v })); clearCustomTokenCache(); }));

            section.appendChild(renderAuthTextarea('Request Body', auth.tokenBody || '',
                '{ "username": "${user}", "password": "${password}" }',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenBody: v })); clearCustomTokenCache(); }));

            section.appendChild(renderAuthTextarea('Request Headers (JSON)', auth.tokenHeaders || '',
                '{ "X-Api-Key": "${apiKey}" }',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenHeaders: v })); clearCustomTokenCache(); }));

            section.appendChild(renderAuthField('Token JSON path', 'bowire-auth-tokenpath', auth.tokenJsonPath || 'token',
                'e.g. token, data.access_token, auth.jwt', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenJsonPath: v })); clearCustomTokenCache(); }));

            section.appendChild(renderAuthField('Expiry JSON path (optional)', 'bowire-auth-expirypath', auth.expiresInJsonPath || '',
                'e.g. expiresIn, expires_in (TTL in seconds)', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { expiresInJsonPath: v })); clearCustomTokenCache(); }));

            section.appendChild(renderAuthField('Authorization prefix', 'bowire-auth-tokenprefix', auth.tokenPrefix == null ? 'Bearer ' : auth.tokenPrefix,
                'e.g. "Bearer " (with trailing space) or empty', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenPrefix: v })); }));

            section.appendChild(renderCustomTokenTestButton(auth));
        } else if (auth.type === 'oauth2_ac') {
            section.appendChild(renderAuthField('Authorization URL', 'bowire-auth-oauth-authurl', auth.authorizationUrl || '',
                'https://login.example.com/oauth2/authorize', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { authorizationUrl: v })); clearOauth2AcTokenCacheForEnv(getActiveEnvId()); }));
            section.appendChild(renderAuthField('Token URL', 'bowire-auth-oauth-tokenurl', auth.tokenUrl || '',
                'https://login.example.com/oauth2/token', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { tokenUrl: v })); clearOauth2AcTokenCacheForEnv(getActiveEnvId()); }));
            section.appendChild(renderAuthField('Client ID', 'bowire-auth-oauth-clientid', auth.clientId || '',
                'Client ID (supports ${var})', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { clientId: v })); clearOauth2AcTokenCacheForEnv(getActiveEnvId()); }));
            section.appendChild(renderAuthField('Client Secret (optional, public clients leave empty)', 'bowire-auth-oauth-clientsecret', auth.clientSecret || '',
                'Only required for confidential clients', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { clientSecret: v })); clearOauth2AcTokenCacheForEnv(getActiveEnvId()); }));
            section.appendChild(renderAuthField('Scope', 'bowire-auth-oauth-scope', auth.scope || '',
                'Space-separated scopes (e.g. openid profile email)', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { scope: v })); clearOauth2AcTokenCacheForEnv(getActiveEnvId()); }));

            // Read-only redirect URI hint — users have to register this
            // exact URL with the IdP for the flow to succeed.
            var redirectRow = el('div', { className: 'bowire-auth-row' });
            redirectRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Redirect URI' }));
            redirectRow.appendChild(el('input', {
                className: 'bowire-auth-input',
                type: 'text',
                value: oauthRedirectUri(),
                readonly: 'readonly',
                onClick: function () { this.select(); }
            }));
            section.appendChild(redirectRow);
            section.appendChild(el('div', {
                className: 'bowire-auth-hint',
                textContent: 'Register this redirect URI with your identity provider exactly as shown. PKCE (S256) is enabled automatically — no client_secret needed for public clients.'
            }));

            section.appendChild(renderOauth2AcStatus(auth));
        } else if (auth.type === 'aws_sigv4') {
            section.appendChild(el('div', {
                className: 'bowire-auth-hint',
                textContent: 'AWS Sig v4 only signs REST requests. The REST plugin signs each request right before it goes on the wire — gRPC / SignalR / WebSocket calls ignore this auth type.'
            }));
            section.appendChild(renderAuthField('Access Key ID', 'bowire-auth-aws-access', auth.accessKey || '',
                'AKIA... (supports ${var})', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { accessKey: v })); }));
            section.appendChild(renderAuthField('Secret Access Key', 'bowire-auth-aws-secret', auth.secretKey || '',
                'Secret access key (supports ${var})', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { secretKey: v })); }));
            section.appendChild(renderAuthField('Region', 'bowire-auth-aws-region', auth.region || 'us-east-1',
                'e.g. us-east-1', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { region: v })); }));
            section.appendChild(renderAuthField('Service', 'bowire-auth-aws-service', auth.service || '',
                'e.g. execute-api, s3, dynamodb', 'text',
                function (v) { setAuth(Object.assign({}, getAuth(), { service: v })); }));
            section.appendChild(renderAuthField('Session Token (optional)', 'bowire-auth-aws-session', auth.sessionToken || '',
                'STS session token (supports ${var})', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { sessionToken: v })); }));
        } else if (auth.type === 'mtls') {
            section.appendChild(el('div', {
                className: 'bowire-auth-hint',
                textContent: 'mTLS attaches a client certificate to the TLS handshake. Wired into REST, gRPC, WebSocket, and SignalR — every TLS-capable transport in Bowire picks the cert up from the same auth helper.'
            }));
            section.appendChild(renderAuthTextarea('Client Certificate (PEM)', auth.certificate || '',
                '-----BEGIN CERTIFICATE-----\nMIID...\n-----END CERTIFICATE-----',
                function (v) { setAuth(Object.assign({}, getAuth(), { certificate: v })); }));
            section.appendChild(renderAuthTextarea('Private Key (PEM)', auth.privateKey || '',
                '-----BEGIN PRIVATE KEY-----\nMIIE...\n-----END PRIVATE KEY-----',
                function (v) { setAuth(Object.assign({}, getAuth(), { privateKey: v })); }));
            section.appendChild(renderAuthField('Passphrase (optional)', 'bowire-auth-mtls-passphrase', auth.passphrase || '',
                'Only required for encrypted private keys', 'password',
                function (v) { setAuth(Object.assign({}, getAuth(), { passphrase: v })); }));
            section.appendChild(renderAuthTextarea('CA Certificate (optional, PEM)', auth.caCertificate || '',
                'Trust anchor for the server certificate. Use this to verify against a private CA without trusting the system store.',
                function (v) { setAuth(Object.assign({}, getAuth(), { caCertificate: v })); }));

            var allowRow = el('div', { className: 'bowire-auth-row' });
            allowRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Allow self-signed' }));
            var checkboxWrap = el('div', { className: 'bowire-auth-checkbox-wrap' });
            var allowCheck = el('input', {
                type: 'checkbox',
                className: 'bowire-auth-checkbox',
                onChange: function (e) { setAuth(Object.assign({}, getAuth(), { allowSelfSigned: !!e.target.checked })); }
            });
            if (auth.allowSelfSigned) allowCheck.setAttribute('checked', 'checked');
            checkboxWrap.appendChild(allowCheck);
            checkboxWrap.appendChild(el('span', {
                className: 'bowire-auth-checkbox-hint',
                textContent: 'Skip server certificate validation entirely. Use only against test environments — defeats the purpose of TLS in production.'
            }));
            allowRow.appendChild(checkboxWrap);
            section.appendChild(allowRow);
        }

        // Cookie-jar toggle: independent of the auth type, lives at the
        // bottom of the section so it composes with any auth helper
        // (Bearer, Basic, mTLS, ...). When on, every request sends the
        // active env id as the __bowireCookieEnv__ marker; RestInvoker
        // hands the per-env CookieContainer to a fresh HttpClientHandler
        // so Set-Cookie persists across calls.
        section.appendChild(el('div', {
            className: 'bowire-auth-section-divider',
            textContent: 'Session cookies'
        }));

        var cookieRow = el('div', { className: 'bowire-auth-row' });
        cookieRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: 'Persist cookies' }));
        var cookieWrap = el('div', { className: 'bowire-auth-checkbox-wrap' });
        var cookieCheck = el('input', {
            type: 'checkbox',
            className: 'bowire-auth-checkbox',
            onChange: function (e) { setAuth(Object.assign({}, getAuth(), { persistCookies: !!e.target.checked })); }
        });
        if (auth.persistCookies) cookieCheck.setAttribute('checked', 'checked');
        cookieWrap.appendChild(cookieCheck);
        cookieWrap.appendChild(el('span', {
            className: 'bowire-auth-checkbox-hint',
            textContent: 'Keep Set-Cookie values across requests within this environment. Useful for session-cookie auth (PHP, Rails, classic ASP.NET). REST only.'
        }));
        cookieRow.appendChild(cookieWrap);
        section.appendChild(cookieRow);

        // Clear-cookies button — only meaningful when the toggle is on.
        if (auth.persistCookies) {
            var clearRow = el('div', { className: 'bowire-auth-row' });
            clearRow.appendChild(el('label', { className: 'bowire-auth-label', textContent: '' }));
            var clearBtn = el('button', {
                className: 'bowire-auth-preview-btn',
                textContent: 'Clear cookies',
                onClick: async function () {
                    var envId = getActiveEnvId();
                    if (!envId) return;
                    try {
                        await fetch(config.prefix + '/api/auth/cookie-jar?env=' + encodeURIComponent(envId), { method: 'DELETE' });
                        toast('Cookies cleared for this environment.');
                    } catch (e) {
                        toast('Failed to clear cookies: ' + e.message, 'error');
                    }
                }
            });
            clearRow.appendChild(clearBtn);
            section.appendChild(clearRow);
        }

        return section;
    }

    /**
     * Recursive checkbox tree for the GraphQL selection-set picker. Each
     * node renders a checkbox + the field name + an inline type hint;
     * nested object fields render their own children indented underneath.
     * Toggling a checkbox updates graphqlSelections and triggers a render
     * so the query editor below picks up the new selection. The depth cap
     * matches the mapper's (3 levels) so the tree never grows wider than
     * what the schema actually exposes.
     */
    function renderGraphQLSelectionTree(messageInfo, prefix, selections, depth) {
        var container = el('div', { className: 'bowire-graphql-selection-tree' + (depth > 0 ? ' nested' : '') });
        if (!messageInfo || !messageInfo.fields || messageInfo.fields.length === 0) {
            container.appendChild(el('div', { className: 'bowire-graphql-selection-empty', textContent:
                '(no fields discovered — schema may not expose this output type)' }));
            return container;
        }

        for (var i = 0; i < messageInfo.fields.length; i++) {
            (function (f) {
                var path = prefix ? prefix + '.' + f.name : f.name;
                var checked = !!selections[path];

                var row = el('label', { className: 'bowire-graphql-selection-row' });
                var cb = el('input', {
                    type: 'checkbox',
                    className: 'bowire-form-checkbox',
                    onChange: function () {
                        setGraphQLSelection(path, this.checked);
                        // Drop any manual query override so the regenerated
                        // query reflects the new selection.
                        delete graphqlQueryOverrides[graphqlMethodKey()];
                        render();
                    }
                });
                if (checked) cb.setAttribute('checked', 'checked');
                row.appendChild(cb);
                row.appendChild(el('span', {
                    className: 'bowire-graphql-selection-name',
                    textContent: f.name
                }));
                var typeText = f.type;
                if (f.isRepeated) typeText = '[' + typeText + ']';
                row.appendChild(el('span', { className: 'bowire-graphql-selection-type', textContent: typeText }));
                container.appendChild(row);

                // Recurse into nested object types when the field is
                // checked. Collapsing on uncheck keeps the tree compact.
                if (f.type === 'message' && f.messageType && checked) {
                    container.appendChild(renderGraphQLSelectionTree(f.messageType, path, selections, depth + 1));
                }
            })(messageInfo.fields[i]);
        }
        return container;
    }

    function renderOauth2AcStatus(auth) {
        var preview = el('div', { className: 'bowire-auth-preview' });
        preview.appendChild(el('div', { className: 'bowire-auth-preview-label', textContent: 'Authorization status' }));

        var envId = getActiveEnvId();
        var key = oauth2AcCacheKey(envId, auth);
        var cached = oauth2AcTokenCache[key];

        var output = el('div', { className: 'bowire-auth-preview-token' });
        if (cached) {
            var nowSec = Math.floor(Date.now() / 1000);
            var ttl = cached.expiresAt - nowSec;
            var statusText = ttl > 0
                ? 'Authorized — token expires in ' + Math.max(0, ttl) + 's' + (cached.refreshToken ? ' (refreshable)' : '')
                : 'Token expired' + (cached.refreshToken ? ' — will auto-refresh on next request' : ' — re-authorize');
            output.textContent = statusText;
            output.classList.add('ok');
            output.classList.remove('error');
        } else {
            output.textContent = 'Not authorized — click Authorize to start the OAuth flow';
        }

        var authorizeBtn = el('button', {
            className: 'bowire-auth-preview-btn',
            textContent: cached ? 'Re-authorize' : 'Authorize',
            onClick: async function () {
                try {
                    var fresh = await authorizeOauth2Ac(getActiveEnvId(), getAuth());
                    var ttl2 = Math.max(0, fresh.expiresAt - Math.floor(Date.now() / 1000));
                    output.textContent = 'Authorized — token expires in ' + ttl2 + 's' + (fresh.refreshToken ? ' (refreshable)' : '');
                    output.classList.add('ok');
                    output.classList.remove('error');
                    render();
                } catch (e) {
                    output.textContent = e.message;
                    output.classList.add('error');
                    output.classList.remove('ok');
                }
            }
        });
        preview.appendChild(authorizeBtn);

        if (cached) {
            preview.appendChild(el('button', {
                className: 'bowire-auth-preview-btn',
                textContent: 'Sign out',
                onClick: function () {
                    clearOauth2AcTokenCacheForEnv(getActiveEnvId());
                    renderEnvManager();
                    render();
                }
            }));
        }

        preview.appendChild(output);
        return preview;
    }

    function renderCustomTokenTestButton(auth) {
        var preview = el('div', { className: 'bowire-auth-preview' });
        preview.appendChild(el('div', { className: 'bowire-auth-preview-label', textContent: 'Token endpoint test' }));
        var output = el('div', { className: 'bowire-auth-preview-token', textContent: 'Click Fetch token to test' });
        var btn = el('button', {
            className: 'bowire-auth-preview-btn',
            textContent: 'Fetch token',
            onClick: async function () {
                clearCustomTokenCache();
                try {
                    var token = await fetchCustomToken(auth);
                    output.textContent = token;
                    output.classList.add('ok');
                    output.classList.remove('error');
                } catch (e) {
                    output.textContent = e.message;
                    output.classList.add('error');
                    output.classList.remove('ok');
                }
            }
        });
        preview.appendChild(btn);
        preview.appendChild(output);
        return preview;
    }

    function renderAuthTextarea(label, value, placeholder, onChange) {
        var row = el('div', { className: 'bowire-auth-row bowire-auth-row-stack' });
        row.appendChild(el('label', { className: 'bowire-auth-label', textContent: label }));
        row.appendChild(el('textarea', {
            className: 'bowire-auth-textarea',
            placeholder: placeholder || '',
            spellcheck: 'false',
            autocomplete: 'off',
            rows: '4',
            onInput: function (e) { onChange(e.target.value); }
        }, value));
        return row;
    }

    function renderJwtPreview(auth) {
        var preview = el('div', { className: 'bowire-auth-preview' });
        preview.appendChild(el('div', { className: 'bowire-auth-preview-label', textContent: 'Preview (signed token)' }));
        var output = el('div', { className: 'bowire-auth-preview-token', textContent: 'Click Sign to preview' });
        var btn = el('button', {
            className: 'bowire-auth-preview-btn',
            textContent: 'Sign',
            onClick: async function () {
                try {
                    var jwt = await buildJwt(auth);
                    output.textContent = jwt;
                    output.classList.add('ok');
                    output.classList.remove('error');
                } catch (e) {
                    output.textContent = e.message;
                    output.classList.add('error');
                    output.classList.remove('ok');
                }
            }
        });
        preview.appendChild(btn);
        preview.appendChild(output);
        return preview;
    }

    function renderOauthTestButton(auth) {
        var preview = el('div', { className: 'bowire-auth-preview' });
        preview.appendChild(el('div', { className: 'bowire-auth-preview-label', textContent: 'Token endpoint test' }));
        var output = el('div', { className: 'bowire-auth-preview-token', textContent: 'Click Fetch token to test' });
        var btn = el('button', {
            className: 'bowire-auth-preview-btn',
            textContent: 'Fetch token',
            onClick: async function () {
                output.textContent = 'Fetching...';
                output.classList.remove('ok', 'error');
                try {
                    clearOauthTokenCache();
                    var token = await fetchOauthClientCredentialsToken(auth);
                    output.textContent = token;
                    output.classList.add('ok');
                } catch (e) {
                    output.textContent = e.message;
                    output.classList.add('error');
                }
            }
        });
        preview.appendChild(btn);
        preview.appendChild(output);
        return preview;
    }

    function renderAuthField(label, className, value, placeholder, type, onChange) {
        var row = el('div', { className: 'bowire-auth-row' });
        row.appendChild(el('label', { className: 'bowire-auth-label', textContent: label }));
        row.appendChild(el('input', {
            className: 'bowire-auth-input ' + className,
            type: type || 'text',
            value: value,
            placeholder: placeholder || '',
            spellcheck: 'false',
            autocomplete: 'off',
            onInput: function (e) { onChange(e.target.value); }
        }));
        return row;
    }

    // renderVarEditor removed — replaced by renderEnvVariableTable
    // in render-main.js (full-width main pane with multi-line values).

