    // ---- Render ----
    function isMobile() {
        return window.innerWidth <= 600;
    }

    function render() {
        const app = document.getElementById('bowire-app');
        if (!app) return;

        // #123 — lazy tab rehydrate. One-shot guard inside; cheap
        // no-op on every subsequent render once services land. Putting
        // it at the top of render() means tab persistence works even
        // when the discovery completion path doesn't end in a render()
        // call directly.
        rehydrateRequestTabs();

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
            if (sidebarCollapsed) {
                body.classList.add('bowire-sidebar-is-collapsed');
                // Pattern A — when the sidebar is hidden the
                // affordance to bring it back lives at the rail edge.
                // Small chevron-button pointing right, vertically
                // centred, full-height-hover for forgiving aim.
                body.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-sidebar-edge-toggle bowire-sidebar-edge-toggle-collapsed',
                    title: 'Show sidebar (Ctrl+B)',
                    'aria-label': 'Show sidebar',
                    onClick: function () { setSidebarCollapsed(false); render(); }
                }, el('span', { innerHTML: svgIcon('chevron') })));
            } else {
                body.appendChild(renderSidebar());
                if (!isMobile()) {
                    // Pattern A — chevron lives AS A CHILD of the splitter
                    // so stacking/positioning is unambiguous. The
                    // splitter's mousedown handler ignores events whose
                    // target is the chevron (or the chevron's span),
                    // so a click on the chevron fires its onClick
                    // instead of starting a drag.
                    var splitter = el('div', {
                        className: 'bowire-sidebar-splitter',
                        id: 'bowire-sidebar-splitter',
                        title: 'Drag to resize the sidebar — drag past the minimum to collapse, or double-click to toggle'
                    });
                    splitter.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-sidebar-edge-toggle bowire-sidebar-edge-toggle-expanded',
                        title: 'Hide sidebar (Ctrl+B) — drag to resize',
                        'aria-label': 'Hide sidebar',
                        // No stopPropagation on mousedown — the splitter
                        // needs the event so the user can start a drag
                        // FROM the chevron pixel. The splitter's
                        // mousedown handler uses a movement threshold:
                        // a wiggle-free click here still fires onClick
                        // (browser default), a drag past threshold
                        // becomes a resize.
                        onClick: function (e) {
                            e.stopPropagation();
                            setSidebarCollapsed(true);
                            render();
                        }
                    }, el('span', { innerHTML: svgIcon('chevron') })));
                    body.appendChild(splitter);
                }
            }
        }
        body.appendChild(renderMain());

        // #299 — Unified right-side drawer. Assistant (#90) and Help
        // (#154 Phase 3) share one drawer chrome on the right edge of
        // the body. When both are open, a tab strip lets the operator
        // switch; the active tab persists across renders. When only
        // one is open, the chrome renders without tabs. This replaces
        // the old "newest open wins, the other is silently hidden"
        // behavior — both can now coexist without competing.
        var helpUsable = helpDrawerOpen && helpAvailable
            && typeof renderHelpDrawer === 'function';
        if (aiDrawerOpen || helpUsable) {
            body.classList.add('bowire-with-ai-drawer');
            body.appendChild(renderUnifiedRightDrawer({
                assistant: aiDrawerOpen,
                help: helpUsable
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

        next.appendChild(body);

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

        // #138 — Statusbar at the very bottom of the app. Hosts the
        // connection pill (moved from topbar), env selector + watch
        // button, plus future ambient indicators (hint count, save
        // state, workspace name).
        next.appendChild(renderStatusBar());

        if (typeof window.morphdom === 'function') {
            window.morphdom(app, next, {
                childrenOnly: true,
                onBeforeElUpdated: function (fromEl, toEl) {
                    // Fast-path: structurally identical subtree → skip.
                    // Big win for static toolbar/header regions that
                    // haven't changed across renders.
                    if (fromEl.isEqualNode && fromEl.isEqualNode(toEl)) {
                        return false;
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
    }

    function attachSidebarSplitterDrag() {
        var splitter = document.getElementById('bowire-sidebar-splitter');
        var app = document.getElementById('bowire-app');
        if (!splitter || !app || splitter.dataset.dragHooked === '1') return;
        splitter.dataset.dragHooked = '1';
        splitter.addEventListener('mousedown', function (e) {
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
                // left edge. getBoundingClientRect is re-read every move
                // so we stay correct under viewport changes mid-drag.
                var rect = app.getBoundingClientRect();
                var raw = Math.round(ev.clientX - rect.left - grabOffsetX);
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

        // Resolve active tab. If the sticky pick isn't currently open
        // (e.g. user closed Assistant while Help was the active tab),
        // fall back to the first open tab.
        var activeId = rightDrawerActiveTab;
        if (!tabs.find(function (t) { return t.id === activeId; })) {
            activeId = tabs[0] ? tabs[0].id : null;
        }
        var activeTab = tabs.find(function (t) { return t.id === activeId; });

        var drawer = el('div', {
            id: 'bowire-right-drawer',
            className: 'bowire-drawer bowire-right-drawer'
                + (tabs.length > 1 ? ' bowire-right-drawer-tabbed' : ''),
            role: 'complementary',
            'aria-label': activeTab ? activeTab.label : 'Drawer'
        });

        if (tabs.length > 1) {
            // Tab strip header — VS Code / JetBrains style. Each tab
            // is a button with its accessory next to the label and an
            // X on the right that closes JUST that tab (so the other
            // stays accessible).
            var tabStrip = el('div', {
                className: 'bowire-right-drawer-tabs',
                role: 'tablist'
            });
            tabs.forEach(function (t) {
                var isActive = t.id === activeId;
                var tabBtn = el('button', {
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
                tabBtn.appendChild(el('span', {
                    className: 'bowire-right-drawer-tab-close',
                    title: t.closeTitle || 'Close',
                    innerHTML: svgIcon('close'),
                    role: 'button',
                    'aria-label': t.closeTitle || 'Close',
                    onClick: function (e) {
                        e.stopPropagation();
                        if (typeof t.onClose === 'function') t.onClose();
                    }
                }));
                tabStrip.appendChild(tabBtn);
            });
            drawer.appendChild(tabStrip);
        } else if (activeTab) {
            // Single-panel mode: header mirrors the legacy drawer
            // (title + accessory + close button) so the visual stays
            // identical when only one panel is open.
            var titleRow = el('div', { className: 'bowire-drawer-title-row' },
                el('span', { className: 'bowire-drawer-title', textContent: activeTab.label }),
                activeTab.accessory || null
            );
            var header = el('div', { className: 'bowire-drawer-header' },
                titleRow,
                el('button', {
                    className: 'bowire-drawer-close',
                    title: activeTab.closeTitle || 'Close',
                    'aria-label': activeTab.closeTitle || ('Close ' + activeTab.label),
                    innerHTML: svgIcon('close'),
                    onClick: function () {
                        if (typeof activeTab.onClose === 'function') activeTab.onClose();
                    }
                })
            );
            drawer.appendChild(header);
        }

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

    // ---- AI drawer (#90) ----
    // Sits at the right edge of the workbench body next to the main pane.
    // Rendered only when aiDrawerOpen is true; the topbar toggle button
    // flips the state + persists it to localStorage. Contains the same
    // AI panel that used to live as a response-pane tab — no logic
    // duplication; we just call window.__bowireAi.renderPanel().
    function renderAiDrawer() {
        // Live status dot — three states, all carrying a hover
        // tooltip with the detail string. Connected (green) is the
        // common case; idle (grey) means no model; missing (amber)
        // means the AI package isn't installed. Lives in the drawer's
        // titleAccessory slot — same shape every drawer surface uses
        // through the renderDrawer primitive.
        var aiSt = (typeof window.__bowireAi === 'object' && window.__bowireAi)
            ? window.__bowireAi.getStatus()
            : null;
        var statusClass, statusTitle;
        if (aiSt && aiSt.hasClient) {
            statusClass = 'bowire-ai-status-dot bowire-ai-status-dot-connected';
            statusTitle = 'Connected · ' + (aiSt.providerId || 'unknown') + ' · ' + (aiSt.model || '(default model)');
        } else if (aiSt === null) {
            statusClass = 'bowire-ai-status-dot bowire-ai-status-dot-missing';
            statusTitle = 'AI package not installed — chat unavailable. Hints still work.';
        } else {
            statusClass = 'bowire-ai-status-dot bowire-ai-status-dot-idle';
            statusTitle = 'No model configured — open Settings → Assistant to connect one.';
        }
        var statusDot = el('span', {
            className: statusClass,
            title: statusTitle,
            'aria-label': statusTitle
        });

        return renderDrawer({
            id: 'bowire-ai-drawer',
            className: 'bowire-ai-drawer',
            title: 'Assistant',
            titleAccessory: statusDot,
            closeTitle: 'Close (Ctrl+Shift+A)',
            closeAriaLabel: 'Close AI drawer',
            ariaLabel: 'Assistant',
            onClose: function () {
                aiDrawerOpen = false;
                try { localStorage.setItem('bowire_ai_drawer_open', '0'); } catch { /* ignore */ }
                render();
            },
            content: function () {
                return window.__bowireAi
                    ? window.__bowireAi.renderPanel()
                    : el('p', {
                        className: 'bowire-drawer-empty',
                        textContent: 'AI module not loaded.'
                    });
            }
        });
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
        var dot = el('span', { className: 'bowire-conn-pill-dot' });
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
                            if (services[si].originUrl === url) {
                                svcCount++;
                                methodCount += (services[si].methods || []).length;
                            }
                        }
                    }

                    var row = el('div', { className: 'bowire-conn-popover-row bowire-conn-row-' + status });

                    var topRow = el('div', { className: 'bowire-conn-popover-row-top' },
                        el('span', { className: 'bowire-conn-popover-row-dot' }),
                        el('span', {
                            className: 'bowire-conn-popover-row-url',
                            textContent: truncateMiddle(url, 36),
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

    // ---- Security drawer (#111) ----
    // Mirror of renderAiDrawer for the Security surface. Same chrome
    // (header + close button + content area), different host call
    // (window.__bowireAi.renderSecurityPanel) and different toggle
    // wiring (securityDrawerOpen + bowire_security_drawer_open
    // localStorage key).
    function renderSecurityDrawer() {
        return renderDrawer({
            id: 'bowire-security-drawer',
            className: 'bowire-security-drawer',
            title: 'Security',
            closeTitle: 'Close Security drawer',
            closeAriaLabel: 'Close Security drawer',
            ariaLabel: 'Security tools',
            onClose: function () {
                securityDrawerOpen = false;
                try { localStorage.setItem('bowire_security_drawer_open', '0'); } catch { /* ignore */ }
                render();
            },
            content: function () {
                return (window.__bowireAi && typeof window.__bowireAi.renderSecurityPanel === 'function')
                    ? window.__bowireAi.renderSecurityPanel()
                    : el('p', {
                        className: 'bowire-drawer-empty',
                        textContent: 'AI module not loaded — Security panel needs Kuestenlogik.Bowire.Ai.'
                    });
            }
        });
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
    function renderTopbar() {
        var bar = el('div', { id: 'bowire-topbar', className: 'bowire-topbar' });

        // --- Brand (left column) ---
        // Theme-aware logo selection: dark theme → mono (white) SVG so
        // the brand stays readable on the dark background, light theme
        // → regular (black) SVG. Both data URLs are shipped inline by
        // BowireHtmlGenerator; getEffectiveTheme() resolves the
        // auto-mode media-query so even auto users get the right one.
        var effectiveTheme = getEffectiveTheme();
        var logoSrc = effectiveTheme === 'dark'
            ? (config.logoIconMono || config.logoIcon)
            : config.logoIcon;
        var logoIcon = logoSrc
            ? el('img', { className: 'bowire-logo-icon', src: logoSrc, alt: '' })
            : el('div', { className: 'bowire-logo-icon', textContent: (config.title || 'B').charAt(0) });
        // Wrap the logo in a 48 px column so the column anchors to
        // the activity-rail width below while the inner mark can
        // size independently. Without the wrapper the .bowire-logo-
        // icon rule fought between '48 px column' and '22 px mark'.
        var logoCol = el('div', { className: 'bowire-topbar-brand-logo-col' }, logoIcon);
        var brand = el('div', { id: 'bowire-topbar-brand', className: 'bowire-topbar-brand' },
            logoCol,
            el('div', { className: 'bowire-topbar-brand-text' },
                el('div', { className: 'bowire-logo-text', textContent: config.title }),
                config.description
                    ? el('div', { className: 'bowire-subtitle', textContent: config.description })
                    : null
            )
        );
        bar.appendChild(brand);

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
                                if (typeof s.onSelect === 'function') s.onSelect();
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
            onClick: openAbout
        }, el('span', {
            innerHTML: svgIcon('info'),
            style: 'width:16px;height:16px;display:flex'
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
            style: 'width:16px;height:16px;display:flex'
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
        var aiHintCount = 0;
        try { aiHintCount = window.__bowireAi ? window.__bowireAi.hintCount() : 0; } catch { /* ignore */ }
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

        // #116 Workspaces Phase 1 — workspace switcher chip.
        // Click opens a small menu with every workspace + a
        // 'New workspace…' action.
        var ws = activeWorkspace();
        var wsChip = el('button', {
            id: 'bowire-workspace-chip',
            type: 'button',
            className: 'bowire-workspace-chip' + (workspaceMenuOpen ? ' active' : ''),
            title: 'Workspace: ' + ws.name,
            'aria-label': 'Switch workspace',
            onClick: function (e) {
                e.stopPropagation();
                workspaceMenuOpen = !workspaceMenuOpen;
                render();
            }
        },
            el('span', { className: 'bowire-workspace-chip-dot', style: 'background:' + (ws.color || 'var(--bowire-accent)') }),
            el('span', { className: 'bowire-workspace-chip-name', textContent: ws.name }),
            el('span', { className: 'bowire-workspace-chip-caret', textContent: '▾' })
        );

        var right = el('div', { id: 'bowire-topbar-right', className: 'bowire-topbar-right' },
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
                        workspaces.map(function (w) {
                            // #146 — workspace env-count label. Shows
                            // 'all' when the workspace includes every
                            // shared env, else the explicit count
                            // from the inclusion list.
                            var envLabel = '';
                            try {
                                if (w.includeAllEnvironments) {
                                    var all = (typeof getAllSharedEnvironments === 'function') ? getAllSharedEnvironments() : [];
                                    envLabel = 'all (' + all.length + ')';
                                } else {
                                    var n = Array.isArray(w.includedEnvironmentIds) ? w.includedEnvironmentIds.length : 0;
                                    envLabel = n + ' env' + (n === 1 ? '' : 's');
                                }
                            } catch { /* ignore */ }
                            return el('div', {
                                className: 'bowire-workspace-menu-item' + (w.id === activeWorkspaceId ? ' active' : ''),
                                onClick: function () {
                                    if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                                    workspaceMenuOpen = false;
                                    render();
                                }
                            },
                                el('span', { className: 'bowire-workspace-menu-item-dot', style: 'background:' + (w.color || 'var(--bowire-accent)') }),
                                el('span', { className: 'bowire-workspace-menu-item-name', textContent: w.name }),
                                el('span', { className: 'bowire-workspace-menu-item-envcount', textContent: envLabel }),
                                w.id === activeWorkspaceId
                                    ? el('span', { className: 'bowire-workspace-menu-item-check', textContent: '✓' })
                                    : null
                            );
                        })
                    ),
                    el('div', { className: 'bowire-workspace-menu-divider' }),
                    el('div', {
                        className: 'bowire-workspace-menu-item bowire-workspace-menu-item-action',
                        onClick: function () {
                            workspaceMenuOpen = false;
                            render();
                            bowirePrompt('New workspace name', {
                                title: 'Create workspace',
                                placeholder: 'e.g. Payments — staging',
                                confirmText: 'Create',
                            }).then(function (name) {
                                if (name) {
                                    createWorkspace(name);
                                    render();
                                }
                            });
                        }
                    },
                        el('span', { className: 'bowire-workspace-menu-item-icon', textContent: '+' }),
                        el('span', { textContent: 'New workspace…' })
                    ),
                    el('div', {
                        className: 'bowire-workspace-menu-item bowire-workspace-menu-item-action',
                        onClick: function () {
                            var oldName = ws.name;
                            var wsId = ws.id;
                            workspaceMenuOpen = false;
                            render();
                            bowirePrompt('Rename workspace', {
                                title: 'Rename',
                                defaultValue: oldName,
                                confirmText: 'Rename',
                            }).then(function (renamed) {
                                if (renamed) {
                                    renameWorkspace(wsId, renamed);
                                    render();
                                }
                            });
                        }
                    },
                        el('span', { className: 'bowire-workspace-menu-item-icon', textContent: '✎' }),
                        el('span', { textContent: 'Rename current…' })
                    ),
                    workspaces.length > 1 ? el('div', {
                        className: 'bowire-workspace-menu-item bowire-workspace-menu-item-action bowire-workspace-menu-item-danger',
                        onClick: function () {
                            var wsName = ws.name;
                            var wsId = ws.id;
                            workspaceMenuOpen = false;
                            render();
                            bowireConfirm(
                                'Delete workspace "' + wsName + '"? The underlying URLs / envs / recordings for this workspace are removed.',
                                function () { deleteWorkspace(wsId); render(); },
                                { title: 'Delete workspace', confirmText: 'Delete', danger: true }
                            );
                        }
                    },
                        el('span', { className: 'bowire-workspace-menu-item-icon', textContent: '🗑' }),
                        el('span', { textContent: 'Delete current' })
                    ) : null
                ) : null,
                renderEnvSelector(),
            ),
            // Drawer + utility group. Security toggle retired in
            // #133 Phase 2 (Security is a rail mode now, not a
            // drawer). Assistant stays as a drawer because it's
            // cross-cutting. Omnibox search trigger sits at the front
            // so the eye lands on 'search' before the drawers. Theme
            // / Help / About promoted out of the old ⋮ overflow
            // (#292) — Settings stays at the activity-rail bottom
            // per VS Code / JetBrains convention.
            el('div', { className: 'bowire-topbar-group bowire-topbar-drawers' },
                omniboxTriggerBtn,
                aiToggleBtn,
                themeBtn,
                helpBtn,
                aboutBtn
            )
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
                        icon: svgIcon('recording'),
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
        // rail. Substring match on mode label + id.
        var railJumps = [
            { id: 'home',         label: 'Home',              icon: 'house' },
            { id: 'discover',     label: 'Discover',          icon: 'compass' },
            { id: 'collections',  label: 'Collections',       icon: 'list' },
            { id: 'environments', label: 'Environments',      icon: 'globe' },
            { id: 'recordings',   label: 'Recordings',        icon: 'recording' },
            { id: 'mocks',        label: 'Mocks',             icon: 'server' },
            { id: 'flows',        label: 'Flows',             icon: 'flow' },
            { id: 'proxy',        label: 'Proxy / MITM',      icon: 'disconnect' },
            { id: 'benchmarks',   label: 'Benchmarks',        icon: 'chart' },
            { id: 'parallel',     label: 'Parallel sessions', icon: 'lightning' },
            { id: 'security',     label: 'Security',          icon: 'shield' },
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

        return out;
    }

    // ---- Environments UI ----

    function renderEnvSelector() {
        var bar = el('div', { id: 'bowire-sidebar-env-bar', className: 'bowire-env-bar' });

        var envs = getEnvironments();
        var activeId = getActiveEnvId();
        var activeEnv = envs.find(function (e) { return e.id === activeId; });
        // Custom env dropdown with color dots
        var activeColor = activeEnv && activeEnv.color ? activeEnv.color : '';
        var envBtnWrapper = el('div', { className: 'bowire-env-dropdown-wrapper' });
        var envBtn = el('button', {
            id: 'bowire-env-dropdown-btn',
            className: 'bowire-env-dropdown-btn',
            title: 'Active environment — click to switch',
            onClick: function (e) {
                e.stopPropagation();
                var existing = envBtnWrapper.querySelector('.bowire-env-dropdown-menu');
                if (existing) { existing.remove(); return; }
                var menu = el('div', { className: 'bowire-env-dropdown-menu' });
                menu.appendChild(el('div', {
                    className: 'bowire-env-dropdown-item' + (!activeId ? ' active' : ''),
                    onClick: function () { setActiveEnvId(''); menu.remove(); render(); }
                },
                    el('span', { className: 'bowire-env-color-dot', style: 'background:#666' }),
                    el('span', { textContent: 'No environment' })
                ));
                for (var di = 0; di < envs.length; di++) {
                    (function (env) {
                        menu.appendChild(el('div', {
                            className: 'bowire-env-dropdown-item' + (env.id === activeId ? ' active' : ''),
                            onClick: function () { setActiveEnvId(env.id); menu.remove(); render(); }
                        },
                            el('span', { className: 'bowire-env-color-dot', style: 'background:' + (env.color || '#6366f1') }),
                            el('span', { textContent: env.name })
                        ));
                    })(envs[di]);
                }
                envBtnWrapper.appendChild(menu);
                setTimeout(function () {
                    document.addEventListener('click', function h() { menu.remove(); document.removeEventListener('click', h); }, { once: true });
                }, 0);
            }
        },
            activeColor ? el('span', { className: 'bowire-env-color-dot', style: 'background:' + activeColor }) : el('span', { className: 'bowire-env-label', innerHTML: svgIcon('globe') }),
            el('span', { className: 'bowire-env-btn-text', textContent: activeEnv ? activeEnv.name : 'No environment' }),
            el('span', { className: 'bowire-env-btn-chevron', textContent: '\u25BE' })
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

