    // ---- Execute Handler ----
    async function handleExecute() {
        if (!selectedMethod || !selectedService) return;

        // If there's a leftover channel from a previous Duplex method
        // and the current method is NOT a channel type, disconnect it
        // so the Unary/Streaming path runs cleanly.
        if (duplexConnected && !isChannelMethod()) {
            channelDisconnect();
        }

        // Channel methods (Duplex / ClientStreaming) use the channel flow
        if (isChannelMethod()) {
            if (duplexConnected) {
                // When connected, Ctrl+Enter sends a message
                channelSend();
            } else {
                channelConnect();
            }
            return;
        }

        if (isExecuting && sseSource) {
            stopStreaming();
            return;
        }

        // Server-streaming + active subscription for THIS method →
        // Ctrl+Enter stops the subscription. The registry survives
        // across tab switches even when `sseSource` is the wrong
        // tab's slot, so the check above doesn't fire.
        if (selectedMethod && selectedMethod.serverStreaming
            && findSubscription(selectedService.name, selectedMethod.name)) {
            stopSubscriptionFor(selectedService.name, selectedMethod.name);
            return;
        }

        // Collect all message editor values into requestMessages
        if (requestInputMode === 'form' && selectedMethod && selectedMethod.inputType) {
            // Validate the form before serialising — required fields, numeric
            // types, integer-vs-float. Block submission with a toast and
            // visible per-field error markers when anything fails so the
            // user gets immediate feedback instead of a 4xx/5xx round-trip.
            var validationErrors = validateForm(selectedMethod.inputType, '');
            if (Object.keys(validationErrors).length > 0) {
                formValidationErrors = validationErrors;
                var count = Object.keys(validationErrors).length;
                toast(count + (count === 1 ? ' field has' : ' fields have') + ' validation errors', 'error');
                render();
                return;
            }
            // Validation passed — clear any leftover markers from a previous
            // failed attempt so the form looks clean again.
            formValidationErrors = {};
            syncFormToJson();
        } else {
            const editors = $$('.bowire-message-editor');
            if (editors.length > 0) {
                requestMessages = editors.map(function (e) { return e.value || '{}'; });
            } else {
                // Fallback: single editor mode
                const editor = $('.bowire-editor');
                requestMessages = [editor ? editor.value : '{}'];
            }
        }
        // #125 Phase 4 — prefetch ai.* refs from every template the
        // upcoming substitution will touch (body + metadata snapshots
        // + URL). The prefetch is no-op when nothing matches; when
        // there are matches it awaits the AI calls and populates the
        // session cache that substituteVars reads from.
        if (typeof window.bowirePrefetchAiVars === 'function') {
            try {
                var aiTemplates = requestMessages.slice();
                // metadata values + URL also pass through substituteVars,
                // so include them in the scan.
                var mdRowsAi = $$('.bowire-metadata-row');
                for (var mdi = 0; mdi < mdRowsAi.length; mdi++) {
                    var mdInputs = mdRowsAi[mdi].querySelectorAll('.bowire-metadata-input');
                    if (mdInputs.length === 2 && mdInputs[1].value) {
                        aiTemplates.push(mdInputs[1].value);
                    }
                }
                if (selectedService && selectedService.originUrl) {
                    aiTemplates.push(selectedService.originUrl);
                }
                await window.bowirePrefetchAiVars(aiTemplates);
            } catch (e) { console.warn('[ai-prefetch] failed', e); }
        }

        // Substitute ${var} placeholders from active environment + globals
        let messages = substituteMessages(
            requestMessages.map(function (m) { return m || '{}'; })
        );

        // GraphQL: wrap variables + (auto-generated or user-edited) operation
        // string into the standard { query, variables } request shape so the
        // server forwards the query verbatim. Substitution already ran on the
        // raw variables, so we wrap after — but the query itself is not
        // substituted (it shouldn't carry secrets, and braces collide).
        if (isGraphQLMethod() && messages.length > 0) {
            messages = [wrapAsGraphQLRequest(messages[0])];
        }

        // Collect metadata (substitute values, leave keys literal)
        const metadataRows = $$('.bowire-metadata-row');
        let metadata = {};
        for (const row of metadataRows) {
            const inputs = row.querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                metadata[inputs[0].value.trim()] = substituteVars(inputs[1].value);
            }
        }
        // Apply auth helper from active environment (Bearer / Basic / API Key / JWT / OAuth)
        metadata = await applyAuth(metadata);

        // ---- Pre-request script (#126) ----
        // Typed `ctx.*` sandbox — env / vars / assert / log are
        // available everywhere; ctx.request / ctx.metadata /
        // ctx.publish overlay per protocol shape so the script
        // can manipulate the wire request before it ships.
        var scripts = getMethodScripts(selectedService.name, selectedMethod.name);
        // Per-request captured bag — both pre- and post-script see
        // the same `captured` object so writes persist across the
        // request boundary, and the next request's {{captured.X}}
        // template substitution reads from the same place.
        window.__bowire_captured = window.__bowire_captured || {};
        var capturedBag = window.__bowire_captured;
        if (scripts.preScript && scripts.preScript.trim()) {
            var shape = (typeof detectScriptProtocolShape === 'function')
                ? detectScriptProtocolShape(selectedService.source, selectedProtocol)
                : 'rest';
            var requestRef = {
                body: messages[0] || '{}',
                headers: Object.assign({}, metadata),
                query: {},
                method: selectedMethod.httpMethod || '',
                url: selectedService.originUrl || ''
            };
            // Snapshot env vars + write-through callback. ctx.env.set
            // updates the active environment (or workspace defaults
            // if no env is selected) so a captured token survives
            // beyond this request.
            var envSnapshot = (typeof getMergedVars === 'function') ? getMergedVars() : {};
            var onEnvSet = function (key, value) {
                try {
                    if (typeof getActiveEnv !== 'function') return;
                    var env = getActiveEnv();
                    if (env) {
                        env.vars = env.vars || {};
                        env.vars[key] = String(value == null ? '' : value);
                        var envs = getEnvironments();
                        var idx = envs.findIndex(function (e) { return e.id === env.id; });
                        if (idx >= 0) { envs[idx] = env; saveEnvironments(envs); }
                    }
                } catch (_) { /* persistence is best-effort */ }
            };
            var preResult = runPreRequestScript({
                source: scripts.preScript,
                protocolShape: shape,
                service: selectedService.name,
                method: selectedMethod.name,
                request: requestRef,
                env: envSnapshot,
                captured: capturedBag,
                onEnvSet: onEnvSet
            });
            if (!preResult.ok) {
                toast('Pre-request script error: ' + preResult.error, 'error');
                return;
            }
            // Apply mutations back to the in-flight request.
            messages[0] = requestRef.body;
            metadata = requestRef.headers;
            // Snapshot for the post-response script's reading of
            // ctx.request / ctx.metadata. Stashed on window so the
            // separate runPostResponseScript() call can pick it up.
            window.__bowire_request_snapshot = {
                body: requestRef.body,
                headers: Object.assign({}, requestRef.headers),
                query: Object.assign({}, requestRef.query || {}),
                method: requestRef.method,
                url: requestRef.url,
                publish: requestRef.publish ? Object.assign({}, requestRef.publish) : null,
                protocolShape: shape
            };
        } else {
            window.__bowire_request_snapshot = null;
        }

        const isServerStreaming = selectedMethod.serverStreaming;

        if (isServerStreaming) {
            invokeStreaming(selectedService.name, selectedMethod.name, messages, metadata);
        } else {
            invokeUnary(selectedService.name, selectedMethod.name, messages, metadata);
        }
    }

    // ---- Post-response Script Runner (#126) ----
    // Called from invokeUnary (after assertions) and invokeStreaming (on
    // 'done'). Runs the user's postScript through the typed sandbox so
    // ctx.* shapes match the protocol — REST gets ctx.request +
    // ctx.response.headers; gRPC gets ctx.metadata; MQTT gets
    // ctx.publish. The always-available env / vars / assert / log
    // surface is consistent across protocols.
    function runPostResponseScript(service, method, parsedResponse, extraResponseInfo) {
        var scripts = getMethodScripts(service, method);
        if (!scripts.postScript || !scripts.postScript.trim()) return;
        var snapshot = window.__bowire_request_snapshot || null;
        var shape = (snapshot && snapshot.protocolShape)
            ? snapshot.protocolShape
            : ((typeof detectScriptProtocolShape === 'function')
                ? detectScriptProtocolShape(
                    selectedService && selectedService.source,
                    selectedProtocol)
                : 'rest');

        window.__bowire_captured = window.__bowire_captured || {};
        var capturedBag = window.__bowire_captured;
        var envSnapshot = (typeof getMergedVars === 'function') ? getMergedVars() : {};
        var onEnvSet = function (key, value) {
            try {
                if (typeof getActiveEnv !== 'function') return;
                var env = getActiveEnv();
                if (env) {
                    env.vars = env.vars || {};
                    env.vars[key] = String(value == null ? '' : value);
                    var envs = getEnvironments();
                    var idx = envs.findIndex(function (e) { return e.id === env.id; });
                    if (idx >= 0) { envs[idx] = env; saveEnvironments(envs); }
                }
            } catch (_) { /* best-effort */ }
        };
        var requestRefForPost = snapshot || { headers: {}, query: {} };
        var responseRef = {
            body: parsedResponse,
            status: (extraResponseInfo && extraResponseInfo.status) || null,
            durationMs: (extraResponseInfo && extraResponseInfo.durationMs) || 0,
            headers: (extraResponseInfo && extraResponseInfo.headers) || {}
        };
        var postResult = runPostResponseScriptTyped({
            source: scripts.postScript,
            protocolShape: shape,
            service: service,
            method: method,
            request: requestRefForPost,
            response: responseRef,
            env: envSnapshot,
            captured: capturedBag,
            onEnvSet: onEnvSet
        });
        if (!postResult.ok) {
            toast('Post-response script error: ' + postResult.error, 'error');
        }
    }

    // ---- Keyboard Shortcuts Overlay ----
    function toggleShortcutsOverlay() {
        showShortcutsOverlay = !showShortcutsOverlay;
        renderShortcutsOverlay();
    }

    function renderShortcutsOverlay() {
        var existing = $('.bowire-shortcuts-overlay');
        if (existing) existing.remove();
        if (!showShortcutsOverlay) return;

        var shortcuts = [
            { key: 'Ctrl+Enter', desc: 'Execute request / Send message' },
            { key: '?', desc: 'Show/hide shortcuts' },
            { key: 'Esc', desc: 'Close overlay / Stop streaming / Disconnect' },
            { key: '/', desc: 'Focus search field' },
            { key: 't', desc: 'Toggle theme (dark/light)' },
            { key: 'f', desc: 'Toggle Form/JSON mode' },
            { key: 'r', desc: 'Repeat last call' },
            { key: 'j', desc: 'Next method (sidebar)' },
            { key: 'k', desc: 'Previous method (sidebar)' }
        ];

        var grid = el('div', { className: 'bowire-shortcuts-grid' });
        for (var i = 0; i < shortcuts.length; i++) {
            grid.appendChild(el('div', { className: 'bowire-shortcut-row' },
                el('kbd', { textContent: shortcuts[i].key }),
                el('span', { textContent: shortcuts[i].desc })
            ));
        }

        var modal = el('div', { className: 'bowire-shortcuts-modal' },
            el('div', { className: 'bowire-shortcuts-title', textContent: 'Keyboard Shortcuts' }),
            grid,
            el('div', { className: 'bowire-shortcuts-footer', textContent: 'Press ? or Esc to close' })
        );

        var overlay = el('div', {
            className: 'bowire-shortcuts-overlay',
            onClick: function (e) {
                if (e.target === overlay) {
                    showShortcutsOverlay = false;
                    renderShortcutsOverlay();
                }
            }
        }, modal);

        document.body.appendChild(overlay);
    }

    // ---- Console / Log Panel ----
    function formatConsoleTime(ts) {
        var d = new Date(ts);
        var pad = function (n, w) { var s = String(n); while (s.length < (w || 2)) s = '0' + s; return s; };
        return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '.' + pad(d.getMilliseconds(), 3);
    }

    function consoleEntryClass(type, status) {
        if (type === 'error') return 'error';
        if (type === 'request' || type === 'send') return 'request';
        if (type === 'stream') return 'stream';
        if (type === 'channel') return 'channel';
        if (status === 'OK' || status === 'Completed' || status === 'Connected') return 'response ok';
        if (status && status !== 'Streaming') return 'response warn';
        return 'response';
    }

    function consoleTypeLabel(type) {
        switch (type) {
            case 'request': return 'REQ';
            case 'response': return 'RES';
            case 'stream':   return 'STR';
            case 'send':     return 'SND';
            case 'channel':  return 'CH';
            case 'error':    return 'ERR';
            default:         return type ? type.toUpperCase() : 'LOG';
        }
    }

    // Fast-path: append a single new entry to the already-open console
    // panel without tearing it down. Returns true on success so the
    // caller knows a full renderConsolePanel can be skipped. Falls
    // through to false when the panel doesn't exist yet (first open)
    // or after a trim that dropped old rows (the count header would
    // be wrong otherwise).
    function appendConsoleEntry(entry) {
        var body = document.querySelector('.bowire-console-body');
        if (!body) return false;

        // Remove the "No activity yet" placeholder (legacy class or
        // #164's empty-card variant) so the new row replaces it
        // instead of stacking under it.
        var empty = body.querySelector('.bowire-console-empty, .bowire-empty-card');
        if (empty) empty.remove();

        var row = buildConsoleRow(entry);
        body.appendChild(row);

        // Update counter in the drawer-tab accessory (legacy:
        // .bowire-console-count from the retired floating panel; new:
        // tab accessory) — pick whichever is present.
        var countEl = document.querySelector('.bowire-console-count')
            || document.querySelector('#bowire-right-drawer-tab-console .bowire-help-topic-count');
        if (countEl) countEl.textContent = String(consoleLog.length);

        // Pin-aware auto-scroll: only follow the tail when the
        // user hasn't scrolled up to read history (or has
        // explicitly toggled off the pin in the toolbar).
        if (consoleAutoScroll) {
            requestAnimationFrame(function () {
                body.scrollTop = body.scrollHeight;
            });
        }
        return true;
    }

    function buildConsoleRow(entry) {
        var isSel = consoleSelected.has(entry.id);
        // Click semantics live on the row itself (not just the
        // summary) so the whole horizontal strip \u2014 including
        // empty space to the right of the text \u2014 is a hit target.
        //   plain         \u2192 replace selection with this row
        //   ctrl/cmd+click \u2192 toggle this row in the selection
        //   shift+click   \u2192 range-select from anchor to here
        // The expand/copy buttons inside the row stopPropagation
        // so they keep firing their own actions; double-click
        // toggles the expanded body so users can still read the
        // full payload without losing selection.
        var row = el('div', {
            className: 'bowire-console-row '
                + consoleEntryClass(entry.type, entry.status)
                + (isSel ? ' selected' : ''),
            'data-entry-id': String(entry.id),
            onClick: function (e) {
                if (e.shiftKey && consoleSelectionAnchor != null) {
                    var filtered = filteredConsoleLog();
                    var ai = filtered.findIndex(function (x) { return x.id === consoleSelectionAnchor; });
                    var bi = filtered.findIndex(function (x) { return x.id === entry.id; });
                    if (ai >= 0 && bi >= 0) {
                        var lo = Math.min(ai, bi), hi = Math.max(ai, bi);
                        consoleSelected.clear();
                        for (var k = lo; k <= hi; k++) consoleSelected.add(filtered[k].id);
                    } else {
                        consoleSelected = new Set([entry.id]);
                    }
                } else if (e.ctrlKey || e.metaKey) {
                    if (consoleSelected.has(entry.id)) consoleSelected.delete(entry.id);
                    else consoleSelected.add(entry.id);
                    consoleSelectionAnchor = entry.id;
                } else {
                    consoleSelected.clear();
                    consoleSelected.add(entry.id);
                    consoleSelectionAnchor = entry.id;
                }
                if (typeof render === 'function') render();
            },
            onDblClick: function (e) {
                e.preventDefault();
                entry.expanded = !entry.expanded;
                if (typeof render === 'function') render();
            }
        });
        var summary = el('div', { className: 'bowire-console-summary' });
        summary.appendChild(el('span', { className: 'bowire-console-time', textContent: formatConsoleTime(entry.time) }));
        summary.appendChild(el('span', { className: 'bowire-console-type', textContent: consoleTypeLabel(entry.type) }));
        summary.appendChild(el('span', { className: 'bowire-console-method', textContent: entry.method || '' }));
        if (entry.status) {
            summary.appendChild(el('span', { className: 'bowire-console-status', textContent: entry.status }));
        }
        if (typeof entry.durationMs === 'number') {
            summary.appendChild(el('span', { className: 'bowire-console-duration', textContent: entry.durationMs + ' ms' }));
        }
        if (entry.body) {
            var preview = (typeof entry.body === 'string' ? entry.body : JSON.stringify(entry.body))
                .replace(/\s+/g, ' ');
            if (preview.length > 80) preview = preview.substring(0, 80) + '\u2026';
            summary.appendChild(el('span', { className: 'bowire-console-preview', textContent: preview }));
        }
        // Hover-revealed action cluster on the right end of the
        // row: expand toggle + copy button. Both are CSS-hidden
        // until .bowire-console-row:hover or focus.
        //
        // Expand toggle (...) doubles as a peek-tooltip: its title
        // carries the pretty-printed body so just hovering over
        // the button (without clicking) shows the full message in
        // the browser's native tooltip. Click toggles the inline
        // expanded body the same way double-click on the row does.
        var fullBody = '';
        if (entry.body) {
            try {
                var parsed = typeof entry.body === 'string' ? JSON.parse(entry.body) : entry.body;
                fullBody = JSON.stringify(parsed, null, 2);
            } catch {
                fullBody = String(entry.body);
            }
        }
        summary.appendChild(el('button', {
            type: 'button',
            className: 'bowire-console-row-expand',
            title: fullBody || 'No body to expand',
            'aria-label': entry.expanded ? 'Collapse entry' : 'Expand entry',
            'aria-expanded': entry.expanded ? 'true' : 'false',
            textContent: entry.expanded ? '×' : '…',
            onClick: function (e) {
                e.stopPropagation();
                entry.expanded = !entry.expanded;
                if (typeof render === 'function') render();
            }
        }));
        summary.appendChild(el('button', {
            type: 'button',
            className: 'bowire-console-row-copy',
            title: 'Copy this entry',
            'aria-label': 'Copy entry',
            innerHTML: svgIcon('copy'),
            onClick: function (e) {
                e.stopPropagation();
                navigator.clipboard.writeText(serializeConsoleEntries([entry])).then(function () {
                    if (typeof toast === 'function') toast('Entry copied', 'success');
                });
            }
        }));
        row.appendChild(summary);
        if (entry.expanded && entry.body) {
            var pretty;
            try {
                var parsed = typeof entry.body === 'string' ? JSON.parse(entry.body) : entry.body;
                pretty = JSON.stringify(parsed, null, 2);
            } catch {
                pretty = String(entry.body);
            }
            row.appendChild(el('pre', { className: 'bowire-console-body-detail', textContent: pretty }));
        }
        return row;
    }

    // #164 — Console moved into the unified right-side drawer as a tab.
    // _renderConsoleDrawerBody returns the tab body (header lives in
    // the drawer's tab strip). renderConsolePanel + appendConsoleEntry
    // now just trigger a full render() so morphdom diffs the drawer
    // content rather than maintaining a separate floating panel.
    function _renderConsoleDrawerBody() {
        var wrap = el('div', { className: 'bowire-console-drawer-wrap' });
        // No separate toolbar row — every filter chip + every
        // action button lives in the drawer header above.
        var body = el('div', { className: 'bowire-console-body bowire-console-drawer-body' });
        // Auto-scroll detection: when the user scrolls up away
        // from the bottom, drop the auto-scroll pin so new entries
        // don't yank them back. Scrolling all the way back to the
        // bottom re-arms it. The pin toggle in the toolbar exposes
        // the same flag for explicit control.
        body.addEventListener('scroll', function () {
            var atBottom = body.scrollTop + body.clientHeight >= body.scrollHeight - 4;
            if (consoleAutoScroll && !atBottom) {
                consoleAutoScroll = false;
                _syncConsolePinButton();
            } else if (!consoleAutoScroll && atBottom) {
                consoleAutoScroll = true;
                _syncConsolePinButton();
            }
        });

        var filtered = filteredConsoleLog();
        if (consoleLog.length === 0) {
            body.appendChild(renderEmptyCard({
                icon: 'console',
                headline: 'No activity yet',
                body: 'Console captures every request, response, channel-frame, and error. Fire a call from the request pane and it lands here as a sortable timeline.',
                hintKey: 'bowire_empty_console_hint',
                actions: [
                    {
                        label: 'Focus request pane',
                        onClick: function () {
                            var pane = document.querySelector('.bowire-request-pane, .bowire-freeform-pane');
                            if (pane) pane.scrollIntoView({ behavior: 'smooth', block: 'start' });
                        }
                    },
                ]
            }));
        } else if (filtered.length === 0) {
            body.appendChild(el('div', { className: 'bowire-console-empty',
                style: 'padding:12px 14px;color:var(--bowire-text-tertiary)',
                textContent: 'No entries match the current filters.' }));
        } else {
            for (var i = 0; i < filtered.length; i++) {
                body.appendChild(buildConsoleRow(filtered[i]));
            }
        }
        wrap.appendChild(body);
        return wrap;
    }

    function _syncConsolePinButton() {
        var btn = document.getElementById('bowire-console-pin-btn');
        if (!btn) return;
        btn.classList.toggle('is-on', !consoleAutoScroll);
        btn.setAttribute('aria-pressed', !consoleAutoScroll ? 'true' : 'false');
        btn.title = consoleAutoScroll
            ? 'Auto-scroll is active — click to pin view'
            : 'View pinned — click to follow tail';
    }

    // Shared label tables used by both the chip row (toolbar) and
    // the filter trigger (header) so the two stay in sync.
    var CONSOLE_TYPE_LABELS = {
        request:  'Req',
        response: 'Resp',
        error:    'Err',
        channel:  'Chan'
    };
    var CONSOLE_TIME_OPTS = [
        { v: 1,  label: 'Last 1 min' },
        { v: 5,  label: 'Last 5 min' },
        { v: 15, label: 'Last 15 min' },
        { v: 60, label: 'Last 60 min' }
    ];

    // Build the chip cluster + filter-add trigger. Returns two DOM
    // nodes so the caller can place the chips inline in the header
    // and the trigger inside the action cluster — the toolbar row
    // is gone, everything lives in the drawer header.
    function _buildConsoleFilterChips() {
        var chipRow = el('div', { className: 'bowire-console-filter-chips' });

        function buildFilterChip(label, onRemove) {
            return el('span', { className: 'bowire-console-filter-chip' },
                el('span', { className: 'bowire-console-filter-chip-label', textContent: label }),
                el('button', {
                    type: 'button',
                    className: 'bowire-console-filter-chip-remove',
                    title: 'Remove filter',
                    'aria-label': 'Remove filter ' + label,
                    textContent: '×',
                    onClick: function (e) {
                        e.stopPropagation();
                        onRemove();
                        if (typeof render === 'function') render();
                    }
                })
            );
        }

        consoleTypeFilter.forEach(function (id) {
            var lbl = CONSOLE_TYPE_LABELS[id] || id;
            chipRow.appendChild(buildFilterChip(lbl, function () {
                consoleTypeFilter.delete(id);
            }));
        });
        if (consoleTimeFilterMin > 0) {
            var match = CONSOLE_TIME_OPTS.find(function (o) { return o.v === consoleTimeFilterMin; });
            chipRow.appendChild(buildFilterChip(match ? match.label : ('Last ' + consoleTimeFilterMin + ' min'), function () {
                consoleTimeFilterMin = 0;
            }));
        }
        return chipRow;
    }

    function _buildConsoleFilterAddTrigger() {
        // Header button — toggles the filter bar below the drawer
        // header on/off. The bar carries the active chips, a search
        // input for free-text filtering, and (Phase 2) parses
        // `type:req`-style tokens into structured chips. is-on
        // reflects "filter bar visible OR any filter active" so the
        // user gets a hint that filters are in play even when the
        // bar is collapsed.
        var anyActive = consoleTypeFilter.size > 0
            || consoleTimeFilterMin > 0
            || !!(consoleTextFilter || '').trim();
        return el('button', {
            type: 'button',
            id: 'bowire-console-filter-add-btn',
            className: 'bowire-console-toolbar-btn bowire-console-filter-add-btn'
                + ((consoleFilterBarOpen || anyActive) ? ' is-on' : ''),
            title: consoleFilterBarOpen ? 'Hide filter bar' : 'Show filter bar',
            'aria-label': 'Toggle filter bar',
            'aria-pressed': consoleFilterBarOpen ? 'true' : 'false',
            innerHTML: svgIcon('filter'),
            onClick: function () {
                consoleFilterBarOpen = !consoleFilterBarOpen;
                if (typeof render === 'function') render();
            }
        });
    }

    // Filter bar that lives between the drawer header and the
    // console body. Hosts the active-filter chips + a search input
    // that filters by free-text match against the log entries.
    function _renderConsoleFilterBar() {
        var bar = el('div', { className: 'bowire-console-filterbar' });

        // Active chips (type + time).
        function buildChip(label, onRemove) {
            return el('span', { className: 'bowire-console-filter-chip' },
                el('span', { className: 'bowire-console-filter-chip-label', textContent: label }),
                el('button', {
                    type: 'button',
                    className: 'bowire-console-filter-chip-remove',
                    title: 'Remove filter',
                    'aria-label': 'Remove filter ' + label,
                    textContent: '×',
                    onClick: function (e) {
                        e.stopPropagation();
                        onRemove();
                        if (typeof render === 'function') render();
                    }
                })
            );
        }
        consoleTypeFilter.forEach(function (id) {
            bar.appendChild(buildChip('type:' + id, function () { consoleTypeFilter.delete(id); }));
        });
        if (consoleTimeFilterMin > 0) {
            bar.appendChild(buildChip(consoleTimeFilterMin + 'm', function () { consoleTimeFilterMin = 0; }));
        }

        // Search input — live filter via consoleTextFilter. Special
        // tokens (`type:req` / `type:resp` / `type:error` /
        // `type:channel` / `5m`, `15m`, …) committed with Space or
        // Enter become chips; everything else stays as free-text
        // search across method/status/body.
        var input = el('input', {
            type: 'text',
            className: 'bowire-console-filterbar-input',
            placeholder: 'Search… (try: type:req, type:resp, 5m)',
            value: consoleTextFilter,
            spellcheck: 'false',
            autocomplete: 'off',
            onInput: function (e) {
                consoleTextFilter = e.target.value;
                _rerenderConsoleBody();
            },
            onKeydown: function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    var raw = (input.value || '').trim();
                    if (!raw) return;
                    if (_tryTokeniseConsoleFilter(raw)) {
                        e.preventDefault();
                        input.value = '';
                        consoleTextFilter = '';
                        if (typeof render === 'function') render();
                    }
                } else if (e.key === 'Backspace' && input.value === '') {
                    // Backspace on empty input pops the most recent
                    // chip — keyboard-friendly chip removal.
                    if (consoleTimeFilterMin > 0) {
                        consoleTimeFilterMin = 0;
                        if (typeof render === 'function') render();
                    } else if (consoleTypeFilter.size > 0) {
                        var last = Array.from(consoleTypeFilter).pop();
                        consoleTypeFilter.delete(last);
                        if (typeof render === 'function') render();
                    }
                }
            }
        });
        bar.appendChild(input);

        // Clear-all button when anything filter-y is set.
        if (consoleTypeFilter.size > 0 || consoleTimeFilterMin > 0 || (consoleTextFilter || '').trim()) {
            bar.appendChild(el('button', {
                type: 'button',
                className: 'bowire-console-filterbar-clear',
                title: 'Clear all filters',
                'aria-label': 'Clear all filters',
                textContent: 'Clear',
                onClick: function () {
                    consoleTypeFilter.clear();
                    consoleTimeFilterMin = 0;
                    consoleTextFilter = '';
                    if (typeof render === 'function') render();
                }
            }));
        }
        return bar;
    }

    // Parse a single token like `type:req` or `5m` into a structured
    // filter chip. Returns true when the token was consumed (caller
    // clears the input), false when it should stay as plain text.
    function _tryTokeniseConsoleFilter(raw) {
        var m;
        m = raw.match(/^type:(req|request|resp|response|err|error|chan|channel)$/i);
        if (m) {
            var alias = {
                req: 'request', request: 'request',
                resp: 'response', response: 'response',
                err: 'error', error: 'error',
                chan: 'channel', channel: 'channel'
            };
            var canon = alias[m[1].toLowerCase()];
            if (canon) {
                consoleTypeFilter.add(canon);
                return true;
            }
        }
        m = raw.match(/^(\d+)m$/i);
        if (m) {
            consoleTimeFilterMin = parseInt(m[1], 10);
            return true;
        }
        return false;
    }

    // Body-only re-render path so live-typing in the search input
    // doesn't yank focus away by recreating the input element.
    function _rerenderConsoleBody() {
        var body = document.querySelector('.bowire-console-drawer-body');
        if (!body) return;
        var parent = body.parentElement;
        var fresh = el('div', { className: 'bowire-console-body bowire-console-drawer-body' });
        var filtered = filteredConsoleLog();
        if (filtered.length === 0) {
            fresh.appendChild(el('div', { className: 'bowire-console-empty',
                style: 'padding:12px 14px;color:var(--bowire-text-tertiary)',
                textContent: consoleLog.length === 0 ? 'No activity yet' : 'No entries match the current filters.' }));
        } else {
            for (var i = 0; i < filtered.length; i++) fresh.appendChild(buildConsoleRow(filtered[i]));
        }
        parent.replaceChild(fresh, body);
    }

    // Build the cluster of action buttons that lives in the
    // drawer header — pin auto-scroll, download selection,
    // download all, clear selection, clear log, close. Mirrors
    // the "all buttons in the header" layout the user asked for.
    function _renderConsoleHeaderActions(onClose) {
        var actions = el('div', { className: 'bowire-console-header-actions' });

        // Filter-add trigger leads the cluster — funnel icon, opens
        // the same dropdown that previously sat under "+ Filter" in
        // the retired toolbar row.
        actions.appendChild(_buildConsoleFilterAddTrigger());

        // Pin glyph = "view pinned at current position, not
        // following the tail". So the button is .is-on when
        // consoleAutoScroll is FALSE (view is pinned / frozen),
        // and inactive when auto-scroll follows new entries.
        // Click pins (= turn auto-scroll off); click again to
        // resume tailing.
        actions.appendChild(el('button', {
            type: 'button',
            id: 'bowire-console-pin-btn',
            className: 'bowire-console-toolbar-btn' + (!consoleAutoScroll ? ' is-on' : ''),
            title: consoleAutoScroll
                ? 'Auto-scroll is active — click to pin view'
                : 'View pinned — click to follow tail',
            'aria-label': 'Toggle auto-scroll',
            'aria-pressed': !consoleAutoScroll ? 'true' : 'false',
            innerHTML: svgIcon('pin'),
            onClick: function () {
                consoleAutoScroll = !consoleAutoScroll;
                _syncConsolePinButton();
                if (consoleAutoScroll) {
                    var body = document.querySelector('.bowire-console-drawer-body');
                    if (body) body.scrollTop = body.scrollHeight;
                }
            }
        }));
        actions.appendChild(el('span', {
            className: 'bowire-console-toolbar-selcount',
            title: 'Selected entries',
            textContent: String(consoleSelected.size)
        }));
        // Smart download — falls back to the whole log when there's
        // no selection, downloads just the selection otherwise. One
        // button instead of separate "download selected / download
        // all" affordances; the title reflects the current mode.
        var hasSel = consoleSelected.size > 0;
        actions.appendChild(el('button', {
            type: 'button',
            className: 'bowire-console-toolbar-btn',
            title: hasSel
                ? 'Download selection (' + consoleSelected.size + ' entr' + (consoleSelected.size === 1 ? 'y' : 'ies') + ')'
                : 'Download entire log (' + consoleLog.length + ')',
            'aria-label': hasSel ? 'Download selection' : 'Download entire log',
            innerHTML: svgIcon('download'),
            onClick: function () {
                if (hasSel) {
                    var entries = consoleLog.filter(function (e) { return consoleSelected.has(e.id); });
                    downloadConsoleEntries(entries, 'bowire-console-selected.log');
                } else {
                    downloadConsoleEntries(consoleLog, 'bowire-console.log');
                }
            }
        }));
        // Clear selection — distinct icon from Close console (which
        // uses the plain X). selectionClear renders a dashed rectangle
        // around an interior X so the metaphor reads as 'clear the
        // marked selection' instead of 'close this pane'. Icon over
        // text so the toolbar survives i18n without layout breaks.
        actions.appendChild(el('button', {
            type: 'button',
            className: 'bowire-console-toolbar-btn',
            title: 'Clear selection',
            'aria-label': 'Clear selection',
            innerHTML: svgIcon('selectionClear'),
            onClick: function () {
                consoleSelected.clear();
                if (typeof render === 'function') render();
            }
        }));
        // Clear all entries — trash icon parity with the
        // Recordings/Mocks rails' per-row delete pattern. Was a 'Clear'
        // text label which read as a verb word next to two other
        // icon-only buttons, so the eye couldn't scan the toolbar as
        // a uniform cluster.
        actions.appendChild(el('button', {
            type: 'button',
            id: 'bowire-console-clear-btn',
            className: 'bowire-console-clear bowire-console-toolbar-btn',
            innerHTML: svgIcon('trash'),
            title: 'Clear all entries',
            'aria-label': 'Clear all entries',
            onClick: clearConsole
        }));
        actions.appendChild(el('button', {
            type: 'button',
            className: 'bowire-drawer-close bowire-bottom-drawer-close',
            title: 'Close console',
            'aria-label': 'Close console',
            innerHTML: svgIcon('close'),
            onClick: onClose
        }));
        return actions;
    }

    function _renderConsoleTabActions() {
        return el('div', { className: 'bowire-console-actions bowire-console-drawer-actions' },
            el('button', {
                id: 'bowire-console-clear-btn',
                className: 'bowire-console-clear',
                innerHTML: svgIcon('trash'),
                title: 'Clear all entries',
                'aria-label': 'Clear all entries',
                onClick: clearConsole
            })
        );
    }

    function renderConsolePanel(autoScroll) {
        // Legacy floating panel retired (#164 v2) — Console now mounts
        // as a bottom-attached drawer below the main body. Clean up
        // any stale floating root, then auto-scroll the bottom-drawer
        // body when new entries arrived AND the pin is on.
        var existing = document.querySelector('.bowire-console-panel');
        if (existing) existing.remove();
        if (consoleOpen && autoScroll && consoleAutoScroll) {
            requestAnimationFrame(function () {
                var body = document.querySelector('.bowire-console-drawer-body');
                if (body) body.scrollTop = body.scrollHeight;
            });
        }
    }

    // #164 v2 — Bottom-attached console drawer. Renders directly inside
    // bowire-app-middle as a sibling below the body. Reads
    // consoleHeight (px) for the row height; a splitter at the top
    // edge lets the operator drag-resize. Persisted height survives
    // reloads via bowire_console_height.
    function renderConsoleBottomDrawer() {
        var drawer = el('div', {
            id: 'bowire-bottom-drawer',
            className: 'bowire-bottom-drawer',
            role: 'complementary',
            'aria-label': 'Console',
            style: 'height:' + (typeof consoleHeight === 'number' ? consoleHeight : 240) + 'px'
        });
        // Top-edge splitter — drag to resize. Mousedown captures move
        // until mouseup; we update consoleHeight live so the panel
        // tracks the cursor. Clamps applied so the splitter can't
        // shrink the drawer below 80 px or eat more than 70 % of the
        // viewport.
        var splitter = el('div', {
            className: 'bowire-bottom-drawer-splitter',
            role: 'separator',
            'aria-orientation': 'horizontal',
            'aria-label': 'Resize console',
            title: 'Drag to resize the console'
        });
        splitter.addEventListener('mousedown', function (e) {
            e.preventDefault();
            var startY = e.clientY;
            var startHeight = drawer.getBoundingClientRect().height;
            var maxH = Math.floor(window.innerHeight * 0.7);
            function onMove(ev) {
                var dy = startY - ev.clientY;
                var next = Math.min(maxH, Math.max(80, startHeight + dy));
                drawer.style.height = next + 'px';
                if (typeof consoleHeight !== 'undefined') consoleHeight = next;
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.classList.remove('bowire-resizing-vertical');
                if (typeof persistConsoleHeight === 'function') persistConsoleHeight();
            }
            document.body.classList.add('bowire-resizing-vertical');
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
        drawer.appendChild(splitter);

        var header = el('div', { className: 'bowire-bottom-drawer-header' });
        header.appendChild(el('div', { className: 'bowire-bottom-drawer-title' },
            el('span', { className: 'bowire-console-title-icon', innerHTML: svgIcon('clock') }),
            el('span', { textContent: 'Console' }),
            consoleLog.length > 0
                ? el('span', { className: 'bowire-bottom-drawer-count', textContent: String(consoleLog.length) })
                : null
        ));
        // Action cluster (filter toggle, pin, downloads, clears,
        // close) lives at the header right end. Active-filter chips
        // moved into the filter bar below — toggle via the funnel
        // button in the action cluster.
        header.appendChild(_renderConsoleHeaderActions(function () {
            consoleOpen = false;
            if (typeof render === 'function') render();
        }));
        drawer.appendChild(header);

        if (consoleFilterBarOpen) {
            drawer.appendChild(_renderConsoleFilterBar());
        }

        drawer.appendChild(_renderConsoleDrawerBody());
        return drawer;
    }

    // ---- Guided Tour (legacy shim) ----
    //
    // #281 — The tour engine moved out of execute.js into its own
    // tour.js fragment with a proper spotlight overlay, page-
    // navigation primitive, and per-step CTA / event / next-button
    // advance modes. The `startTour()` symbol stayed callable from
    // every prior call site (sidebar 'Take Tour' footer, landing
    // help footer, render-main welcome card) so we expose a thin
    // shim that forwards to the new engine's Getting-started tour.
    function startTour() {
        if (typeof window !== 'undefined'
            && typeof window.bowireStartGettingStartedTour === 'function') {
            // Force-mode = true so the legacy 'Take Tour' affordance
            // always launches even if the saved-once flag is set —
            // the operator clicked it on purpose.
            window.bowireStartGettingStartedTour({ force: true });
            return;
        }
    }
    function endTour() {
        if (typeof window !== 'undefined' && typeof window.bowireStopTour === 'function') {
            window.bowireStopTour();
        }
    }
    function shouldAutoStartTour() {
        // Auto-start retired — the new engine pops only on explicit
        // user action ('Take a tour' button or palette command), so
        // callers that still probed this flag get a stable `false`.
        return false;
    }

