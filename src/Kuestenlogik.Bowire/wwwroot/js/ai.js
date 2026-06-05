// ai.js — AI side-panel: deterministic hint engine (Phase 1) +
// IChatClient-backed local chat (Phase 2).
//
// The ADR (docs/architecture/ai-integration.md) lays out four phases:
//
//   1. Hint engine        — no LLM, rule-based suggestions from live UI state.
//   2. Local-model mode   — Ollama / LM Studio auto-detect + Mode-2 features.
//   3. BYOK cloud         — Anthropic / OpenAI / OpenRouter / Azure OpenAI.
//   4. MCP-client mode    — Bowire as MCP client, routes through user's host.
//
// Phase 1 stays untouched: rule-based hints that need no network. Phase 2
// adds three endpoint calls fronted by the optional Kuestenlogik.Bowire.Ai
// package:
//
//   GET  /api/ai/probe-local    — 300ms probe of Ollama / LM Studio
//   GET  /api/ai/status         — does the host have an IChatClient?
//   POST /api/ai/chat           — proxy a chat completion
//
// When the package isn't installed every call returns 404 / 503 and the
// panel falls back to the Phase-1 hint engine + a docs link — the host
// stays usable. When it is installed, the footer flips to a live status
// line and a tiny chat composer renders under the hints.

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

    // ---------- Phase 2 status + chat plumbing ----------
    // Cached so a repaint doesn't re-fetch on every keystroke. Refreshed
    // on demand via window.__bowireAi.refreshStatus().
    var aiStatus = null;        // { hasClient, providerId, endpoint, model, autoDetectLocal } | null
    var aiProbe = null;         // { ollama: { endpoint, provider, models[] }|null, lmstudio: {...}|null } | null
    var aiInflightStatus = null;
    var aiInflightProbe = null;
    var chatHistory = [];       // [{ role: 'user'|'assistant', content: '...' }]
    var chatBusy = false;

    function aiPrefix() {
        // Match mocks.js / api.js: every endpoint lives under the same
        // base path as the workbench HTML mount.
        return (typeof config !== 'undefined' && config && config.prefix) ? config.prefix : '';
    }

    function refreshAiStatus() {
        if (aiInflightStatus) return aiInflightStatus;
        aiInflightStatus = fetch(aiPrefix() + '/api/ai/status')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { aiStatus = data; return data; })
            .catch(function () { aiStatus = null; return null; })
            .finally(function () { aiInflightStatus = null; });
        return aiInflightStatus;
    }

    function refreshAiProbe() {
        if (aiInflightProbe) return aiInflightProbe;
        aiInflightProbe = fetch(aiPrefix() + '/api/ai/probe-local')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { aiProbe = data; return data; })
            .catch(function () { aiProbe = null; return null; })
            .finally(function () { aiInflightProbe = null; });
        return aiInflightProbe;
    }

    function sendChat(text) {
        if (!text || chatBusy) return Promise.resolve(null);
        chatBusy = true;
        chatHistory.push({ role: 'user', content: text });
        return fetch(aiPrefix() + '/api/ai/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ messages: chatHistory })
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .then(function (resp) {
                if (resp.ok && resp.body && typeof resp.body.content === 'string') {
                    chatHistory.push({ role: 'assistant', content: resp.body.content });
                } else {
                    chatHistory.push({ role: 'assistant', content: '⚠ ' + ((resp.body && resp.body.error) || 'AI request failed (HTTP ' + resp.status + ').') });
                }
            })
            .catch(function (err) {
                chatHistory.push({ role: 'assistant', content: '⚠ Network error: ' + (err && err.message ? err.message : err) });
            })
            .finally(function () { chatBusy = false; });
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

        // Phase 2 chat surface. Renders only when /api/ai/status reports
        // hasClient=true; otherwise the footer becomes a probe-driven
        // "Detected: <provider>" affordance plus a link to install the
        // Kuestenlogik.Bowire.Ai package.
        var chatHost = el('div', { className: 'bowire-ai-chat-host' });
        panel.appendChild(chatHost);

        function rerenderChat() {
            chatHost.replaceChildren();
            if (!aiStatus || !aiStatus.hasClient) return;

            var transcript = el('div', { className: 'bowire-ai-chat-transcript' });
            chatHistory.forEach(function (m) {
                transcript.appendChild(el('div', {
                    className: 'bowire-ai-chat-msg bowire-ai-chat-msg-' + m.role,
                    textContent: (m.role === 'user' ? 'You: ' : 'AI: ') + m.content
                }));
            });
            chatHost.appendChild(transcript);

            var form = el('form', { className: 'bowire-ai-chat-form' });
            var input = el('textarea', {
                className: 'bowire-ai-chat-input',
                placeholder: 'Ask the model — Bowire ships the workbench context as a system prompt.',
                rows: 2
            });
            var send = el('button', {
                type: 'submit',
                className: 'bowire-ai-chat-send',
                textContent: chatBusy ? 'Sending…' : 'Send'
            });
            if (chatBusy) send.setAttribute('disabled', 'disabled');
            form.appendChild(input);
            form.appendChild(send);
            form.onsubmit = function (e) {
                e.preventDefault();
                var text = (input.value || '').trim();
                if (!text || chatBusy) return;
                input.value = '';
                sendChat(text).then(rerenderChat);
                rerenderChat();
            };
            chatHost.appendChild(form);
        }
        rerenderChat();

        var footer = el('div', { className: 'bowire-ai-footer' });
        function rerenderFooter() {
            footer.replaceChildren();
            if (aiStatus && aiStatus.hasClient) {
                footer.appendChild(el('span', {
                    className: 'bowire-ai-status bowire-ai-status-live',
                    textContent: 'Connected: ' + (aiStatus.providerId || 'unknown') + ' · ' + (aiStatus.model || '(default model)')
                }));
                return;
            }
            // No IChatClient registered. Two sub-cases:
            //   (a) The Kuestenlogik.Bowire.Ai package isn't installed
            //       (status endpoint 404'd → aiStatus === null).
            //   (b) Package is installed but no provider connected yet
            //       (status 200 with hasClient=false).
            var pkgMissing = aiStatus === null;
            footer.appendChild(el('span', {
                className: 'bowire-ai-status bowire-ai-status-idle',
                textContent: pkgMissing
                    ? 'No AI provider — install Kuestenlogik.Bowire.Ai to enable chat (Ollama / LM Studio / BYOK cloud).'
                    : 'AI package installed, no model connected.'
            }));
            // Probe-driven detection: surfaces the running Ollama /
            // LM-Studio instance on the same host so the user gets a
            // one-line "detected llama3.2:3b" affordance.
            if (aiProbe) {
                ['ollama', 'lmstudio'].forEach(function (key) {
                    var hit = aiProbe[key];
                    if (!hit || !hit.endpoint) return;
                    var models = Array.isArray(hit.models) ? hit.models.filter(function (m) { return !!m; }) : [];
                    var preview = models.length > 0 ? models.slice(0, 3).join(', ') + (models.length > 3 ? ', …' : '') : '(no models loaded)';
                    footer.appendChild(el('div', {
                        className: 'bowire-ai-detected',
                        textContent: 'Detected ' + hit.provider + ' at ' + hit.endpoint + ' — ' + preview
                    }));
                });
            }
        }
        rerenderFooter();
        panel.appendChild(footer);

        // Kick the status + probe fetches on first paint; once they land
        // the panel re-renders its footer + chat host so the user sees the
        // live state without manual refresh. Both calls are idempotent +
        // cheap so re-running on every panel paint is fine.
        refreshAiStatus().then(function () {
            rerenderFooter();
            rerenderChat();
        });
        // Probe is opt-in via the AutoDetectLocal flag; the endpoint
        // short-circuits and returns { skipped } when disabled, so the
        // fetch stays cheap even then.
        refreshAiProbe().then(rerenderFooter);

        return panel;
    }

    // Expose for render-main.js
    window.__bowireAi = {
        renderPanel: renderAiPanel,
        evaluateHints: evaluateHints,
        hintCount: function () { return evaluateHints().length; },
        // Phase 2 surface. Lets render layers (and tests) read or reset
        // the status / probe / chat cache without touching internals.
        getStatus: function () { return aiStatus; },
        getProbe: function () { return aiProbe; },
        refreshStatus: refreshAiStatus,
        refreshProbe: refreshAiProbe,
        resetChat: function () { chatHistory.length = 0; }
    };
