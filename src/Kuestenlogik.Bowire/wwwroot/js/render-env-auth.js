    // ---- Render ----
    function isMobile() {
        return window.innerWidth <= 600;
    }

    function render() {
        const app = document.getElementById('bowire-app');
        if (!app) return;

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

        body.appendChild(renderSidebar());
        // Sidebar-resize splitter lives between the sidebar and the main
        // pane. Hidden on mobile (sidebar is an overlay there, no split
        // layout) and when the sidebar is collapsed (CSS hides it via the
        // adjacent-sibling rule).
        if (!isMobile()) {
            body.appendChild(el('div', {
                className: 'bowire-sidebar-splitter',
                id: 'bowire-sidebar-splitter',
                title: 'Drag to resize the sidebar'
            }));
        }
        body.appendChild(renderMain());

        next.appendChild(body);

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
            e.preventDefault();
            e.stopPropagation();
            document.body.style.cursor = 'ew-resize';
            document.body.style.userSelect = 'none';
            app.classList.add('is-resizing');

            function onMove(ev) {
                // Compute sidebar width relative to the app container's
                // left edge. getBoundingClientRect is re-read every move
                // so we stay correct under viewport changes mid-drag.
                var rect = app.getBoundingClientRect();
                var w = Math.round(ev.clientX - rect.left);
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
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                app.classList.remove('is-resizing');
                try { localStorage.setItem(SIDEBAR_WIDTH_KEY, String(sidebarWidth)); } catch { /* ignore */ }
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
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
        var brand = el('div', { id: 'bowire-topbar-brand', className: 'bowire-topbar-brand' },
            logoIcon,
            el('div', { className: 'bowire-topbar-brand-text' },
                el('div', { className: 'bowire-logo-text', textContent: config.title }),
                config.description
                    ? el('div', { className: 'bowire-subtitle', textContent: config.description })
                    : null
            )
        );
        bar.appendChild(brand);

        // --- Command palette (center column) ---
        var paletteWrap = el('div', { id: 'bowire-topbar-palette', className: 'bowire-topbar-palette' });

        var searchInputEl = el('input', {
            id: 'bowire-command-palette-input',
            className: 'bowire-command-palette-input',
            type: 'text',
            placeholder: 'Search methods, filters, protocols, environments \u2026',
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

        bar.appendChild(paletteWrap);

        // --- Right controls: env + theme ---
        // Schema Watch toggle
        var watchBtn = el('button', {
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

        // About button — pops the standalone About dialog (version,
        // open-source notices, Küstenlogik credit). Sits between the
        // theme toggle and Settings: the gear is for changing config,
        // the question mark is for "what is this thing".
        //
        // Renders a literal "?" glyph rather than the lucide help-circle
        // SVG: the icon's curved path reads as a generic scribble at
        // 16px, the typographic question mark is always recognisable
        // because the user's font already has it.
        var aboutBtn = el('button', {
            id: 'bowire-about-btn',
            className: 'bowire-theme-toggle-btn bowire-about-btn',
            title: 'About Bowire',
            'aria-label': 'About Bowire',
            onClick: openAbout
        }, el('span', {
            className: 'bowire-about-btn-glyph',
            textContent: '?'
        }));

        // Settings button
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

        var right = el('div', { id: 'bowire-topbar-right', className: 'bowire-topbar-right' },
            renderEnvSelector(),
            watchBtn,
            renderThemeToggle(),
            aboutBtn,
            settingsBtn
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

        var capitalPref = pref.charAt(0).toUpperCase() + pref.slice(1);
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
            }),
            el('span', {
                className: 'bowire-theme-toggle-label',
                textContent: capitalPref
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

    // Builds the list of suggestions shown under the command palette.
    // Order: Methods > Filters > Protocols > Environments. Each
    // suggestion carries an onSelect callback that does the work.
    function buildSearchSuggestions(query) {
        var q = (query || '').trim();
        if (!q) return [];
        var qLower = q.toLowerCase();
        var out = [];

        // --- Methods (top 8) ---
        var methodMatches = [];
        for (var si = 0; si < services.length; si++) {
            var svc = services[si];
            if (!svc.methods) continue;
            for (var mi = 0; mi < svc.methods.length; mi++) {
                var m = svc.methods[mi];
                var hay = (
                    (m.name || '') + ' ' +
                    (m.fullName || '') + ' ' +
                    (svc.name || '') + ' ' +
                    (m.description || '') + ' ' +
                    (m.summary || '') + ' ' +
                    (m.httpPath || '')
                ).toLowerCase();
                if (hay.indexOf(qLower) !== -1) {
                    methodMatches.push({ svc: svc, method: m });
                    if (methodMatches.length >= 8) break;
                }
            }
            if (methodMatches.length >= 8) break;
        }
        for (var mmi = 0; mmi < methodMatches.length; mmi++) {
            (function (mm) {
                var proto = protocols.find(function (p) { return p.id === mm.svc.source; });
                out.push({
                    group: 'Methods',
                    // Label = just the method name. The service goes on
                    // the right as a sublabel — users asked for this so
                    // the scan reads as "method ← service" instead of
                    // duplicating the service name in two places.
                    label: mm.method.name,
                    sublabel: mm.svc.name,
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
                renderEnvManager();
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
                    renderEnvManager(); // refresh: secret label / textarea changes
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

