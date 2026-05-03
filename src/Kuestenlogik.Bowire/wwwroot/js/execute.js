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

        // ---- Pre-request script ----
        var scripts = getMethodScripts(selectedService.name, selectedMethod.name);
        if (scripts.preScript && scripts.preScript.trim()) {
            try {
                var envVars = getMergedVars();
                var flowVars = {};
                var requestObj = {
                    body: messages[0] || '{}',
                    headers: Object.assign({}, metadata)
                };
                new Function('env', 'vars', 'request', scripts.preScript)(envVars, flowVars, requestObj);
                // Apply any mutations the script made
                messages[0] = requestObj.body;
                metadata = requestObj.headers;
                // Store flowVars so the post-response script can access them
                window.__bowire_flow_vars = flowVars;
            } catch (scriptErr) {
                toast('Pre-request script error: ' + scriptErr.message, 'error');
                return;
            }
        } else {
            window.__bowire_flow_vars = {};
        }

        const isServerStreaming = selectedMethod.serverStreaming;

        if (isServerStreaming) {
            invokeStreaming(selectedService.name, selectedMethod.name, messages, metadata);
        } else {
            invokeUnary(selectedService.name, selectedMethod.name, messages, metadata);
        }
    }

    // ---- Post-response Script Runner ----
    // Called from invokeUnary (after assertions) and invokeStreaming (on
    // 'done'). Runs the user's postScript in a sandboxed Function with
    // env, vars, response, and a simple assert() helper.
    function runPostResponseScript(service, method, parsedResponse) {
        var scripts = getMethodScripts(service, method);
        if (!scripts.postScript || !scripts.postScript.trim()) return;

        try {
            var envVars = getMergedVars();
            var flowVars = window.__bowire_flow_vars || {};
            var assertFn = function (condition, message) {
                if (!condition) {
                    throw new Error('Assertion failed' + (message ? ': ' + message : ''));
                }
            };
            new Function('env', 'vars', 'response', 'assert', scripts.postScript)(
                envVars, flowVars, parsedResponse, assertFn
            );
        } catch (scriptErr) {
            toast('Post-response script error: ' + scriptErr.message, 'error');
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

        // Remove the "No activity yet" placeholder if present
        var empty = body.querySelector('.bowire-console-empty');
        if (empty) empty.remove();

        var row = buildConsoleRow(entry);
        body.appendChild(row);

        // Update counter in the header
        var countEl = document.querySelector('.bowire-console-count');
        if (countEl) countEl.textContent = String(consoleLog.length);

        // Auto-scroll to bottom
        requestAnimationFrame(function () {
            body.scrollTop = body.scrollHeight;
        });
        return true;
    }

    function buildConsoleRow(entry) {
        var row = el('div', { className: 'bowire-console-row ' + consoleEntryClass(entry.type, entry.status) });
        var summary = el('div', {
            className: 'bowire-console-summary',
            onClick: function () {
                entry.expanded = !entry.expanded;
                renderConsolePanel(false);
            }
        });
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

    function renderConsolePanel(autoScroll) {
        var existing = $('.bowire-console-panel');
        if (existing) existing.remove();
        if (!consoleOpen) return;

        var header = el('div', { className: 'bowire-console-header' },
            el('div', { className: 'bowire-console-title' },
                el('span', { className: 'bowire-console-title-icon', innerHTML: svgIcon('clock') }),
                el('span', { textContent: 'Console' }),
                el('span', { className: 'bowire-console-count', textContent: String(consoleLog.length) })
            ),
            el('div', { className: 'bowire-console-actions' },
                el('button', {
                    id: 'bowire-console-clear-btn',
                    className: 'bowire-console-clear',
                    textContent: 'Clear',
                    title: 'Clear all entries',
                    onClick: clearConsole
                }),
                el('button', {
                    id: 'bowire-console-close-btn',
                    className: 'bowire-console-close',
                    title: 'Close console',
                    'aria-label': 'Close console',
                    innerHTML: svgIcon('close'),
                    onClick: function () { consoleOpen = false; renderConsolePanel(false); }
                })
            )
        );

        var body = el('div', { className: 'bowire-console-body' });

        if (consoleLog.length === 0) {
            body.appendChild(el('div', { className: 'bowire-console-empty', textContent: 'No activity yet. Send a request to see it here.' }));
        } else {
            for (var i = 0; i < consoleLog.length; i++) {
                body.appendChild(buildConsoleRow(consoleLog[i]));
            }
        }

        var panel = el('div', { className: 'bowire-console-panel' }, header, body);
        document.body.appendChild(panel);

        if (autoScroll) {
            requestAnimationFrame(function () {
                body.scrollTop = body.scrollHeight;
            });
        }
    }

    // ---- Guided Tour ----
    // ---- Lightweight Tour (no external dependencies) ----
    var tourStepIndex = -1;
    var tourOverlay = null;

    function getTourSteps() {
        var steps = [
            { el: '.bowire-service-list', text: 'Browse your gRPC services here. Click a service to expand its methods.', pos: 'right' },
            { el: '.bowire-method-item', text: 'Each method shows its type: Unary (U), Server Streaming (SS), Client Streaming (CS), or Duplex (DX).', pos: 'right' },
            { el: '#bowire-topbar-palette', text: 'Command palette — search methods, jump between environments, apply protocol filters, pin a name filter. Press Shift+/ to focus it from anywhere.', pos: 'bottom' },
        ];
        if ($('.bowire-favorites-group')) steps.push({ el: '.bowire-favorites-group', text: 'Star methods for quick access. Favorites are pinned at the top.', pos: 'right' });
        if ($('.bowire-pane')) steps.push({ el: '.bowire-pane', text: 'Enter your request here. Switch between Form and JSON mode.', pos: 'right' });
        if ($('.bowire-input-mode-toggle')) steps.push({ el: '.bowire-input-mode-toggle', text: 'Form mode for simple types, JSON for complex payloads.', pos: 'bottom' });
        if ($('.bowire-execute-btn')) steps.push({ el: '.bowire-execute-btn', text: 'Click Execute or press Ctrl+Enter to send your request.', pos: 'top' });
        steps.push({ el: '.bowire-theme-toggle', text: 'Switch between dark and light theme. Press T as shortcut.', pos: 'top' });
        steps.push({ el: null, text: 'Press ? anytime to see all keyboard shortcuts. Enjoy exploring your APIs!', pos: 'center' });
        return steps;
    }

    var TOUR_DONE_KEY = 'bowire_tour_done';

    function startTour() {
        tourStepIndex = 0;
        showTourStep();
    }

    function endTour() {
        tourStepIndex = -1;
        if (tourOverlay) { tourOverlay.remove(); tourOverlay = null; }
        try { localStorage.setItem(TOUR_DONE_KEY, '1'); } catch {}
    }

    function shouldAutoStartTour() {
        try { return !localStorage.getItem(TOUR_DONE_KEY); } catch { return false; }
    }

    function showTourStep() {
        var steps = getTourSteps();
        if (tourStepIndex < 0 || tourStepIndex >= steps.length) { endTour(); return; }

        if (tourOverlay) tourOverlay.remove();

        var step = steps[tourStepIndex];
        var target = step.el ? $(step.el) : null;

        // Overlay
        tourOverlay = el('div', { className: 'bowire-tour-overlay', onClick: function (e) { if (e.target === tourOverlay) endTour(); } });

        // Tooltip
        var isLast = tourStepIndex === steps.length - 1;
        var isFirst = tourStepIndex === 0;

        var buttons = el('div', { className: 'bowire-tour-buttons' });
        if (!isFirst) buttons.appendChild(el('button', { id: 'bowire-tour-back-btn', className: 'bowire-tour-btn-secondary', textContent: 'Back', onClick: function () { tourStepIndex--; showTourStep(); } }));
        buttons.appendChild(el('button', { id: 'bowire-tour-skip-btn', className: 'bowire-tour-btn-skip', textContent: 'Skip', onClick: endTour }));
        buttons.appendChild(el('button', { id: 'bowire-tour-next-btn', className: 'bowire-tour-btn-primary', textContent: isLast ? 'Done' : 'Next', onClick: function () { tourStepIndex++; showTourStep(); } }));

        var counter = el('span', { className: 'bowire-tour-counter', textContent: (tourStepIndex + 1) + ' / ' + steps.length });

        var tooltip = el('div', { className: 'bowire-tour-tooltip' },
            el('div', { className: 'bowire-tour-text', textContent: step.text }),
            el('div', { className: 'bowire-tour-footer' }, counter, buttons)
        );

        tourOverlay.appendChild(tooltip);
        document.body.appendChild(tourOverlay);

        // Position tooltip near target
        if (target) {
            target.scrollIntoView({ behavior: 'smooth', block: 'center' });
            requestAnimationFrame(function () {
                var rect = target.getBoundingClientRect();
                target.classList.add('bowire-tour-highlight');
                // Remove highlight from previous
                $$('.bowire-tour-highlight').forEach(function (el) { if (el !== target) el.classList.remove('bowire-tour-highlight'); });

                if (step.pos === 'right') {
                    tooltip.style.left = (rect.right + 12) + 'px';
                    tooltip.style.top = rect.top + 'px';
                } else if (step.pos === 'bottom') {
                    tooltip.style.left = rect.left + 'px';
                    tooltip.style.top = (rect.bottom + 12) + 'px';
                } else if (step.pos === 'top') {
                    tooltip.style.left = rect.left + 'px';
                    tooltip.style.top = (rect.top - 12) + 'px';
                    tooltip.style.transform = 'translateY(-100%)';
                }

                // Keep tooltip in viewport
                requestAnimationFrame(function () {
                    var tr = tooltip.getBoundingClientRect();
                    if (tr.right > window.innerWidth - 16) tooltip.style.left = (window.innerWidth - tr.width - 16) + 'px';
                    if (tr.bottom > window.innerHeight - 16) tooltip.style.top = (window.innerHeight - tr.height - 16) + 'px';
                    if (tr.left < 16) tooltip.style.left = '16px';
                    if (tr.top < 16) { tooltip.style.top = '16px'; tooltip.style.transform = 'none'; }
                });
            });
        } else {
            // Center (no target element)
            tooltip.style.left = '50%';
            tooltip.style.top = '50%';
            tooltip.style.transform = 'translate(-50%, -50%)';
            $$('.bowire-tour-highlight').forEach(function (el) { el.classList.remove('bowire-tour-highlight'); });
        }
    }

