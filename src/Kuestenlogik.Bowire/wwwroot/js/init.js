    // ---- Init ----
    function init() {
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

            // Esc: close overlay, stop streaming, or disconnect channel
            if (e.key === 'Escape') {
                if (aboutOpen) {
                    closeAbout();
                    return;
                }
                if (settingsOpen) {
                    closeSettings();
                    return;
                }
                if (recordingManagerOpen) {
                    recordingManagerOpen = false;
                    renderRecordingManager();
                    return;
                }
                // envManagerOpen modal removed — envs are inline now
                if (showShortcutsOverlay) {
                    showShortcutsOverlay = false;
                    renderShortcutsOverlay();
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
            if (changed) render();
        });

        // Load environments + recordings from disk in parallel so the env
        // selector and recordings list both reflect ~/.bowire/* on first
        // render. Both have an in-memory fall-back from localStorage if the
        // disk fetch fails.
        // Load collections from localStorage on boot
        collectionsList = loadCollections();

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

        Promise.allSettled([
            loadEnvironmentsFromDisk(),
            loadRecordingsFromDisk()
        ]).finally(function () {
            render();

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
