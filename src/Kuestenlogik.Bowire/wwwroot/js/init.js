    // ---- Init ----
    function init() {
        // #115 — check the app-version marker first so a major-bump
        // cosmetic reset (drawer-open / split-mode / rail-mode / …)
        // lands BEFORE any rendering reads those keys. Data and user
        // settings (theme, watch-interval) are untouched; see the
        // checkAppVersionMarker contract in prologue.js for the list.
        try { checkAppVersionMarker(); }
        catch (e) { console.warn('[bowire] version-marker check failed', e); }

        // Apply stored theme preference (or auto → OS) immediately so
        // the first paint is correct. Replaces the old
        // `setAttribute('data-theme', config.theme)` hard-wire, which
        // ignored the saved user preference and didn't track OS theme
        // changes.
        applyTheme();

        // Keep Bowire in sync with OS dark-mode toggles while the
        // preference is 'auto'. When the user has explicitly pinned
        // dark or light, the OS change is ignored (matches typical
        // web-app expectations).
        try {
            var mq = window.matchMedia('(prefers-color-scheme: dark)');
            var handleMqChange = function () {
                if (themePreference === 'auto') {
                    applyTheme();
                    render();
                }
            };
            if (typeof mq.addEventListener === 'function') {
                mq.addEventListener('change', handleMqChange);
            } else if (typeof mq.addListener === 'function') {
                // Safari < 14 fallback
                mq.addListener(handleMqChange);
            }
        } catch { /* matchMedia not available — skip live tracking */ }

        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
                e.preventDefault();
                handleExecute();
                return;
            }

            // #154 Phase 3 — F1 opens the Help drawer at the topic
            // most relevant to the current rail mode. Esc closes it
            // when it's open and nothing else is competing for Esc.
            // Both no-ops when the Help package isn't installed
            // (helpAvailable === false from the boot probe).
            if (e.key === 'F1' && helpAvailable && typeof helpOpenDrawer === 'function') {
                e.preventDefault();
                helpOpenDrawer();
                return;
            }
            if (e.key === 'Escape' && helpDrawerOpen
                && typeof helpCloseDrawer === 'function'
                && !searchSuggestionsOpen) {
                e.preventDefault();
                helpCloseDrawer();
                return;
            }

            // #127 — Cmd/Ctrl+S Force-Flush. Writes every persist*()
            // slot in one sweep so the operator can pin the current
            // state without waiting for the throttled autosave path.
            // The browser's native Cmd+S (save page as) is intercepted;
            // the workbench surface is dynamic and 'save page' has no
            // useful semantics here anyway.
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey
                && (e.key === 's' || e.key === 'S')) {
                e.preventDefault();
                flushAllPersists();
                return;
            }

            // #124 Cmd/Ctrl+K — open the command palette omnibox.
            // Same input as the topbar palette; this shortcut focuses
            // it from anywhere and pops the suggestion dropdown so
            // operators can search methods, recordings, mocks, and
            // rail-mode jumps without reaching for the mouse.
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey
                && (e.key === 'k' || e.key === 'K')) {
                e.preventDefault();
                searchSuggestionsOpen = true;
                searchSuggestionIndex = 0;
                render();
                requestAnimationFrame(function () {
                    var input = document.getElementById('bowire-command-palette-input');
                    if (input) {
                        input.focus();
                        input.select();
                    }
                });
                return;
            }

            // Ctrl/Cmd+Shift+A — toggle the AI drawer (#90). Shift is in
            // the chord deliberately: Ctrl+A is "select all" inside text
            // inputs and we don't want to steal it.
            if ((e.ctrlKey || e.metaKey) && e.shiftKey
                && (e.key === 'A' || e.key === 'a')) {
                e.preventDefault();
                aiDrawerOpen = !aiDrawerOpen;
                try { localStorage.setItem('bowire_ai_drawer_open', aiDrawerOpen ? '1' : '0'); } catch { /* ignore */ }
                render();
                return;
            }

            // Ctrl/Cmd+Alt+\ — toggle the request/response split
            // orientation (#135). Browser-style: '\' lives on the
            // same key as '|' on US/EU keyboards, mirrors the
            // 'split editor' chord most editors use.
            if ((e.ctrlKey || e.metaKey) && e.altKey && e.key === '\\') {
                e.preventDefault();
                splitMode = splitMode === 'horizontal' ? 'vertical' : 'horizontal';
                try { localStorage.setItem('bowire_split_mode', splitMode); } catch { /* ignore */ }
                render();
                return;
            }

            // #143 Phase 3 — Del / Backspace on a list sidebar with
            // a non-empty selection moves the selection to trash.
            // Falls back to the row that the rail-mode list has
            // visually selected (recordingManagerSelectedId etc.)
            // when the multi-select set is empty. Skip when typing
            // in a text field so the chord doesn't intercept real
            // editing.
            var inTextForDel = document.activeElement
                && (/^(INPUT|TEXTAREA|SELECT)$/i.test(document.activeElement.tagName)
                    || document.activeElement.isContentEditable);
            if (!inTextForDel && (e.key === 'Delete' || e.key === 'Backspace')) {
                if (railMode === 'recordings' && typeof recordingsSelected !== 'undefined') {
                    var rIds = recordingsSelected.size > 0
                        ? Array.from(recordingsSelected)
                        : (recordingManagerSelectedId ? [recordingManagerSelectedId] : []);
                    if (rIds.length > 0) {
                        e.preventDefault();
                        var rRemoved = [];
                        rIds.forEach(function (id) {
                            var idx = recordingsList.findIndex(function (r) { return r.id === id; });
                            if (idx < 0) return;
                            rRemoved.push({ entry: recordingsList[idx], originalIdx: idx, deletedAt: Date.now() });
                            recordingsList.splice(idx, 1);
                            if (recordingManagerSelectedId === id) recordingManagerSelectedId = null;
                            if (recordingActiveId === id) recordingActiveId = null;
                        });
                        for (var rk = rRemoved.length - 1; rk >= 0; rk--) recordingsTrash.unshift(rRemoved[rk]);
                        recordingsSelected.clear();
                        recordingsSelectionAnchor = null;
                        persistRecordings();
                        persistRecordingsTrash();
                        toast(rRemoved.length + ' recording' + (rRemoved.length === 1 ? '' : 's') + ' moved to trash', 'success');
                        render();
                        return;
                    }
                }
                if (railMode === 'collections' && typeof collectionsSelected !== 'undefined') {
                    var cIds = collectionsSelected.size > 0
                        ? Array.from(collectionsSelected)
                        : (collectionManagerSelectedId ? [collectionManagerSelectedId] : []);
                    if (cIds.length > 0) {
                        e.preventDefault();
                        var cRemoved = [];
                        cIds.forEach(function (id) {
                            var idx = collectionsList.findIndex(function (c) { return c.id === id; });
                            if (idx < 0) return;
                            cRemoved.push({ entry: collectionsList[idx], originalIdx: idx, deletedAt: Date.now() });
                            collectionsList.splice(idx, 1);
                            if (collectionManagerSelectedId === id) collectionManagerSelectedId = null;
                        });
                        for (var ck = cRemoved.length - 1; ck >= 0; ck--) collectionsTrash.unshift(cRemoved[ck]);
                        collectionsSelected.clear();
                        collectionsSelectionAnchor = null;
                        persistCollections();
                        persistCollectionsTrash();
                        toast(cRemoved.length + ' collection' + (cRemoved.length === 1 ? '' : 's') + ' moved to trash', 'success');
                        render();
                        return;
                    }
                }
            }

            // Ctrl/Cmd+Shift+S — toggle the Security drawer (#111).
            // Paired with the AI shortcut for muscle-memory consistency.
            if ((e.ctrlKey || e.metaKey) && e.shiftKey
                && (e.key === 'S' || e.key === 's')) {
                e.preventDefault();
                securityDrawerOpen = !securityDrawerOpen;
                try { localStorage.setItem('bowire_security_drawer_open', securityDrawerOpen ? '1' : '0'); } catch { /* ignore */ }
                render();
                return;
            }

            // #123 — tab keyboard shortcuts. Ignored while the user is
            // typing in an input / textarea / contenteditable so the
            // chords don't intercept normal text editing.
            var inText = document.activeElement
                && (/^(INPUT|TEXTAREA|SELECT)$/i.test(document.activeElement.tagName)
                    || document.activeElement.isContentEditable);
            // Ctrl/Cmd+T — new (freeform) tab. T alone is browser
            // 'new tab'; Shift+ makes it ours without breaking the
            // browser shortcut for users who want it.
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey
                && (e.key === 't' || e.key === 'T')
                && !inText) {
                e.preventDefault();
                startFreeformRequest();
                return;
            }
            // Ctrl/Cmd+W — close the active tab. Skip when no tabs
            // are open so the browser's close-window stays available.
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey
                && (e.key === 'w' || e.key === 'W')
                && requestTabs.length > 0 && activeTabId !== null) {
                e.preventDefault();
                closeTab(activeTabId);
                return;
            }
            // Ctrl+Tab / Ctrl+Shift+Tab — cycle tabs.
            if (e.ctrlKey && !e.metaKey && !e.altKey && e.key === 'Tab'
                && requestTabs.length >= 2) {
                e.preventDefault();
                var idx = -1;
                for (var ti = 0; ti < requestTabs.length; ti++) {
                    if (requestTabs[ti].id === activeTabId) { idx = ti; break; }
                }
                if (idx < 0) idx = 0;
                var nextIdx = e.shiftKey
                    ? (idx - 1 + requestTabs.length) % requestTabs.length
                    : (idx + 1) % requestTabs.length;
                switchTab(requestTabs[nextIdx].id);
                return;
            }
            // Ctrl+1..9 — jump to the Nth tab.
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey
                && /^[1-9]$/.test(e.key) && !inText) {
                var pos = parseInt(e.key, 10) - 1;
                if (pos < requestTabs.length) {
                    e.preventDefault();
                    switchTab(requestTabs[pos].id);
                    return;
                }
            }

            // Esc: close overlay, stop streaming, or disconnect channel
            if (e.key === 'Escape') {
                if (searchSuggestionsOpen) {
                    searchSuggestionsOpen = false;
                    searchQuery = '';
                    render();
                    return;
                }
                if (topbarOverflowOpen) {
                    topbarOverflowOpen = false;
                    render();
                    return;
                }
                if (workspaceMenuOpen) {
                    workspaceMenuOpen = false;
                    render();
                    return;
                }
                // #143 Phase 3 — Esc clears any in-progress
                // multi-select on the active list sidebar.
                if ((typeof sourcesSelected !== 'undefined' && sourcesSelected.size > 0)) {
                    sourcesSelected.clear();
                    sourcesSelectionAnchor = null;
                    render();
                    return;
                }
                if ((typeof recordingsSelected !== 'undefined' && recordingsSelected.size > 0)
                    || (typeof collectionsSelected !== 'undefined' && collectionsSelected.size > 0)) {
                    if (typeof recordingsSelected !== 'undefined') {
                        recordingsSelected.clear();
                        recordingsSelectionAnchor = null;
                    }
                    if (typeof collectionsSelected !== 'undefined') {
                        collectionsSelected.clear();
                        collectionsSelectionAnchor = null;
                    }
                    render();
                    return;
                }
                if (aboutOpen) {
                    closeAbout();
                    return;
                }
                if (settingsOpen) {
                    closeSettings();
                    return;
                }
                // recordingManagerOpen / collectionManagerOpen /
                // mocksManagerOpen modal branches dropped in
                // #133 Phase 3 — those surfaces are rail modes.
                // Esc on a rail mode falls through to the default
                // (no-op or the streaming/channel branches below).
                // envManagerOpen modal removed — envs are inline now
                if (showShortcutsOverlay) {
                    showShortcutsOverlay = false;
                    renderShortcutsOverlay();
                    return;
                }
                // Restore a maximized widget pane (map widget, future
                // viewers) before stop-streaming so a user mashing Esc
                // to "make this small again" doesn't accidentally kill
                // the live stream too. CSS toggle only, no render() —
                // see the maximize-button click handler in render-main.js
                // for why a full render would dispose the live widget.
                if (widgetPaneMaximized) {
                    widgetPaneMaximized = false;
                    var maxPane = document.querySelector('.bowire-widget-pane');
                    if (maxPane) maxPane.classList.remove('is-maximized');
                    var maxBtn = document.querySelector('.bowire-widget-pane-maximize');
                    if (maxBtn) {
                        // Same SVG path bowireLayoutIcon('maximize')
                        // produces — inlined because the icon helper
                        // lives in render-main.js's IIFE and isn't
                        // reachable from here.
                        maxBtn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
                            + '<polyline points="4 9 4 4 9 4"/>'
                            + '<polyline points="20 9 20 4 15 4"/>'
                            + '<polyline points="20 15 20 20 15 20"/>'
                            + '<polyline points="4 15 4 20 9 20"/>'
                            + '</svg>';
                        maxBtn.title = 'Maximize widget to fill the window';
                    }
                    return;
                }
                if (isExecuting && sseSource) {
                    stopStreaming();
                    render();
                    return;
                }
                if (duplexConnected) {
                    channelDisconnect();
                    return;
                }
                return;
            }

            // Skip single-key shortcuts when focused on input or textarea
            var tag = document.activeElement && document.activeElement.tagName;
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;

            if (e.key === '?') {
                e.preventDefault();
                toggleShortcutsOverlay();
                return;
            }

            if (e.key === '/') {
                e.preventDefault();
                // The search lives in the topbar command palette now.
                // Fall back to the old class selector in case any
                // embedded host still has the legacy sidebar search
                // markup somewhere.
                var searchInput = document.getElementById('bowire-command-palette-input')
                    || $('.bowire-search-input');
                if (searchInput) searchInput.focus();
                return;
            }

            if (e.key === 't') {
                e.preventDefault();
                // Cycle through the same three states as the click on
                // the theme toggle button: auto → dark → light → auto.
                // Goes through setThemePreference so localStorage and
                // the CSS data-theme attribute stay in sync.
                var curPref = themePreference;
                var nextPref = curPref === 'auto' ? 'dark'
                             : curPref === 'dark' ? 'light'
                             : 'auto';
                setThemePreference(nextPref);
                render();
                return;
            }

            if (e.key === 'f') {
                e.preventDefault();
                if (requestInputMode === 'form') {
                    syncFormToJson();
                    requestInputMode = 'json';
                } else {
                    syncJsonToForm();
                    requestInputMode = 'form';
                }
                render();
                return;
            }

            if (e.key === 'r') {
                e.preventDefault();
                repeatLastCall();
                return;
            }

            // Vim-style method navigation through the sidebar's flat
            // visible list. j moves forward, k moves backward, both
            // wrap. Skipped automatically when focus is in an input
            // (covered by the early-return at the top of this block)
            // so typing j or k into the search field still works.
            if (e.key === 'j' || e.key === 'J') {
                e.preventDefault();
                navigateMethodSequence(1);
                return;
            }
            if (e.key === 'k' || e.key === 'K') {
                e.preventDefault();
                navigateMethodSequence(-1);
                return;
            }
        });

        // Clean up open channels on page navigation
        window.addEventListener('beforeunload', function () {
            if (duplexConnected) channelDisconnect();
        });

        // Global outside-click handler for popups that don't need to
        // self-close: protocol filter popup and command-palette
        // suggestions dropdown. Both use stopPropagation inside their
        // own item handlers, so any click that reaches document is by
        // definition outside. The command palette also closes on
        // clicks outside the palette wrapper, which lets the user
        // dismiss the dropdown by clicking anywhere in the main pane.
        document.addEventListener('click', function (e) {
            var changed = false;
            if (protocolFilterOpen) {
                protocolFilterOpen = false;
                changed = true;
            }
            if (searchSuggestionsOpen) {
                // Keep the dropdown open when the user is clicking
                // inside the command-palette wrapper (e.g. the input
                // itself or the dropdown items).
                var palette = document.getElementById('bowire-topbar-palette');
                if (!palette || !palette.contains(e.target)) {
                    searchSuggestionsOpen = false;
                    changed = true;
                }
            }
            if (topbarOverflowOpen) {
                // Same idea: clicks inside the overflow button or its
                // menu keep it open; clicks anywhere else close it.
                var ovBtn = document.getElementById('bowire-topbar-overflow');
                var ovMenu = document.querySelector('.bowire-topbar-overflow-menu');
                var inside = (ovBtn && ovBtn.contains(e.target)) || (ovMenu && ovMenu.contains(e.target));
                if (!inside) {
                    topbarOverflowOpen = false;
                    changed = true;
                }
            }
            if (workspaceMenuOpen) {
                var wsBtn = document.getElementById('bowire-workspace-chip');
                var wsMenu = document.querySelector('.bowire-workspace-menu');
                var wsInside = (wsBtn && wsBtn.contains(e.target)) || (wsMenu && wsMenu.contains(e.target));
                if (!wsInside) {
                    workspaceMenuOpen = false;
                    changed = true;
                }
            }
            if (changed) render();
        });

        // Load environments + recordings from disk in parallel so the env
        // selector and recordings list both reflect ~/.bowire/* on first
        // render. Both have an in-memory fall-back from localStorage if the
        // disk fetch fails.
        // Load collections from disk too (#30) -- localStorage stays the
        // in-flight cache, the GET below promotes the canonical disk
        // state into it when present.
        collectionsList = loadCollections();
        loadCollectionsFromDisk().then(function () { render(); });

        // Load flows from localStorage
        flowsList = loadFlows();

        // Phase 3-R — kick off the external-extension bootstrap. Each
        // discovered extension's JS bundle + optional stylesheet
        // dynamic-loads in parallel; the framework's
        // `mountWidgetsForMethod` path waits on this implicitly through
        // the registration cache (a registered extension is mountable;
        // an unregistered one renders the placeholder card). We don't
        // block the first render on it — extensions are an enhancement,
        // and the workbench's core surface (sidebar, request form, …)
        // ships work without them.
        if (window.__bowireExtFramework
            && typeof window.__bowireExtFramework.loadExternalExtensions === 'function') {
            try { window.__bowireExtFramework.loadExternalExtensions(); }
            catch (e) { console.warn('[bowire-ext] bootstrap failed', e); }
        }

        // #127 — capability probe for the workspace-folder open
        // action. Embedded hosts get { available: false } back so the
        // save-pill click is gated off (the host is typically a
        // production server). Single fire-and-forget GET; failure
        // leaves canOpenWorkspaceFolder at its boot default of false.
        fetch(config.prefix + '/api/workspace/can-open-folder')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && data.available) {
                    canOpenWorkspaceFolder = true;
                    render();
                }
            })
            .catch(function () { /* leave false */ });

        // #154 Phase 1 — in-app help capability probe. Returns true
        // when an IBowireHelpProvider is registered (i.e. the
        // Kuestenlogik.Bowire.Help package is installed). When
        // false, Help affordances render disabled with an install
        // hint — see render-env-auth.js topbar overflow.
        fetch(config.prefix + '/api/help/available')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && data.available) {
                    helpAvailable = true;
                    render();
                }
            })
            .catch(function () { /* leave false */ });

        // #125 Phase 2 — vars-autocomplete dropdown installs once
        // at boot; sits on document-level listeners so it covers
        // every dynamically-mounted text input/textarea without
        // per-field wiring.
        try { installVarsAutocomplete(); } catch (e) { console.warn('[vars-ac] install failed', e); }
        // #125 Phase 2 v2 — chip overlay attaches lazily on focus
        // so dynamically-mounted forms get coverage without
        // per-field wiring. Skip eligible fields by setting
        // data-bowire-no-vars-chip="1".
        try { installVarsChips(); } catch (e) { console.warn('[vars-chips] install failed', e); }

        Promise.allSettled([
            loadEnvironmentsFromDisk(),
            loadRecordingsFromDisk()
        ]).finally(function () {
            render();

            // #145 Phase 1 — once recordings + envs are hydrated,
            // scan the active workspace for legacy ${name} usage and
            // surface the soft-deprecation toast (sticky; user picks
            // Snooze or Dismiss). Snooze marker is workspace-scoped,
            // so switching workspaces re-checks per-workspace.
            try { showLegacyVarsToastIfNeeded(); }
            catch (e) { console.warn('[vars-deprecation] toast failed', e); }

            // Reveal the app and fade out the loading overlay. The
            // overlay lives in the static HTML so it's visible from
            // the very first paint — no FOUC. We remove it after a
            // short transition so the spinner doesn't linger.
            requestAnimationFrame(function () {
                var appEl = document.getElementById('bowire-app');
                if (appEl) appEl.classList.add('bowire-app-ready');
                var loading = document.getElementById('bowire-loading');
                if (loading) {
                    loading.style.opacity = '0';
                    setTimeout(function () { loading.remove(); }, 300);
                }
            });

            fetchServices();
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
