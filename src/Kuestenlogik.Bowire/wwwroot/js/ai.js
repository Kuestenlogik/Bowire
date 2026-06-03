// ai.js — AI side-panel slot + deterministic hint engine (#25 Phase 1).
//
// The ADR (docs/architecture/ai-integration.md) lays out four phases:
//
//   1. Hint engine        — no LLM, rule-based suggestions from live UI state.
//   2. Local-model mode   — Ollama / LM Studio auto-detect + Mode-2 features.
//   3. BYOK cloud         — Anthropic / OpenAI / OpenRouter / Azure OpenAI.
//   4. MCP-client mode    — Bowire as MCP client, routes through user's host.
//
// This file is Phase 1: a deterministic rule engine that runs against the
// workbench's live state (selected method, last response, recordings list,
// protocol) and surfaces actionable hints in the AI panel. No network call,
// no model dependency, always available. The panel slot also exposes a
// discreet "Connect a model →" affordance pointing at Settings → AI, which
// is the entry point Phases 2-4 will fill out without changing this layer.

    // ---------- hint catalogue ----------
    // Each hint:
    //   id       — stable identifier (for filtering / dismiss-once later).
    //   level    — 'info' | 'warn' | 'tip'. Cosmetic for now, drives styling.
    //   match    — (ctx) -> bool. ctx is the snapshot built by collectContext().
    //   render   — (ctx) -> string. Plain text, may include the method name.
    //
    // Adding a new hint = appending one object. The rules read only what
    // collectContext() exposes -- so the contract for adding rules is one file.

    var BOWIRE_AI_HINTS = [
        {
            id: 'no-method-selected',
            level: 'info',
            match: function (c) { return !c.method; },
            render: function () {
                return 'Pick a method in the sidebar to see context-aware hints here.';
            }
        },
        {
            id: 'grpc-empty-response',
            level: 'warn',
            match: function (c) {
                return c.method && c.method.protocol === 'grpc' && c.hasResponse
                    && c.responseText !== null && c.responseText.length === 0;
            },
            render: function (c) {
                return 'This gRPC method returned an empty response — check Server Reflection is enabled on '
                    + (c.serverUrl || 'the host')
                    + ' and that the method actually streams / returns a body.';
            }
        },
        {
            id: 'repeat-call-quickaction',
            level: 'tip',
            match: function (c) {
                return c.method && c.callsToThisMethodInLastMinute >= 3;
            },
            render: function (c) {
                return 'You called ' + c.method.name + ' ' + c.callsToThisMethodInLastMinute
                    + ' times in the last minute — save it as a recording step so you can replay it later.';
            }
        },
        {
            id: 'response-extra-fields',
            level: 'warn',
            match: function (c) {
                return c.method && c.method.protocol === 'rest' && c.responseHasUnknownKeys;
            },
            render: function (c) {
                return 'The response carries fields the schema does not declare ('
                    + c.responseUnknownKeys.slice(0, 3).join(', ')
                    + (c.responseUnknownKeys.length > 3 ? ', …' : '')
                    + '). The schema may be stale — re-run discovery against ' + (c.serverUrl || 'the host') + '.';
            }
        },
        {
            id: 'streaming-method-tab-hint',
            level: 'tip',
            match: function (c) {
                return c.method && /Stream|Channel|Subscribe/i.test(c.method.methodType || '');
            },
            render: function () {
                return 'Streaming method — the Response tab shows frames as they arrive. Hit "Record" before invoking to capture every frame for later replay.';
            }
        },
        {
            id: 'mock-replay-context',
            level: 'tip',
            match: function (c) { return c.runningMockCount > 0; },
            render: function (c) {
                return 'You have ' + c.runningMockCount + ' mock'
                    + (c.runningMockCount === 1 ? '' : 's') + ' running — open the Mocks panel to point a second workbench at them and compare responses.';
            }
        },
        {
            id: 'recording-ready-to-mock',
            level: 'tip',
            match: function (c) { return c.recordingsCount > 0 && c.runningMockCount === 0; },
            render: function (c) {
                return 'You have ' + c.recordingsCount + ' recording'
                    + (c.recordingsCount === 1 ? '' : 's') + ' captured — open the Recordings manager and click "Run as mock" to replay them as a live endpoint.';
            }
        },
        {
            id: 'auth-method-no-token',
            level: 'warn',
            match: function (c) {
                return c.hasResponse && c.lastStatusCode === 401;
            },
            render: function () {
                return 'The last call returned 401 — set or refresh the auth helper in the Environment panel before retrying.';
            }
        }
    ];

    // ---------- state collection ----------
    function collectContext() {
        var method = (typeof selectedMethod !== 'undefined') ? selectedMethod : null;
        var service = (typeof selectedService !== 'undefined') ? selectedService : null;
        var lastResponse = (typeof lastResponseJson !== 'undefined') ? lastResponseJson : null;
        var recordings = (typeof recordingsList !== 'undefined') ? recordingsList : [];

        var responseText = '';
        try { responseText = lastResponse == null ? '' : (typeof lastResponse === 'string' ? lastResponse : JSON.stringify(lastResponse)); }
        catch { responseText = ''; }

        // Schema-vs-response known-key set (cheap REST-only check).
        var schemaKeys = [];
        var unknownKeys = [];
        var hasUnknownKeys = false;
        if (method && method.responseSchema && method.responseSchema.properties && lastResponse && typeof lastResponse === 'object') {
            schemaKeys = Object.keys(method.responseSchema.properties);
            unknownKeys = Object.keys(lastResponse).filter(function (k) { return schemaKeys.indexOf(k) === -1; });
            hasUnknownKeys = unknownKeys.length > 0;
        }

        // Call-frequency: walk in-memory call history if available
        var callsRecent = 0;
        try {
            if (Array.isArray(window.__bowireCallHistory) && method) {
                var cutoff = Date.now() - 60 * 1000;
                callsRecent = window.__bowireCallHistory.filter(function (entry) {
                    return entry && entry.method === method.name && entry.timestamp >= cutoff;
                }).length;
            }
        } catch { /* ignore */ }

        var runningMockCount = 0;
        try {
            if (window.__bowireMocks) {
                runningMockCount = (window.__bowireMocks.list() || []).length;
            }
        } catch { /* ignore */ }

        return {
            method: method,
            service: service,
            serverUrl: method && method.serverUrl ? method.serverUrl : null,
            hasResponse: lastResponse !== null && lastResponse !== undefined,
            responseText: responseText,
            lastStatusCode: (typeof lastResponseStatusCode !== 'undefined') ? lastResponseStatusCode : null,
            responseHasUnknownKeys: hasUnknownKeys,
            responseUnknownKeys: unknownKeys,
            callsToThisMethodInLastMinute: callsRecent,
            runningMockCount: runningMockCount,
            recordingsCount: recordings.length
        };
    }

    function evaluateHints() {
        var ctx = collectContext();
        var fired = [];
        for (var i = 0; i < BOWIRE_AI_HINTS.length; i++) {
            var h = BOWIRE_AI_HINTS[i];
            try {
                if (h.match(ctx)) {
                    fired.push({ id: h.id, level: h.level, text: h.render(ctx) });
                }
            } catch { /* hint may throw on unexpected state shape — ignore */ }
        }
        return fired;
    }

    // ---------- panel ----------
    function renderAiPanel() {
        var panel = el('div', { className: 'bowire-ai-panel' });

        // Header
        var header = el('div', { className: 'bowire-ai-header' });
        header.appendChild(el('h3', { textContent: 'AI side-panel' }));
        header.appendChild(el('span', {
            className: 'bowire-ai-mode',
            textContent: 'Hint engine — no model required'
        }));
        panel.appendChild(header);

        // Hints
        var hints = evaluateHints();
        if (hints.length === 0) {
            panel.appendChild(el('p', {
                className: 'bowire-ai-empty',
                textContent: 'No hints fire from the current workbench state. Pick a method, fire a request, or open a recording — the panel surfaces context-aware suggestions as you go.'
            }));
        } else {
            var list = el('ul', { className: 'bowire-ai-hint-list' });
            hints.forEach(function (h) {
                list.appendChild(el('li', {
                    className: 'bowire-ai-hint bowire-ai-hint-' + h.level
                },
                    el('span', { className: 'bowire-ai-hint-icon', textContent: h.level === 'warn' ? '⚠' : h.level === 'tip' ? '💡' : 'ℹ' }),
                    el('span', { className: 'bowire-ai-hint-text', textContent: h.text })
                ));
            });
            panel.appendChild(list);
        }

        // Connect-a-model footer — placeholder for Phases 2-4 hookup
        var footer = el('div', { className: 'bowire-ai-footer' });
        footer.appendChild(el('a', {
            href: '#',
            className: 'bowire-ai-model-link',
            textContent: 'Connect a model → (coming in Phase 2)',
            title: 'Once Phase 2 ships, this opens Settings → AI to connect Ollama / LM Studio / a cloud provider.',
            onClick: function (e) {
                e.preventDefault();
                toast('AI model connection lands in Phase 2 (#25) — local Ollama / LM Studio auto-detect plus BYOK cloud.', 'info');
            }
        }));
        panel.appendChild(footer);

        return panel;
    }

    // Expose for render-main.js
    window.__bowireAi = {
        renderPanel: renderAiPanel,
        evaluateHints: evaluateHints,
        hintCount: function () { return evaluateHints().length; }
    };
