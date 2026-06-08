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
    // #109 — Phase 3 gate: when true, the chat request marks
    // context.allowInvoke = true and the backend registers the
    // bowire_invoke tool. When false (default), the tool isn't even
    // offered to the model. Session-only — NEVER persisted to
    // localStorage; the user must opt in each fresh session.
    var aiAllowInvoke = false;
    // UX progress feedback for in-flight chat requests. Local models
    // doing tool calls take 30-45 s with llama3.2:3b — without a
    // visible "still thinking" indicator the user assumes the request
    // hung. chatPendingSince is the ms-timestamp the request fired;
    // chatAbort holds the AbortController so the Cancel button can
    // tear it down; chatTickHandle is the 1 s setInterval that
    // re-renders the elapsed-seconds counter. All three live as a
    // single state group — set together in sendChat, cleared together
    // in the .finally.
    var chatPendingSince = null;
    var chatAbort = null;
    var chatTickHandle = null;

    // #59 Threat-model state. Populated by runThreatModel(); rendered
    // by rerenderThreatModel(). Endpoint index lets per-row buttons
    // look up the original endpoint payload by its id so we don't
    // have to round-trip through the model's verbatim string.
    var threatState = {
        running: false,
        lastRun: 0,
        lastInputCount: 0,
        truncated: false,
        modelId: null,
        ranked: null,        // [{ endpointId, risk, why, suggestedTemplates }]
        endpointIndex: {},   // { endpointId: { path, verb, protocol, serverUrl, ... } }
        error: null
    };

    function collectThreatEndpoints() {
        // Walks the workbench's discovered service surface and emits a
        // flat list of endpoints with enough context for the model to
        // rank by risk. Source of truth is `services` (set by api.js
        // after /api/services), each service carries `methods`.
        var endpoints = [];
        var index = {};
        var src = (typeof services !== 'undefined') ? services : [];
        for (var i = 0; i < src.length; i++) {
            var svc = src[i];
            if (!svc || !Array.isArray(svc.methods)) continue;
            for (var j = 0; j < svc.methods.length; j++) {
                var m = svc.methods[j];
                if (!m) continue;
                var id = 's' + i + 'm' + j;
                var path = m.httpPath || m.name || (svc.name + '/' + (m.name || j));
                var verb = m.httpVerb || (m.methodType || null);
                var protocol = svc.source || svc.protocol || null;
                var endpoint = {
                    endpointId: id,
                    path: path,
                    verb: verb,
                    protocol: protocol,
                    service: svc.name || null,
                    serverUrl: m.serverUrl || svc.originUrl || null,
                    inputShape: m.inputType && m.inputType.name ? m.inputType.name : null,
                    authState: null
                };
                endpoints.push(endpoint);
                index[id] = endpoint;
            }
        }
        return { endpoints: endpoints, index: index };
    }

    // #60 — Nuclei template suggestion plumbing. Same class enum as
    // the server endpoint's KnownTemplateClasses; mirror by hand so
    // the frontend doesn't need a separate /api/ai/template-classes
    // round-trip.
    var BOWIRE_AI_TEMPLATE_CLASSES = [
        'auth-bypass', 'idor', 'mass-assignment', 'parameter-tampering',
        'injection-sqli', 'injection-cmdi', 'injection-template',
        'ssrf', 'path-traversal', 'open-redirect'
    ];

    function bowireBuildTemplateGenerator(endpoint, defaultClass) {
        var wrap = el('div', { className: 'bowire-ai-template-trigger' });
        var classSelect = el('select', { className: 'bowire-ai-template-class' });
        BOWIRE_AI_TEMPLATE_CLASSES.forEach(function (c) {
            var opt = el('option', { value: c, textContent: c });
            if (c === defaultClass) opt.selected = true;
            classSelect.appendChild(opt);
        });
        var genBtn = el('button', {
            type: 'button',
            className: 'bowire-ai-template-gen-btn',
            textContent: 'Generate template',
            onClick: function () {
                var cls = classSelect.value;
                var outputId = 'bowire-ai-template-out-' + endpoint.endpointId;
                var output = document.getElementById(outputId);
                if (!output) return;
                output.style.display = 'block';
                output.replaceChildren();
                output.appendChild(el('div', {
                    className: 'bowire-ai-template-status',
                    textContent: 'Generating ' + cls + ' template…'
                }));
                genBtn.setAttribute('disabled', 'disabled');
                bowireGenerateTemplate(endpoint, cls)
                    .then(function (result) { bowireRenderTemplateOutput(output, result); })
                    .finally(function () { genBtn.removeAttribute('disabled'); });
            }
        });
        wrap.appendChild(classSelect);
        wrap.appendChild(genBtn);
        return wrap;
    }

    function bowireGenerateTemplate(endpoint, cls) {
        var payload = {
            path: endpoint.path,
            'class': cls,
            verb: endpoint.verb || null,
            protocol: endpoint.protocol || null,
            service: endpoint.service || null,
            inputShape: endpoint.inputShape || null,
            authState: endpoint.authState || null
        };
        return fetch(aiPrefix() + '/api/ai/template-suggest', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .catch(function (err) {
                return { ok: false, status: 0, body: { error: 'Network error: ' + (err && err.message ? err.message : err) } };
            });
    }

    function bowireRenderTemplateOutput(host, result) {
        host.replaceChildren();
        if (!result.ok) {
            host.appendChild(el('div', {
                className: 'bowire-ai-template-error',
                textContent: '⚠ ' + ((result.body && result.body.error) || 'request failed')
            }));
            return;
        }
        var pre = el('pre', { className: 'bowire-ai-template-yaml' });
        pre.appendChild(el('code', { textContent: result.body.yaml || '(empty)' }));
        host.appendChild(pre);

        var actions = el('div', { className: 'bowire-ai-template-actions' });
        var copyBtn = el('button', {
            type: 'button',
            className: 'bowire-ai-template-copy',
            textContent: 'Copy YAML',
            onClick: function () {
                if (!navigator.clipboard) return;
                navigator.clipboard.writeText(result.body.yaml).then(function () {
                    copyBtn.textContent = 'Copied';
                    setTimeout(function () { copyBtn.textContent = 'Copy YAML'; }, 1500);
                });
            }
        });
        var saveBtn = el('button', {
            type: 'button',
            className: 'bowire-ai-template-save',
            textContent: 'Save to ~/.bowire/templates',
            onClick: function () {
                saveBtn.setAttribute('disabled', 'disabled');
                saveBtn.textContent = 'Saving…';
                fetch(aiPrefix() + '/api/ai/template-save', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        filename: result.body.suggestedFilename,
                        yaml: result.body.yaml
                    })
                })
                    .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
                    .then(function (resp) {
                        saveBtn.removeAttribute('disabled');
                        if (resp.ok) {
                            saveBtn.textContent = 'Saved ' + (resp.body.path || '');
                            setTimeout(function () {
                                saveBtn.textContent = 'Save to ~/.bowire/templates';
                            }, 3000);
                        } else {
                            saveBtn.textContent = '⚠ ' + (resp.body.error || 'save failed');
                        }
                    })
                    .catch(function (err) {
                        saveBtn.removeAttribute('disabled');
                        saveBtn.textContent = '⚠ ' + (err && err.message ? err.message : 'save failed');
                    });
            }
        });
        actions.appendChild(copyBtn);
        actions.appendChild(saveBtn);
        host.appendChild(actions);

        if (result.body.modelId) {
            host.appendChild(el('div', {
                className: 'bowire-ai-template-meta',
                textContent: 'via ' + result.body.modelId + ' · suggested filename: ' + (result.body.suggestedFilename || '(none)')
            }));
        }
    }

    // Tick handle for the threat-model "Ranking… (Ns)" counter.
    // Same pattern as chatTickHandle — re-renders the threat section
    // once per second while a request is in flight so the user can
    // see elapsed time. Local-model threat ranking takes 10-30 s;
    // without a counter the button just sits at "Ranking…" and looks
    // hung.
    var threatTickHandle = null;
    function runThreatModel() {
        if (threatState.running) return Promise.resolve(threatState);
        var collected = collectThreatEndpoints();
        if (collected.endpoints.length === 0) {
            threatState.error = 'No discovered endpoints to rank yet. Connect to a server first.';
            return Promise.resolve(threatState);
        }
        threatState.running = true;
        threatState.startedAt = Date.now();
        threatState.error = null;
        threatState.endpointIndex = collected.index;
        if (threatTickHandle) clearInterval(threatTickHandle);
        threatTickHandle = setInterval(function () {
            try { if (rerenderThreatModelExternal) rerenderThreatModelExternal(); } catch { /* harmless */ }
        }, 1000);
        return fetch(aiPrefix() + '/api/ai/threat-model', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                endpoints: collected.endpoints,
                topN: 10
            })
        })
            .then(function (resp) { return resp.json().then(function (b) { return { ok: resp.ok, status: resp.status, body: b }; }); })
            .then(function (resp) {
                threatState.running = false;
                if (threatTickHandle) { clearInterval(threatTickHandle); threatTickHandle = null; }
                if (!resp.ok) {
                    threatState.error = (resp.body && resp.body.error) || ('Request failed (HTTP ' + resp.status + ').');
                    threatState.ranked = null;
                    return threatState;
                }
                threatState.lastRun = Date.now();
                threatState.lastInputCount = resp.body.inputCount || collected.endpoints.length;
                threatState.truncated = !!resp.body.truncated;
                threatState.modelId = resp.body.modelId || null;
                threatState.ranked = Array.isArray(resp.body.ranked) ? resp.body.ranked : [];
                return threatState;
            })
            .catch(function (err) {
                threatState.running = false;
                if (threatTickHandle) { clearInterval(threatTickHandle); threatTickHandle = null; }
                threatState.error = 'Network error: ' + (err && err.message ? err.message : err);
                threatState.ranked = null;
                return threatState;
            });
    }

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

    /**
     * Snapshot the live workbench state so the AI's system prompt can
     * ground the model in what the user is actually looking at (#89
     * Phase 1). Pure read of the existing closure-scoped globals —
     * no extra fetches.
     *
     * Output is small on purpose: a small model needs concise context
     * more than a complete API surface dump. Top-level service +
     * method counts always; the selected method's full schema only
     * when one is picked (that's where the user's attention is).
     */
    function collectWorkbenchContext() {
        var urlState = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls))
            ? serverUrls.map(function (u) {
                var status = (typeof connectionStatuses !== 'undefined' && connectionStatuses)
                    ? (connectionStatuses[u] || 'unknown') : 'unknown';
                return { url: u, status: status };
            }) : [];

        var svcSummary = [];
        if (typeof services !== 'undefined' && Array.isArray(services)) {
            for (var i = 0; i < services.length; i++) {
                var s = services[i];
                svcSummary.push({
                    name: s.name,
                    protocol: s.source,
                    methods: (s.methods || []).map(function (m) { return m.name; }),
                });
            }
        }

        var selected = null;
        if (typeof selectedService !== 'undefined' && selectedService
            && typeof selectedMethod !== 'undefined' && selectedMethod) {
            selected = {
                service: selectedService.name,
                method: selectedMethod.name,
                protocol: selectedService.source,
                methodType: selectedMethod.methodType,
                description: selectedMethod.description,
                inputType: selectedMethod.inputType ? {
                    name: selectedMethod.inputType.fullName || selectedMethod.inputType.name,
                    fields: (selectedMethod.inputType.fields || []).map(function (f) {
                        return { name: f.name, type: f.type, optional: !!f.optional };
                    }),
                } : null,
                outputType: selectedMethod.outputType ? {
                    name: selectedMethod.outputType.fullName || selectedMethod.outputType.name,
                } : null,
                originUrl: selectedService.originUrl,
            };
        }

        var recent = [];
        try {
            if (typeof getHistory === 'function') {
                var h = getHistory() || [];
                for (var ri = 0; ri < Math.min(h.length, 5); ri++) {
                    var e = h[ri];
                    recent.push({
                        service: e.service,
                        method: e.method,
                        status: e.status,
                        // Drop body excerpts — they're often large + privacy-sensitive.
                    });
                }
            }
        } catch { /* defensive */ }

        return {
            urls: urlState,
            services: svcSummary,
            selected: selected,
            recent: recent,
            ai: aiStatus ? { model: aiStatus.model, provider: aiStatus.providerId, endpoint: aiStatus.endpoint } : null,
        };
    }

    /**
     * Project the JS-side workbench-context snapshot down to the shape
     * the backend's WorkbenchContext record (BowireAiEndpoints.cs)
     * deserialises. Mostly a 1:1 mapping; renames a few field names
     * to match C# casing conventions.
     */
    function serializeContextForBackend(ctx) {
        if (!ctx) return null;
        return {
            allowInvoke: aiAllowInvoke,
            serverUrls: (ctx.urls || []).map(function (u) { return u.url; }),
            services: (ctx.services || []).map(function (s) {
                return {
                    name: s.name,
                    protocol: s.protocol,
                    originUrl: ctx.selected && ctx.selected.service === s.name ? ctx.selected.originUrl : null,
                    methods: (s.methods || []).map(function (mName) {
                        // For the selected method we have full schema in
                        // ctx.selected; for everything else we only have
                        // the name. Surface what we have so the model can
                        // at least enumerate, and tools (describe_method)
                        // return "more detail not in snapshot" when asked
                        // about a non-selected method.
                        if (ctx.selected && ctx.selected.service === s.name && ctx.selected.method === mName) {
                            var sel = ctx.selected;
                            return {
                                name: mName,
                                description: sel.description,
                                methodType: sel.methodType,
                                inputTypeName: sel.inputType ? sel.inputType.name : null,
                                inputFields: sel.inputType ? sel.inputType.fields : null,
                                outputTypeName: sel.outputType ? sel.outputType.name : null,
                            };
                        }
                        return { name: mName };
                    }),
                };
            }),
            recent: (ctx.recent || []).map(function (r) {
                return { service: r.service, method: r.method, status: r.status };
            }),
        };
    }

    /**
     * Build a system prompt from a workbench-context snapshot. The
     * prompt tells the model what Bowire is, names the loaded
     * services + the selected method, and asks the model to answer
     * with that context rather than going general-knowledge.
     *
     * Returns null when the context is genuinely empty (no URLs, no
     * services, no method selected) — at that point the model has
     * nothing workbench-specific to ground in and a generic system
     * prompt just costs tokens.
     */
    function buildSystemPrompt(ctx) {
        if (!ctx) return null;
        var hasAnything = (ctx.urls && ctx.urls.length)
            || (ctx.services && ctx.services.length)
            || ctx.selected
            || (ctx.recent && ctx.recent.length);
        if (!hasAnything) return null;

        var lines = [
            'You are the AI assistant embedded in Bowire, a multi-protocol API workbench (gRPC, REST, GraphQL, MQTT, SignalR, WebSocket, MCP, …). The user is currently testing APIs through Bowire. Ground your answers in what they have loaded; do not invent endpoints or methods that are not listed below. When the user asks about a method by name (case-insensitive, prefix-match), refer to the matching entry from the list. Suggest the next concrete action (which method to invoke, which header to add, which auth to configure) rather than generic web advice.',
            '',
            '## Workbench state',
            ''
        ];

        if (ctx.urls && ctx.urls.length) {
            lines.push('Connected URLs:');
            for (var ui = 0; ui < ctx.urls.length; ui++) {
                lines.push('  - ' + ctx.urls[ui].url + '  (' + ctx.urls[ui].status + ')');
            }
            lines.push('');
        }

        if (ctx.services && ctx.services.length) {
            lines.push('Discovered services:');
            for (var si = 0; si < ctx.services.length; si++) {
                var svc = ctx.services[si];
                lines.push('  - ' + svc.name + '  [' + svc.protocol + ']  — methods: ' + svc.methods.join(', '));
            }
            lines.push('');
        }

        if (ctx.selected) {
            var sel = ctx.selected;
            lines.push('Selected method (this is what the user is looking at right now):');
            lines.push('  ' + sel.service + '.' + sel.method + '  [' + sel.protocol + (sel.methodType ? ', ' + sel.methodType : '') + ']');
            if (sel.description) lines.push('  Description: ' + sel.description);
            if (sel.inputType) {
                lines.push('  Input (' + sel.inputType.name + '):');
                for (var fi = 0; fi < sel.inputType.fields.length; fi++) {
                    var f = sel.inputType.fields[fi];
                    lines.push('    - ' + f.name + ': ' + f.type + (f.optional ? '  (optional)' : ''));
                }
            }
            if (sel.outputType) lines.push('  Output: ' + sel.outputType.name);
            if (sel.originUrl) lines.push('  Origin URL: ' + sel.originUrl);
            lines.push('');
        }

        if (ctx.recent && ctx.recent.length) {
            lines.push('Recent calls:');
            for (var ki = 0; ki < ctx.recent.length; ki++) {
                var r = ctx.recent[ki];
                lines.push('  - ' + r.service + '.' + r.method + '  → ' + (r.status || 'unknown'));
            }
            lines.push('');
        }

        if (ctx.ai) {
            lines.push('AI provider: ' + (ctx.ai.provider || 'unknown') + ', model: ' + (ctx.ai.model || 'unknown') + ', endpoint: ' + (ctx.ai.endpoint || 'unknown'));
        }

        return lines.join('\n');
    }

    function sendChat(text) {
        if (!text || chatBusy) return Promise.resolve(null);
        chatBusy = true;
        chatPendingSince = Date.now();
        chatAbort = new AbortController();
        // Re-render once per second so the elapsed-time counter on the
        // "Thinking…" bubble visibly ticks. setInterval is fine here
        // — the only cost is one DOM diff per tick and the chat panel
        // is small. Cleared in .finally.
        chatTickHandle = setInterval(function () {
            try { rerenderChatExternal(); } catch { /* harmless */ }
        }, 1000);
        chatHistory.push({ role: 'user', content: text });
        // Two grounding paths every turn (#89 Phase 1 + Phase 2):
        //  - System prompt with a workbench overview (loaded URLs +
        //    service names + selected method's schema). Catches every
        //    question that doesn't need a tool call.
        //  - The same snapshot serialised as `context` in the request
        //    body so the backend's MCP-style tools (#108) can read it
        //    on demand without burning tokens to keep huge schemas in
        //    the prompt itself.
        // Both rebuild every turn — the workbench state moves, the
        // model should see the snapshot that matches the question.
        var wbCtx = collectWorkbenchContext();
        var sysPrompt = buildSystemPrompt(wbCtx);
        var outgoing = sysPrompt
            ? [{ role: 'system', content: sysPrompt }].concat(chatHistory.filter(function (m) {
                // Skip transient assistant-side problem renders + tool-call
                // bubbles — they're UI artifacts, not conversation turns.
                return !m.problem && !m.toolCalls;
            }))
            : chatHistory.filter(function (m) { return !m.problem && !m.toolCalls; });
        // Backend-facing shape — strip the UI's role names down to the
        // {role, content} pair the endpoint deserialises. Drop assistant
        // entries that only carried a problem card (no text content).
        outgoing = outgoing.map(function (m) {
            return { role: m.role, content: m.content || '' };
        });
        return fetch(aiPrefix() + '/api/ai/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            signal: chatAbort.signal,
            body: JSON.stringify({
                messages: outgoing,
                context: serializeContextForBackend(wbCtx),
            })
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .then(function (resp) {
                if (resp.ok && resp.body && typeof resp.body.content === 'string') {
                    // Tool calls (#108 Phase 2) — show before the
                    // assistant text so the transcript reads "AI looked
                    // up X, then said Y". Pushed as their own bubble
                    // so the transcript filter skips them on the next
                    // turn (no point re-grounding via prior tool calls).
                    if (Array.isArray(resp.body.toolCalls) && resp.body.toolCalls.length > 0) {
                        chatHistory.push({
                            role: 'assistant',
                            toolCalls: resp.body.toolCalls,
                        });
                        // #109 Phase 4 — dispatch on tool name. The
                        // open_method tool's server-side body just
                        // validates the request; the actual navigation
                        // is client-side here. Looking at the tool name
                        // (not a side-channel) keeps the protocol
                        // dumb-pipe: the same response shape the AI
                        // returned drives the UI.
                        resp.body.toolCalls.forEach(function (tc) {
                            if (tc.name === 'bowire_open_method'
                                && tc.arguments && tc.arguments.service && tc.arguments.method
                                && typeof services !== 'undefined' && typeof openTab === 'function') {
                                var svc = services.find(function (s) {
                                    return s.name && s.name.toLowerCase() === tc.arguments.service.toLowerCase();
                                });
                                if (!svc || !svc.methods) return;
                                var m = svc.methods.find(function (x) {
                                    return x.name && x.name.toLowerCase() === tc.arguments.method.toLowerCase();
                                });
                                if (m) {
                                    try { openTab(svc, m); render(); } catch { /* ignore */ }
                                }
                            }
                        });
                    }
                    chatHistory.push({ role: 'assistant', content: resp.body.content });
                } else {
                    // Server returned a problem+json (or legacy { error }).
                    // Carry the normalized problem so the renderer can
                    // surface the structured form (title + details +
                    // links). Fallback to text when the body is empty
                    // (network blip — caught case below should be hit
                    // instead but be defensive).
                    var problem = normalizeProblem(resp.body) || normalizeProblem('AI request failed (HTTP ' + resp.status + ').');
                    chatHistory.push({ role: 'assistant', problem: problem });
                }
            })
            .catch(function (err) {
                // AbortError is the user clicking Cancel — surface as
                // a quiet "canceled" line instead of a Network-error
                // problem card; the cancel was deliberate.
                if (err && err.name === 'AbortError') {
                    chatHistory.push({
                        role: 'assistant',
                        content: '(canceled)',
                    });
                } else {
                    chatHistory.push({
                        role: 'assistant',
                        problem: normalizeProblem('Network error: ' + (err && err.message ? err.message : err))
                    });
                }
            })
            .finally(function () {
                chatBusy = false;
                chatPendingSince = null;
                chatAbort = null;
                if (chatTickHandle) {
                    clearInterval(chatTickHandle);
                    chatTickHandle = null;
                }
            });
    }

    // Module-scope hooks the per-second tick closures call into.
    // Assigned by the matching rerenderX() function inside
    // renderAiPanel() — that's where the host DOM lives. Lets the
    // setInterval ticks refresh the pending/elapsed UI without
    // re-running the whole renderAiPanel.
    var rerenderChatExternal = function () { /* no-op until panel mounted */ };
    var rerenderThreatModelExternal = function () { /* no-op until panel mounted */ };

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

        // #59 Threat-model surface. Sits between the hint list and the
        // chat composer so the "rank my whole service surface" button
        // is the first AI action a user sees after the hints. Only
        // renders when hasClient=true.
        var threatHost = el('div', { className: 'bowire-ai-threat-host' });
        panel.appendChild(threatHost);

        function rerenderThreatModel() {
            threatHost.replaceChildren();
            if (!aiStatus || !aiStatus.hasClient) return;

            var section = el('div', { className: 'bowire-ai-threat-section' });
            section.appendChild(el('h4', { className: 'bowire-ai-threat-title', textContent: 'Threat model' }));
            section.appendChild(el('p', {
                className: 'bowire-ai-threat-help',
                textContent: 'Rank discovered endpoints by attack-surface risk. The model proposes — you confirm before scanning.'
            }));

            var runRow = el('div', { className: 'bowire-ai-threat-runrow' });
            // While running, surface elapsed seconds in the button text
            // so the user sees the request is alive (local-model
            // threat-model takes 10-30 s with llama3.2:1b). Driven by
            // the threatTickHandle setInterval that re-renders this
            // section once per second; the seconds counter ticks
            // visibly.
            var elapsedThreatSec = threatState.running && threatState.startedAt
                ? Math.max(0, Math.floor((Date.now() - threatState.startedAt) / 1000))
                : 0;
            var runBtn = el('button', {
                type: 'button',
                className: 'bowire-ai-threat-run',
                textContent: threatState.running
                    ? 'Ranking… (' + elapsedThreatSec + 's)'
                    : 'Run threat model',
                onClick: function () { runThreatModel().then(rerenderThreatModel); }
            });
            if (threatState.running) runBtn.setAttribute('disabled', 'disabled');
            runRow.appendChild(runBtn);
            var statusLine = el('span', { className: 'bowire-ai-threat-meta' });
            if (threatState.error) {
                statusLine.textContent = '⚠ ' + threatState.error;
                statusLine.classList.add('err');
            } else if (threatState.lastRun) {
                statusLine.textContent = threatState.lastInputCount + ' endpoint(s) considered'
                    + (threatState.truncated ? ' (truncated to first 200)' : '')
                    + (threatState.modelId ? ' · ' + threatState.modelId : '');
            }
            runRow.appendChild(statusLine);
            section.appendChild(runRow);

            if (threatState.ranked && threatState.ranked.length > 0) {
                var list = el('ol', { className: 'bowire-ai-threat-list' });
                threatState.ranked.forEach(function (row) {
                    var endpoint = threatState.endpointIndex[row.endpointId];
                    var li = el('li', { className: 'bowire-ai-threat-row' });
                    var header = el('div', { className: 'bowire-ai-threat-row-head' });
                    var scoreClass = row.risk >= 8 ? 'high' : row.risk >= 5 ? 'medium' : 'low';
                    header.appendChild(el('span', {
                        className: 'bowire-ai-threat-score score-' + scoreClass,
                        textContent: row.risk + '/10'
                    }));
                    header.appendChild(el('code', {
                        className: 'bowire-ai-threat-endpoint',
                        textContent: endpoint
                            ? ((endpoint.verb ? endpoint.verb + ' ' : '') + (endpoint.path || row.endpointId))
                            : row.endpointId
                    }));
                    li.appendChild(header);
                    if (row.why) {
                        li.appendChild(el('div', { className: 'bowire-ai-threat-why', textContent: row.why }));
                    }
                    if (row.suggestedTemplates && row.suggestedTemplates.length > 0) {
                        var tplWrap = el('div', { className: 'bowire-ai-threat-templates' });
                        tplWrap.appendChild(el('span', {
                            className: 'bowire-ai-threat-templates-label',
                            textContent: 'Suggested:'
                        }));
                        row.suggestedTemplates.forEach(function (t) {
                            tplWrap.appendChild(el('span', {
                                className: 'bowire-ai-threat-template-chip',
                                textContent: t
                            }));
                        });
                        li.appendChild(tplWrap);
                    }
                    // Action row: copy the CLI command (Tier-1
                    // shortcut) + generate a Nuclei template via #60.
                    if (endpoint && endpoint.path) {
                        var actionRow = el('div', { className: 'bowire-ai-threat-scan' });
                        var scanBtn = el('button', {
                            type: 'button',
                            className: 'bowire-ai-threat-scan-btn',
                            textContent: 'Copy bowire scan command',
                            onClick: (function (ep, templates) {
                                return function () {
                                    var cmd = 'bowire scan --url ' + (ep.serverUrl || '<server>')
                                        + ' --path ' + ep.path
                                        + (templates.length > 0 ? ' --templates ' + templates.join(',') : '');
                                    if (navigator.clipboard) {
                                        navigator.clipboard.writeText(cmd).then(function () {
                                            scanBtn.textContent = 'Copied';
                                            setTimeout(function () {
                                                scanBtn.textContent = 'Copy bowire scan command';
                                            }, 1500);
                                        });
                                    }
                                };
                            })(endpoint, row.suggestedTemplates || [])
                        });
                        actionRow.appendChild(scanBtn);
                        // #60 per-row template-generator. Class picker
                        // is pre-populated with the model's first
                        // suggestedTemplate, falls back to 'idor'.
                        var defaultClass = (row.suggestedTemplates && row.suggestedTemplates[0]) || 'idor';
                        actionRow.appendChild(bowireBuildTemplateGenerator(endpoint, defaultClass));
                        li.appendChild(actionRow);
                        // Output box that the generator targets.
                        var outputBox = el('div', { className: 'bowire-ai-template-output' });
                        outputBox.id = 'bowire-ai-template-out-' + endpoint.endpointId;
                        outputBox.style.display = 'none';
                        li.appendChild(outputBox);
                    }
                    list.appendChild(li);
                });
                section.appendChild(list);
            }

            threatHost.appendChild(section);
        }
        rerenderThreatModelExternal = rerenderThreatModel;
        rerenderThreatModel();

        // Phase 2 chat surface. Renders only when /api/ai/status reports
        // hasClient=true; otherwise the footer becomes a probe-driven
        // "Detected: <provider>" affordance plus a link to install the
        // Kuestenlogik.Bowire.Ai package.
        var chatHost = el('div', { className: 'bowire-ai-chat-host' });
        panel.appendChild(chatHost);

        function rerenderChat() {
            chatHost.replaceChildren();
            if (!aiStatus || !aiStatus.hasClient) return;

            // #109 — Phase 3 opt-in gate. Toggle is session-only,
            // never persisted (see aiAllowInvoke comment). When OFF
            // the backend doesn't even register the bowire_invoke
            // tool — the model literally cannot try. When ON the
            // AI can dispatch real calls; every invoke writes to
            // ~/.bowire/.ai-actions.jsonl for audit.
            var invokeRow = el('label', { className: 'bowire-ai-chat-toggle' });
            var invokeBox = el('input', { type: 'checkbox' });
            if (aiAllowInvoke) invokeBox.checked = true;
            invokeBox.addEventListener('change', function () {
                aiAllowInvoke = invokeBox.checked;
                rerenderChat();
            });
            invokeRow.appendChild(invokeBox);
            invokeRow.appendChild(el('span', { textContent: 'Allow AI to invoke methods' }));
            invokeRow.appendChild(el('span', {
                className: 'bowire-ai-chat-toggle-hint',
                textContent: aiAllowInvoke ? '· session only, audited' : '· off — AI may only observe',
            }));
            chatHost.appendChild(invokeRow);

            var transcript = el('div', { className: 'bowire-ai-chat-transcript' });
            chatHistory.forEach(function (m) {
                var bubble = el('div', { className: 'bowire-ai-chat-msg bowire-ai-chat-msg-' + m.role });
                // Structured-error bubble (#88): render the Problem
                // card instead of the plain assistant content. The
                // prefix "AI: " is still useful to anchor the bubble
                // in the conversation.
                if (m.problem) {
                    bubble.appendChild(el('div', { className: 'bowire-ai-chat-prefix', textContent: 'AI:' }));
                    renderProblem(m.problem, bubble);
                } else if (m.toolCalls) {
                    // Tool-call trace bubble (#108 Phase 2). Renders
                    // each tool call as a small "consulted X" line so
                    // the user can see what the AI actually looked up
                    // before answering. Stays collapsed by default —
                    // open a <details> for the raw arguments JSON.
                    bubble.classList.add('bowire-ai-chat-msg-tools');
                    m.toolCalls.forEach(function (tc) {
                        var prettyName = tc.name
                            .replace(/^bowire_/, '')
                            .replace(/_/g, ' ');
                        var line = el('div', { className: 'bowire-ai-chat-tool' });
                        line.appendChild(el('span', { className: 'bowire-ai-chat-tool-icon', textContent: '🔧' }));
                        line.appendChild(el('span', {
                            className: 'bowire-ai-chat-tool-name',
                            textContent: 'Consulted ' + prettyName,
                        }));
                        if (tc.arguments && Object.keys(tc.arguments).length > 0) {
                            var args = el('details', { className: 'bowire-ai-chat-tool-args' });
                            args.appendChild(el('summary', { textContent: 'args' }));
                            args.appendChild(el('pre', { textContent: JSON.stringify(tc.arguments, null, 2) }));
                            line.appendChild(args);
                        }
                        bubble.appendChild(line);
                    });
                } else {
                    bubble.textContent = (m.role === 'user' ? 'You: ' : 'AI: ') + m.content;
                }
                transcript.appendChild(bubble);
            });

            // "Thinking…" bubble while a request is in flight (UX
            // quick-fix). Local models with tool-calling can take
            // 30-45 s on llama3.2:3b — without a visible counter the
            // user assumes the request hung. The bubble shows elapsed
            // seconds (ticking via the 1 s setInterval set in
            // sendChat) and a Cancel button that aborts the fetch.
            if (chatBusy && chatPendingSince) {
                var elapsedSec = Math.max(0, Math.floor((Date.now() - chatPendingSince) / 1000));
                var pendingBubble = el('div', { className: 'bowire-ai-chat-msg bowire-ai-chat-msg-assistant bowire-ai-chat-pending' });
                pendingBubble.appendChild(el('span', { className: 'bowire-ai-chat-pending-dot' }));
                pendingBubble.appendChild(el('span', {
                    className: 'bowire-ai-chat-pending-text',
                    textContent: 'AI: thinking… (' + elapsedSec + 's)',
                }));
                pendingBubble.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-ai-chat-pending-cancel',
                    textContent: 'Cancel',
                    onClick: function () { if (chatAbort) chatAbort.abort(); }
                }));
                transcript.appendChild(pendingBubble);
            }

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
        // Hand the tick interval (sendChat) a stable hook that always
        // re-runs the *current* rerenderChat closure. Reassigned on
        // every renderAiPanel() so a re-mounted drawer doesn't keep
        // calling a stale closure that targets a detached chatHost.
        rerenderChatExternal = rerenderChat;
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
        resetChat: function () { chatHistory.length = 0; },
        // Screenshot-helper seams. Used by scripts/capture-screenshots.js
        // to seed the panel with deterministic demo data without needing
        // a real IChatClient on the marketing CI runner. Prefix `_` so
        // they're clearly out of the supported API surface.
        _seedStatus: function (st) { aiStatus = st; },
        _seedProbe: function (pr) { aiProbe = pr; },
        _seedChat: function (history) {
            chatHistory.length = 0;
            if (Array.isArray(history)) {
                history.forEach(function (m) { chatHistory.push(m); });
            }
        },
        _seedThreatModel: function (state) {
            if (!state) {
                threatState.ranked = null;
                threatState.lastRun = 0;
                return;
            }
            threatState.ranked = state.ranked || [];
            threatState.lastInputCount = state.inputCount || (state.ranked || []).length;
            threatState.truncated = !!state.truncated;
            threatState.modelId = state.modelId || null;
            threatState.endpointIndex = state.endpointIndex || {};
            threatState.lastRun = Date.now();
            threatState.error = null;
        },
        _seedTemplate: function (endpointId, body) {
            var outputId = 'bowire-ai-template-out-' + endpointId;
            var output = document.getElementById(outputId);
            if (!output) return false;
            output.style.display = 'block';
            bowireRenderTemplateOutput(output, { ok: true, body: body });
            return true;
        }
    };
